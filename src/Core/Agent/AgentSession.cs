using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Agent;

/// <summary>带图工具结果回灌给模型的方式。</summary>
public enum AgentImageDelivery
{
    /// <summary>
    /// 默认：工具消息之后追加一条带图的 <c>user</c> 消息。
    /// 兼容性最好 —— 官方 OpenAI 协议只允许 <c>user</c> 消息的 <c>content</c> 放 <c>image_url</c>，
    /// tool 消息塞数组会被一部分服务端直接 400。
    /// </summary>
    FollowUpUserMessage = 0,

    /// <summary>把图直接放进 <c>tool</c> 消息的 <c>content</c> 数组。更紧凑，但只对认这套的服务端有效。</summary>
    ToolMessage = 1,
}

/// <summary>
/// 一个可以对话、可以调工具的会话 —— 界面主要绑的就是这个对象。
/// </summary>
/// <remarks>
/// <para>
/// 它把 <see cref="OpenAiProvider"/>（说话）和 <see cref="ToolRegistry"/>（干活）串成一个闭环：
/// </para>
/// <code>
/// 用户: "帮我看看屏幕上是什么"
///   → 模型要 pc_screenshot
///   → 权限门（Safe，直接过）
///   → 截图执行，拿到真实图片
///   → 结果 + 图片（image_url data URI）回灌
///   → 模型看着图回答
/// </code>
/// <para><b>权限门</b>：<see cref="ToolRisk.Confirm"/> / <see cref="ToolRisk.Dangerous"/> 的工具一律先 await
/// <see cref="Approver"/>；<b>没挂 approver 就一律拒绝</b>，不存在"忘了挂于是乱点用户电脑"的路径。</para>
/// <para><b>思考模式</b>：模型吐的思维链（<see cref="ChatReasoningDelta"/>）会攒进这一轮的 assistant 消息
/// （<see cref="ChatMessage.ReasoningContent"/>），并且<b>之后每个请求都原样带回</b> ——
/// DeepSeek 思考模式要求带 tools 时回传所有之前轮次的 reasoning_content，漏了就 400。</para>
/// <para><b>线程模型</b>：不是线程安全的，同一时刻只跑一轮。事件是在<b>调用方的线程</b>上吐出来的
/// （<c>await foreach</c> 的续体），WPF 里记得回到 UI 线程再刷控件。</para>
/// <para><b>异常</b>：模型/网络出错不会抛，而是吐一条 <see cref="AgentError"/> 然后收尾；
/// 只有用户取消（<see cref="OperationCanceledException"/>）会原样上抛 —— 取消时会把剩下来的
/// tool 消息补齐，保证历史自洽，不会留下"assistant 要调工具却没有结果"的破历史。</para>
/// </remarks>
public sealed class AgentSession
{
    private readonly List<ChatMessage> _history = new();
    private readonly HashSet<string> _alwaysAllowed = new(StringComparer.Ordinal);

