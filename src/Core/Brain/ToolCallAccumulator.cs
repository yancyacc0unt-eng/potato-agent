using System.Text;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 把流式返回的 <see cref="ChatToolCallDelta"/> 拼成完整的 <see cref="ToolCall"/>。
/// </summary>
/// <remarks>
/// <para>为什么必须单独一个类：模型调工具时服务端不是一个 chunk 发完的，而是拆成很多片，典型长这样——</para>
/// <code>
/// data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_time","arguments":""}}]}}]}
/// data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\""}}]}}]}
/// data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":\"上海\"}"}}]}}]}
/// </code>
/// <para>
/// 参数是<b>字符串碎片</b>，必须按 index 归并后原样首尾相接（不能每片各解析一次 JSON）。
/// 而且服务端可能在同一条消息里并发调用多个工具，靠 index 区分。
/// </para>
/// <para>用法：一边读事件一边 <see cref="Apply"/>，流结束（或收到 finish_reason）后调 <see cref="Build"/>。</para>
/// </remarks>
public sealed class ToolCallAccumulator
{
    private readonly SortedDictionary<int, Builder> _builders = new();
    private int _nextImplicitIndex;

    /// <summary>已经见过至少一个工具调用片。</summary>
    public bool HasAny => _builders.Count > 0;

    /// <summary>吃进一片增量。同一 index 的片会被拼接。</summary>
    public void Apply(ChatToolCallDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var index = ResolveIndex(delta);

        if (!_builders.TryGetValue(index, out var builder))
        {
            builder = new Builder();
            _builders.Add(index, builder);
        }

        if (!string.IsNullOrEmpty(delta.Id))
        {
            builder.Id = delta.Id!;
        }

        if (!string.IsNullOrEmpty(delta.Name))
        {
            builder.Name = delta.Name!;
        }

        if (!string.IsNullOrEmpty(delta.ArgumentsFragment))
        {
            builder.Arguments.Append(delta.ArgumentsFragment);
        }
    }

    /// <summary>拼好的工具调用，按 index 升序。参数是空的话给 <c>{}</c>（很多模型的无参调用就是空串）。</summary>
    public List<ToolCall> Build()
    {
        var calls = new List<ToolCall>(_builders.Count);

        foreach (var pair in _builders)
        {
            var builder = pair.Value;
            var arguments = builder.Arguments.Length == 0 ? "{}" : builder.Arguments.ToString();

            calls.Add(new ToolCall
            {
                Id = string.IsNullOrEmpty(builder.Id) ? $"call_{pair.Key}" : builder.Id,
                Type = "function",
                Function = new ToolCallFunction
                {
                    Name = builder.Name ?? string.Empty,
                    Arguments = arguments,
                },
            });
        }

        return calls;
    }

    /// <summary>清空，准备下一轮。</summary>
    public void Reset()
    {
        _builders.Clear();
        _nextImplicitIndex = 0;
    }

    /// <summary>
    /// 定 index：服务端给了就用；没给时，带 id 的当新调用，不带 id 的续在最后一个调用后面
    /// （少数兼容服务端不发 index，这样至少不会把两个工具的参数粘在一起）。
    /// </summary>
    private int ResolveIndex(ChatToolCallDelta delta)
    {
        if (delta.Index.HasValue)
        {
            _nextImplicitIndex = Math.Max(_nextImplicitIndex, delta.Index.Value + 1);
            return delta.Index.Value;
        }

        if (!string.IsNullOrEmpty(delta.Id))
        {
            return _nextImplicitIndex++;
        }

        if (_builders.Count > 0)
        {
            return _builders.Keys.Max();
        }

        return _nextImplicitIndex++;
    }

    private sealed class Builder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
