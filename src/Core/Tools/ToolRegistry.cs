using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PotatoAgent.Core.Tools;

/// <summary>工具名不存在时抛（只有主动查表才会遇到；<see cref="ToolRegistry.InvokeAsync"/> 会把它转成失败结果）。</summary>
public sealed class ToolNotFoundException : Exception
{
    public ToolNotFoundException(string toolName)
        : base($"Tool '{toolName}' is not registered.")
    {
        ToolName = toolName;
    }

    /// <summary>找不到的工具名。</summary>
    public string ToolName { get; }
}

/// <summary>
/// 工具注册表：注册 / 按名查 / 导出成 OpenAI <c>tools</c> 数组 / 统一执行。
/// </summary>
/// <remarks>
/// <para>
/// <b>异常一律吞掉</b>是这张表最重要的性质：<see cref="InvokeAsync"/> 永远返回 <see cref="ToolResult"/>，
/// 工具抛什么（NullReference、IO、超时后自己抛的）都变成失败结果回灌给模型，绝不让异常炸掉对话主循环。
/// 唯一例外是取消：<paramref name="ct"/> 已取消时抛出的 <see cref="OperationCanceledException"/> 原样上抛，
/// 否则用户按了"停止"却还在傻跑。
/// </para>
/// <para>线程安全：注册表本身用锁保护，可以边跑边注册。</para>
/// </remarks>
public sealed class ToolRegistry
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);
    private readonly List<ITool> _ordered = new();

    /// <summary>
    /// 可选的确认回调：返回 false 表示用户拒绝执行。UI（Chat 页确认框）挂这个钩子；
    /// 为 null 时不做拦截，由调用方自己保证危险动作不会被静默执行。
    /// </summary>
    public Func<ITool, JsonElement, CancellationToken, Task<bool>>? Approver { get; set; }

    /// <summary>已注册的工具（按注册顺序）。返回的是快照副本，可以安全遍历。</summary>
    public IReadOnlyList<ITool> Tools
    {
        get
        {
            lock (_gate)
            {
                return _ordered.ToArray();
            }
        }
    }

    /// <summary>已注册数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _ordered.Count;
            }
        }
    }

    /// <summary>注册工具。名字重复或格式非法时抛 <see cref="ArgumentException"/>（这是编程错误，早炸早好）。</summary>
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        Validate(tool);

        lock (_gate)
        {
            if (_byName.ContainsKey(tool.Name))
            {
                throw new ArgumentException($"Tool '{tool.Name}' is already registered.", nameof(tool));
            }

            _byName.Add(tool.Name, tool);
            _ordered.Add(tool);
        }
    }

    /// <summary>注册工具，重名/非法只返回 false 不抛（给插件式动态加载用）。</summary>
    public bool TryRegister(ITool tool)
    {
        if (tool is null)
        {
            return false;
        }

        try
        {
            Register(tool);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>按名查；找不到返回 false。</summary>
    public bool TryGet(string name, out ITool? tool)
    {
        if (name is null)
        {
            tool = null;
            return false;
        }

        lock (_gate)
        {
            return _byName.TryGetValue(name, out tool);
        }
    }

    /// <summary>按名查；找不到抛 <see cref="ToolNotFoundException"/>。</summary>
    public ITool Get(string name)
    {
        if (TryGet(name, out var tool) && tool is not null)
        {
            return tool;
        }

        throw new ToolNotFoundException(name ?? "<null>");
    }

    /// <summary>注销工具；不存在返回 false。</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            if (!_byName.Remove(name))
            {
                return false;
            }

            _ordered.RemoveAll(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            return true;
        }
    }

    /// <summary>清空注册表（换工作区 / 重载工具时用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byName.Clear();
            _ordered.Clear();
        }
    }

    /// <summary>
    /// 导出成 OpenAI 请求体里的 <c>tools</c> 数组（JSON 字符串形式）：
    /// <c>[{"type":"function","function":{"name":…,"description":…,"parameters":{…}}}]</c>
    /// </summary>
    public string BuildToolsJson()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTools(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>导出成 <see cref="JsonElement"/>（独立文档，调用方可以直接塞进请求体）。</summary>
    public JsonElement BuildToolsElement()
    {
        using var doc = JsonDocument.Parse(BuildToolsJson());
        return doc.RootElement.Clone();
    }

    /// <summary>把工具数组写进已有的 <see cref="Utf8JsonWriter"/>（组装请求体时省一次中间字符串）。</summary>
    public void WriteTools(Utf8JsonWriter writer)
    {
        ITool[] snapshot;
        lock (_gate)
        {
            snapshot = _ordered.ToArray();
        }

        WriteTools(writer, snapshot);
    }

    /// <summary>把任意一组工具写进 <see cref="Utf8JsonWriter"/>（Provider 手里只有工具列表、没有注册表时用）。</summary>
    public static void WriteTools(Utf8JsonWriter writer, IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tools);

        writer.WriteStartArray();
        foreach (var tool in tools)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description ?? string.Empty);
            writer.WritePropertyName("parameters");
            using (var schema = JsonDocument.Parse(tool.ParametersJsonSchema))
            {
                schema.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// 执行工具：查表 → （可选）用户确认 → 调用 → 任何异常吞成失败结果。
    /// 这个方法**不会因为工具出错而抛异常**（取消除外）。
    /// </summary>
    public async Task<ToolResult> InvokeAsync(string name, JsonElement args, CancellationToken ct = default)
    {
        if (!TryGet(name, out var tool) || tool is null)
        {
            return ToolResult.Error($"Unknown tool '{name}'. Registered tools: {RegisteredNames()}.");
        }

        var approver = Approver;
        if (approver is not null && tool.Risk != ToolRisk.Safe)
        {
            bool allowed;
            try
            {
                allowed = await approver(tool, args, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Confirmation for tool '{name}' failed: {Describe(ex)}");
            }

            if (!allowed)
            {
                return ToolResult.Error($"User denied the call to '{name}'.");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await tool.InvokeAsync(args, ct).ConfigureAwait(false);
            return result ?? ToolResult.Error($"Tool '{name}' returned null.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户主动取消：原样上抛，别伪装成"工具失败"。
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Tool '{name}' failed after {stopwatch.ElapsedMilliseconds} ms: {Describe(ex)}");
        }
    }

    /// <summary>已注册工具名的逗号列表，用于错误提示（让模型知道有哪些工具可选）。</summary>
    public string RegisteredNames()
    {
        lock (_gate)
        {
            return _ordered.Count == 0 ? "(none)" : string.Join(", ", _ordered.Select(t => t.Name));
        }
    }

    private static void Validate(ITool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("Tool name must not be empty.", nameof(tool));
        }

        if (!NamePattern.IsMatch(tool.Name))
        {
            throw new ArgumentException(
                $"Tool name '{tool.Name}' must be snake_case (^[a-z][a-z0-9_]*$).", nameof(tool));
        }

        if (string.IsNullOrWhiteSpace(tool.ParametersJsonSchema))
        {
            throw new ArgumentException($"Tool '{tool.Name}' must provide a JSON Schema for its parameters.", nameof(tool));
        }

        try
        {
            using var doc = JsonDocument.Parse(tool.ParametersJsonSchema);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"Tool '{tool.Name}' parameter schema must be a JSON object.", nameof(tool));
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Tool '{tool.Name}' parameter schema is not valid JSON: {ex.Message}", nameof(tool), ex);
        }
    }

    private static string Describe(Exception ex)
        => $"{ex.GetType().Name}: {ex.Message}";
}
