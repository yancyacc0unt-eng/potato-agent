using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 工具调用循环：模型要工具 → 查 <see cref="ToolRegistry"/> → 执行 → 把结果当 <c>role: tool</c> 回灌 → 继续问，
/// 直到模型不再要工具为止。
/// </summary>
/// <remarks>
/// <para>一轮对话的消息流水（都能在 <see cref="History"/> 里看到）：</para>
/// <code>
/// user: "现在几点"
/// assistant: tool_calls=[get_time({"city":"上海"})]     ← 模型要工具
/// tool: "2026-09-19 10:31:07"（tool_call_id 对上）      ← 回灌
/// assistant: "现在是 10 点 31 分。"                      ← 拿到结果后的最终回答
/// </code>
/// <para>
/// 护栏：单轮工具调用最多 <see cref="MaxToolRounds"/> 轮，防止模型来回抽风把用户的钱烧光；
/// 工具的异常已经被 <see cref="ToolRegistry"/> 吞成失败结果回灌给模型（模型看到 ERROR 会自己改参数重试）。
/// </para>
/// <para>不是线程安全的：一个会话同一时刻只跑一轮。</para>
/// </remarks>
public sealed class ChatSession
{
    private readonly List<ChatMessage> _history = new();

    /// <summary>建一个会话。<paramref name="systemMessages"/> 是要预置的 system 消息（人设 / 工具说明 / 记忆）。</summary>
    public ChatSession(OpenAiProvider provider, ToolRegistry tools, IEnumerable<ChatMessage>? systemMessages = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));

        if (systemMessages is not null)
        {
            foreach (var message in systemMessages)
            {
                if (message is not null)
                {
                    _history.Add(message);
                }
            }
        }
    }

    /// <summary>大脑。</summary>
    public OpenAiProvider Provider { get; }

    /// <summary>工具表。</summary>
    public ToolRegistry Tools { get; }

    /// <summary>完整上下文（含 system / 历史 / 工具结果），发给服务端的就是它。</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>单轮最多跑几轮工具调用。到顶就停下并如实标记（<see cref="TurnCompleted.StoppedAtRoundLimit"/>）。</summary>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>追加一条 system 消息（改人设 / 换记忆时用）。</summary>
    public void AddSystemMessage(string content) => _history.Add(ChatMessage.System(content));

    /// <summary>清空上下文，从零开始。</summary>
    public void Reset() => _history.Clear();

    /// <summary>
    /// 发一条用户消息并跑完整个工具循环，过程中把所有事件吐出来。
    /// </summary>
    public async IAsyncEnumerable<ChatTurnEvent> SendAsync(
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userMessage = ChatMessage.User(userText ?? string.Empty);
        _history.Add(userMessage);

        var newMessages = new List<ChatMessage> { userMessage };
        var finalText = string.Empty;
        var toolCallsExecuted = 0;
        var stoppedAtRoundLimit = false;
        string? finishReason = null;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var accumulator = new ToolCallAccumulator();
            var text = new StringBuilder();

            // 这一轮的思维链：攒进 assistant 消息，后面每个请求都原样带回（思考模式 + tools 的硬要求）。
            var reasoning = new StringBuilder();

            await foreach (var evt in Provider.StreamCompletionAsync(_history, Tools.Tools, ct).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case ChatTextDelta delta:
                        text.Append(delta.Text);
                        yield return new TurnTextDelta(delta.Text);
                        break;

                    case ChatReasoningDelta thought:
                        // 思维链不是回答正文：不吐 TurnTextDelta，只记账。
                        reasoning.Append(thought.Text);
                        break;

                    case ChatToolCallDelta toolDelta:
                        accumulator.Apply(toolDelta);
                        break;

                    case ChatFinished finished:
                        finishReason = finished.FinishReason;
                        break;
                }
            }

            var calls = accumulator.Build();
            var assistant = ChatMessage.Assistant(
                text.Length > 0 ? text.ToString() : null,
                calls.Count > 0 ? calls : null,
                reasoning.Length > 0 ? reasoning.ToString() : null);
            _history.Add(assistant);
            newMessages.Add(assistant);
            yield return new TurnAssistantMessage(assistant);

            if (calls.Count == 0)
            {
                // 模型不再要工具 —— 这一轮结束。
                finalText = text.ToString();
                break;
            }

            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                toolCallsExecuted++;

                var name = call.Function.Name ?? string.Empty;
                var argumentsJson = call.Function.Arguments;
                var risk = Tools.TryGet(name, out var tool) && tool is not null ? tool.Risk : ToolRisk.Safe;

                yield return new TurnToolCallStarting(call.Id, name, argumentsJson, risk);

                ToolResult result;
                var elapsed = TimeSpan.Zero;

                if (!TryParseArguments(argumentsJson, out var args, out var parseError))
                {
                    // 模型吐了半截非法 JSON：不执行，把错误当工具结果回灌，让它自己改。
                    result = ToolResult.Error(parseError!);
                }
                else
                {
                    var stopwatch = Stopwatch.StartNew();
                    result = await Tools.InvokeAsync(name, args, ct).ConfigureAwait(false);
                    stopwatch.Stop();
                    elapsed = stopwatch.Elapsed;
                }

                yield return new TurnToolCallFinished(call.Id, name, result, elapsed);

                var toolMessage = ChatMessage.Tool(call.Id, result.ToModelText(), name);
                _history.Add(toolMessage);
                newMessages.Add(toolMessage);
            }

            if (round == MaxToolRounds - 1)
            {
                stoppedAtRoundLimit = true;
                finalText = text.ToString();
            }
        }

        yield return new TurnCompleted(finalText, toolCallsExecuted, finishReason, stoppedAtRoundLimit, newMessages);
    }

    /// <summary>不关心过程时的简版：跑完一整轮，只拿最终文本和统计。</summary>
    public async Task<ChatTurnResult> SendAndCollectAsync(string userText, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        var toolCalls = 0;
        string? finishReason = null;
        IReadOnlyList<ChatMessage> messages = Array.Empty<ChatMessage>();

        await foreach (var evt in SendAsync(userText, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case TurnTextDelta delta:
                    text.Append(delta.Text);
                    break;

                case TurnCompleted completed:
                    toolCalls = completed.ToolCallsExecuted;
                    finishReason = completed.FinishReason;
                    messages = completed.NewMessages;
                    break;
            }
        }

        return new ChatTurnResult(text.ToString(), toolCalls, finishReason, messages);
    }

    /// <summary>
    /// 解析工具参数。<b>失败不抛异常</b>，而是把原因交回给调用方当工具结果回灌 ——
    /// 模型经常吐半截 JSON，这属于"要让它知道并重试"，不是崩溃点。
    /// </summary>
    private static bool TryParseArguments(string? json, out JsonElement args, out string? error)
    {
        args = default;
        error = null;

        var text = string.IsNullOrWhiteSpace(json) ? "{}" : json!;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"Tool arguments must be a JSON object, but got {doc.RootElement.ValueKind}: {Truncate(text)}";
                return false;
            }

            // Clone 之后才敢把 JsonElement 交出去（原文档马上就被释放了）。
            args = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Tool arguments are not valid JSON ({ex.Message}): {Truncate(text)}";
            return false;
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