    /// <summary>建一个会话。</summary>
    /// <param name="provider">大脑（OpenAI 兼容流式客户端）。</param>
    /// <param name="tools">工具表。</param>
    /// <param name="approver">权限门；<b>null 表示危险工具一律拒绝</b>。</param>
    /// <param name="systemMessages">预置的 system 消息（人设 / 工具说明 / 记忆）。</param>
    public AgentSession(
        OpenAiProvider provider,
        ToolRegistry tools,
        IToolApprover? approver = null,
        IEnumerable<ChatMessage>? systemMessages = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Approver = approver;

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

    /// <summary>大脑。换模型就换它（<see cref="OpenAiProvider"/> 持有 HttpClient，别每轮 new 一个）。</summary>
    public OpenAiProvider Provider { get; }

    /// <summary>工具表。界面上的"工具"页签直接绑 <see cref="ToolRegistry.Tools"/>。</summary>
    public ToolRegistry Tools { get; }

    /// <summary>
    /// 权限门。可以在运行中替换（例如用户中途把"每次都问"改成"自动批准"）。
    /// 设成 null = 危险工具重新变回一律拒绝。
    /// </summary>
    public IToolApprover? Approver { get; set; }

    /// <summary>
    /// 权限档位。可以在运行中改 —— 下一次工具调用就按新档位判定。
    /// 默认 <see cref="ToolApprovalMode.Basic"/>：Confirm / Dangerous 都先问用户。
    /// </summary>
    /// <remarks>档位只决定"要不要弹确认框"；没挂 <see cref="Approver"/> 时该问的一律拒绝（fail-closed）。</remarks>
    public ToolApprovalMode ApprovalMode { get; set; } = ToolApprovalMode.Basic;

    /// <summary>单轮最多跑几轮工具调用；到顶就停下并如实标记 <see cref="AgentTurnResult.StoppedAtRoundLimit"/>。</summary>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>
    /// "同一个回合里不许用同一个工具对同一个东西做两遍"这道护栏，默认<b>开启</b>。
    /// 两道判定：<b>参数逐字节相同</b>（见 <see cref="ToolCallSignature"/>），
    /// 以及<b>工具自报的资源身份相同</b>（见 <see cref="IToolCallIdentity"/>，参数换个写法也拦得住）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 第二次出现时<b>不执行</b>，只回一条带第一次结果摘要的提示给模型（<c>ERROR: You already called …</c>）。
    /// </para>
    /// <para>
    /// 为什么默认开：真实事故 —— 模型想开记事本，一时没拿到窗口句柄就反复调 <c>pc_launch</c>，
    /// 而用户点过 Allow always 之后连确认框都不弹，于是静默堆出一屏记事本。
    /// 参数逐字节那道拦不住"<c>{"path":"notepad.exe"}</c> 改成 <c>{"path":"notepad.exe","wait_ms":8000}</c>"，
    /// 所以补了身份那道。
    /// </para>
    /// <para>
    /// 反证也成立：<b>换目标</b>、<b>换工具</b>、<b>换一个回合</b>（新的一次 <see cref="SendAsync"/>）都不受影响。
    /// 需要"故意重复"的场景把它设成 false 即可。
    /// </para>
    /// </remarks>
    public bool DeduplicateToolCalls { get; set; } = true;

    /// <summary>带图工具结果的回灌方式，默认见 <see cref="AgentImageDelivery.FollowUpUserMessage"/>。</summary>
    public AgentImageDelivery ImageDelivery { get; set; } = AgentImageDelivery.FollowUpUserMessage;

    /// <summary>完整上下文（system / 历史 / 工具结果，含图片），发给服务端的就是它。</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>
    /// 本次会话里用户说过"允许"的那些工具（<see cref="ToolApprovalDecision.AllowAlways"/> 攒下来的）。
    /// 界面可以显示成"已授权：pc_click, pc_type"。
    /// </summary>
    public IReadOnlyCollection<string> AlwaysAllowedTools => _alwaysAllowed;

    /// <summary>把"已授权"清空 —— 下一轮危险工具会重新弹确认框。</summary>
    public void ClearRememberedApprovals() => _alwaysAllowed.Clear();

    /// <summary>追加一条 system 消息（改人设 / 换记忆时用）。</summary>
    public void AddSystemMessage(string content) => _history.Add(ChatMessage.System(content));

    /// <summary>清空上下文，从零开始（"新对话"按钮）。</summary>
    public void Reset()
    {
        _history.Clear();
        _alwaysAllowed.Clear();
    }

    /// <summary>
    /// 把一份已有历史装回来 —— 切回旧会话、或重启后恢复上次对话时用。
    /// 会先清空当前上下文和"总是允许"名单，再按顺序装入；null 或空表 = 只是清空。
    /// </summary>
    /// <remarks>只在没有正在跑一轮的时候调用（<see cref="AgentSession"/> 本来就不是线程安全的）。</remarks>
    public void RestoreHistory(IEnumerable<ChatMessage>? messages)
    {
        _history.Clear();
        _alwaysAllowed.Clear();

        if (messages is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            if (message is not null)
            {
                _history.Add(message);
            }
        }
    }

    /// <summary>
    /// 发一条用户消息，跑完整个"说话 → 调工具 → 回灌 → 再说"的循环，过程中把事件流式吐出来。
    /// </summary>
    /// <param name="userText">用户输入。</param>
    /// <param name="ct">取消令牌：按了"停止"就传进来，工具不会被执行到一半还继续。</param>
    public async IAsyncEnumerable<AgentEvent> SendAsync(
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userMessage = ChatMessage.User(userText ?? string.Empty);
        _history.Add(userMessage);

        var newMessages = new List<ChatMessage> { userMessage };
        var finalText = string.Empty;
        var executed = 0;
        var denied = 0;
        var duplicateCalls = 0;
        var stoppedAtRoundLimit = false;
        string? finishReason = null;
        AgentError? failure = null;

        // 护栏的账本：签名 → 第一次那次调用的结果摘要。
        // 每个回合新建一份，所以"换一个回合"天然不受影响。
        var seenCalls = new Dictionary<string, string>(StringComparer.Ordinal);

        // 第二本账：工具自报的"资源身份" → 第一次那次调用的结果摘要。
        // 大小写不敏感（Windows 上路径本来就不区分大小写），同样是每回合新建一份。
        var seenTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < MaxToolRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var accumulator = new ToolCallAccumulator();
            var text = new StringBuilder();

            // 这一轮的思维链。必须攒下来写进 assistant 消息 —— 带 tools 的请求漏了它会 400。
            var reasoning = new StringBuilder();
            string? roundFinishReason = null;
            AgentError? streamError = null;

            // 手动 MoveNext：yield return 不能出现在带 catch 的 try 里（C# 语法限制），
            // 但把 try/catch 只套在 MoveNextAsync 上就够了 —— 这样文本增量可以【当场】交给调用方，
            // 而不是攒到整轮结束再一次性吐（实测老写法：服务端每 50ms 发一块、共 15 块时，
            // 15 个 AgentTextDelta 全挤在最后一个毫秒到达）。
            var stream = Provider
                .StreamCompletionAsync(_history, Tools.Tools, ct)
                .GetAsyncEnumerator(ct);

            try
            {
                while (true)
                {
                    ChatStreamEvent evt;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        evt = stream.Current;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        streamError = new AgentError(Describe(ex), ex);
                        break;
                    }

                    switch (evt)
                    {
                        case ChatTextDelta delta:
                            text.Append(delta.Text);
                            yield return new AgentTextDelta(delta.Text);
                            break;

                        case ChatReasoningDelta thought:
                            // 只记账，不往界面上吐（思考过程不是回答正文）。
                            // 全文会跟着这一轮的 assistant 消息进历史，下一轮请求原样带回。
                            reasoning.Append(thought.Text);
                            break;

                        case ChatToolCallDelta toolDelta:
                            accumulator.Apply(toolDelta);
                            break;

                        case ChatFinished finished:
                            roundFinishReason = finished.FinishReason;
                            break;
                    }
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (streamError is not null)
            {
                failure = streamError;
                break;
            }

            if (roundFinishReason is not null)
            {
                finishReason = roundFinishReason;
            }

            var calls = accumulator.Build();
            var assistant = ChatMessage.Assistant(
                text.Length > 0 ? text.ToString() : null,
                calls.Count > 0 ? calls : null,
                reasoning.Length > 0 ? reasoning.ToString() : null);   // 这轮没思考就不写这个字段
            _history.Add(assistant);
            newMessages.Add(assistant);

            if (calls.Count == 0)
            {
                finalText = text.ToString();
                break;
            }

            OperationCanceledException? cancellation = null;

            foreach (var call in calls)
            {
                var name = call.Function.Name ?? string.Empty;
                var argumentsJson = call.Function.Arguments ?? string.Empty;
                Tools.TryGet(name, out var tool);
                var risk = tool?.Risk ?? ToolRisk.Safe;
                var needsApproval = NeedsApproval(tool, name);

                if (ct.IsCancellationRequested && cancellation is null)
                {
                    cancellation = new OperationCanceledException(ct);
                }

                if (cancellation is not null)
                {
                    // 已经取消了：这次和后面每次调用都补一条 tool 消息再走，免得历史破在半路。
                    AppendToolMessage(call.Id, name, ToolResult.Error("Cancelled by the user before this tool ran."), newMessages);
                    continue;
                }

                // 护栏一：这一回合里"同名工具 + 规范化后完全相同的参数"是不是已经出现过？
                var signature = ToolCallSignature.Of(name, argumentsJson);
                var parsed = TryParseArguments(argumentsJson, out var args, out var parseError);

                string? earlierSummary = null;
                var isDuplicate = DeduplicateToolCalls && seenCalls.TryGetValue(signature, out earlierSummary);

                // 护栏二：这一回合里"同名工具 + 同一个资源身份"是不是已经出现过？
                // 参数写得不一样也拦得住 —— 见 IToolCallIdentity（pc_launch 的"同一个程序只启动一次"靠它）。
                string? identityKey = null;
                if (DeduplicateToolCalls && !isDuplicate && parsed && tool is IToolCallIdentity identityTool)
                {
                    var identity = identityTool.IdentityOf(args);
                    if (!string.IsNullOrWhiteSpace(identity))
                    {
                        identityKey = name + "\u0000" + identity;
                    }
                }

                string? earlierIdentitySummary = null;
                var isSameTarget = identityKey is not null &&
                                   seenTargets.TryGetValue(identityKey, out earlierIdentitySummary);

                yield return new AgentToolStarting(
                    call.Id, name, argumentsJson, risk, isDuplicate || isSameTarget ? false : needsApproval);

                ToolResult result;
                var elapsed = TimeSpan.Zero;
                AgentToolDenied? denial = null;

                if (isDuplicate)
                {
                    // 不执行、也不过权限门（第一次已经问过了）：只把"你刚刚调过、结果是这个"讲清楚。
                    duplicateCalls++;
                    result = ToolResult.Error(DuplicateCallNotice(name, earlierSummary!));
                }
                else if (isSameTarget)
                {
                    // 同上：同一个资源这一回合只动一次，参数换个写法也不行。
                    duplicateCalls++;
                    result = ToolResult.Error(SameTargetNotice(name, identityKey!, earlierIdentitySummary!));
                }
                else if (!parsed)
                {
                    result = ToolResult.Error(parseError!);
                }
                else
                {
                    var (allowed, denyReason) = await DecideAsync(tool, name, args, argumentsJson, ct).ConfigureAwait(false);

                    if (!allowed)
                    {
                        denied++;
                        result = ToolResult.Error(denyReason!);
                        denial = new AgentToolDenied(call.Id, name, denyReason!);
                    }
                    else
                    {
                        var stopwatch = Stopwatch.StartNew();
                        try
                        {
                            result = await Tools.InvokeAsync(name, args, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            cancellation = new OperationCanceledException(ct);
                            AppendToolMessage(call.Id, name, ToolResult.Error("Cancelled by the user while this tool was running."), newMessages);
                            continue;
                        }
                        finally
                        {
                            stopwatch.Stop();
                            elapsed = stopwatch.Elapsed;
                        }

                        executed++;
                    }
                }

                if (!isDuplicate && !isSameTarget)
                {
                    // 记下这一次的结果摘要 —— 第二次出现时提示里要带上它，模型才知道该往下走。
                    // （被拒绝的调用也记：同一个回合里不该为同一件事再弹一次确认框。）
                    seenCalls[signature] = Summarize(result);
                    if (identityKey is not null)
                    {
                        seenTargets[identityKey] = Summarize(result);
                    }
                }

                if (denial is not null)
                {
                    yield return denial;
                }
                else
                {
                    yield return new AgentToolFinished(call.Id, name, result.Success, Summarize(result), elapsed, result.Images.Count);
                }

                AppendToolMessage(call.Id, name, result, newMessages);
            }

            if (cancellation is not null)
            {
                throw cancellation;
            }

            if (round == MaxToolRounds - 1)
            {
                stoppedAtRoundLimit = true;
                finalText = text.ToString();
            }
        }

        if (failure is not null)
        {
            yield return failure;
        }

        yield return new AgentTurnCompleted(
            new AgentTurnResult(finalText, executed, denied, finishReason, stoppedAtRoundLimit, newMessages, duplicateCalls));
    }

    /// <summary>不关心过程时的简版：跑完一整轮，只拿最终文本和统计。</summary>
    public async Task<AgentTurnResult> SendAndCollectAsync(string userText, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        AgentTurnResult? result = null;

        await foreach (var evt in SendAsync(userText, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentTextDelta delta:
                    text.Append(delta.Text);
                    break;

                case AgentTurnCompleted completed:
                    result = completed.Result;
                    break;
            }
        }

        return result ?? new AgentTurnResult(text.ToString(), 0, 0, null, false, Array.Empty<ChatMessage>());
    }

    // ==================== 内部 ====================

    /// <summary>
    /// 这一次调用要不要过权限门。三种情况直接放行：安全的工具、用户点过"总是允许"的、以及当前档位下不必问的。
    /// </summary>
    /// <remarks>
    /// <see cref="ToolApprovalMode.Basic"/> 下 Confirm / Dangerous 都问；
    /// <see cref="ToolApprovalMode.Advanced"/> 下只有 Dangerous 才问，Confirm 静默放行。
    /// 未知工具（<paramref name="tool"/> 为 null）也走这里返回 false，交给 ToolRegistry 去报"未知工具"。
    /// </remarks>
    private bool NeedsApproval(ITool? tool, string name)
    {
        if (tool is null || tool.Risk == ToolRisk.Safe || _alwaysAllowed.Contains(name))
        {
            return false;
        }

        return ApprovalMode == ToolApprovalMode.Basic || tool.Risk == ToolRisk.Dangerous;
    }

    /// <summary>权限门：决定这一次调用放不放行。任何"说不清"的情况都按拒绝处理。</summary>
    private async Task<(bool Allowed, string? DenyReason)> DecideAsync(
        ITool? tool,
        string name,
        JsonElement args,
        string argumentsJson,
        CancellationToken ct)
    {
        // 不存在的工具交给 ToolRegistry 报"未知工具"，这里不拦（拦了反而让模型不知道名字写错了）。
        if (tool is null || !NeedsApproval(tool, name))
        {
            return (true, null);
        }

        var approver = Approver;
        if (approver is null)
        {
            return (false, $"Refused: '{name}' has risk {tool.Risk} and no approver is configured, so it was not executed.");
        }

        ToolApprovalDecision decision;
        try
        {
            decision = await approver
                .ApproveAsync(new ToolApprovalRequest(name, tool.Description ?? string.Empty, tool.Risk, args, argumentsJson), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 确认框自己崩了：按拒绝处理。fail-closed。
            return (false, $"Refused: the approval prompt for '{name}' failed ({Describe(ex)}), so it was not executed.");
        }

        return decision switch
        {
            ToolApprovalDecision.AllowAlways => Remember(name),
            ToolApprovalDecision.AllowOnce => (true, null),
            _ => (false, $"Refused: the user denied the call to '{name}'."),
        };
    }

    private (bool Allowed, string? DenyReason) Remember(string name)
    {
        _alwaysAllowed.Add(name);
        return (true, null);
    }

    /// <summary>把工具结果写进历史（必要时另起一条带图的 user 消息）。</summary>
    private void AppendToolMessage(string callId, string name, ToolResult result, List<ChatMessage> newMessages)
    {
        var modelText = result.ToModelText();

        if (!result.HasImages || ImageDelivery == AgentImageDelivery.FollowUpUserMessage)
        {
            var plain = ChatMessage.Tool(callId, modelText, name);
            _history.Add(plain);
            newMessages.Add(plain);

            if (result.HasImages)
            {
                // 图不放在 tool 消息里：官方协议只给 user 消息开了 image_url。
                var followUp = ChatMessage.UserWithParts(
                    BuildImageParts($"{name} returned {result.Images.Count} image(s) (tool_call_id={callId}). Look at them.", result.Images));
                _history.Add(followUp);
                newMessages.Add(followUp);
            }

            return;
        }

        var withParts = ChatMessage.ToolWithParts(callId, BuildImageParts(modelText, result.Images), name);
        _history.Add(withParts);
        newMessages.Add(withParts);
    }

    /// <summary>文本 + 图片拼成 <c>content</c> 数组。</summary>
    private static List<ChatContentPart> BuildImageParts(string text, IReadOnlyList<ToolImage> images)
    {
        var parts = new List<ChatContentPart>(images.Count * 2 + 1)
        {
            ChatContentPart.FromText(text),
        };

        foreach (var image in images)
        {
            var label = string.IsNullOrWhiteSpace(image.Caption)
                ? $"image ({image.MimeType}, {image.ApproximateByteCount} bytes)"
                : $"{image.Caption} ({image.MimeType}, {image.ApproximateByteCount} bytes)";

            parts.Add(ChatContentPart.FromText(label));
            parts.Add(ChatContentPart.FromImageDataUri(image.DataUri));
        }

        return parts;
    }

    /// <summary>
    /// 护栏拦下重复调用时回给模型的那句话：说清"你已经调过了"、附上第一次的结果摘要、
    /// 并指明接下来能怎么办 —— 免得它傻等一个不会到来的结果。
    /// </summary>
    private static string DuplicateCallNotice(string name, string earlierSummary) =>
        $"You already called {name} with these exact arguments in this turn; result was: {earlierSummary}. " +
        "This duplicate call was NOT executed - the earlier result still stands. " +
        "Continue from that result; if you really need to run it again, change the arguments or start a new turn.";

    /// <summary>
    /// 第二道护栏拦下"同一个资源"时回给模型的那句话：说清"参数虽然换了写法，但动的还是同一个东西"、
    /// 附上第一次的结果摘要，并明确下一步该拿那个结果继续做。
    /// </summary>
    private static string SameTargetNotice(string name, string identityKey, string earlierSummary)
    {
        var target = identityKey[(identityKey.IndexOf('\0') + 1)..].Replace('\u001F', ' ');

        return $"You already called {name} for the SAME target (\"{target}\") in this turn - " +
               $"different argument wording, same thing. Nothing was done a second time. Result of the first call: {earlierSummary}. " +
               "Use what that first call returned (for pc_launch: the hwnd) and carry on. " +
               "Only if you deliberately need a second, separate instance, ask for it explicitly (force_new_instance=true).";
    }

    /// <summary>一行结果摘要，给界面做过程提示用。</summary>
    private static string Summarize(ToolResult result)
    {
        var text = result.Content.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (text.Length == 0)
        {
            text = result.Success ? "(no output)" : "(failed with no message)";
        }

        if (text.Length > 200)
        {
            text = text[..200] + "…";
        }

        return result.Success ? text : "ERROR: " + text;
    }

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

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}
