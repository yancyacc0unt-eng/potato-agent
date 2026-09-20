namespace PotatoAgent.Core.Brain;

/// <summary>
/// 一次流式请求的实测时序 —— 用来回答"它到底是一点点在吐，还是攒到最后一次性蹦出来"。
/// </summary>
/// <remarks>
/// <para>
/// 全部数字都来自 <see cref="OpenAiProvider"/> 自己的秒表，不经过任何界面的渲染路径，
/// 所以能干净地区分两件事：
/// </para>
/// <list type="bullet">
/// <item><see cref="FirstByte"/> / <see cref="FirstTextDelta"/> 很大 —— 是服务端/模型的首字延迟（不是我们的问题）；</item>
/// <item>增量之间间隔很小、却在同一瞬间被消费方看到 —— 是我们自己或调用方在攒（缓冲），该修。</item>
/// </list>
/// <para>每次请求结束（正常结束或出错）都会刷新一次，可以从 <see cref="OpenAiProvider.LastStreamTiming"/> 读。</para>
/// </remarks>
/// <param name="Total">从发请求到流结束的总耗时。</param>
/// <param name="FirstByte">响应体第一个字节到达的时刻（相对发请求）；一个字节都没读到则为 null。</param>
/// <param name="FirstTextDelta">第一个文本增量解析出来的时刻；整轮没有任何文本则为 null。</param>
/// <param name="TextDeltaCount">文本增量个数（服务端拆得越细，这个数越大）。</param>
/// <param name="TextDeltaGaps">相邻两个文本增量之间的间隔，按顺序排列（个数 = <paramref name="TextDeltaCount"/> - 1）。</param>
public sealed record StreamTiming(
    TimeSpan Total,
    TimeSpan? FirstByte,
    TimeSpan? FirstTextDelta,
    int TextDeltaCount,
    IReadOnlyList<TimeSpan> TextDeltaGaps)
{
    /// <summary>相邻文本增量之间的最大间隔；不足两个增量时为 null。</summary>
    public TimeSpan? MaxTextDeltaGap
    {
        get
        {
            TimeSpan? max = null;
            foreach (var gap in TextDeltaGaps)
            {
                if (max is null || gap > max.Value)
                {
                    max = gap;
                }
            }

            return max;
        }
    }

    /// <summary>相邻文本增量之间的平均间隔；不足两个增量时为 null。</summary>
    public TimeSpan? AverageTextDeltaGap
    {
        get
        {
            if (TextDeltaGaps.Count == 0)
            {
                return null;
            }

            double total = 0;
            foreach (var gap in TextDeltaGaps)
            {
                total += gap.TotalMilliseconds;
            }

            return TimeSpan.FromMilliseconds(total / TextDeltaGaps.Count);
        }
    }

    /// <summary>一行人话摘要，直接进日志/状态栏。</summary>
    public override string ToString() =>
        $"total {Total.TotalMilliseconds:0} ms, first byte {(FirstByte is { } b ? $"{b.TotalMilliseconds:0} ms" : "n/a")}, " +
        $"first text delta {(FirstTextDelta is { } d ? $"{d.TotalMilliseconds:0} ms" : "n/a")}, " +
        $"{TextDeltaCount} delta(s), gap avg {(AverageTextDeltaGap is { } a ? $"{a.TotalMilliseconds:0}" : "n/a")} ms / " +
        $"max {(MaxTextDeltaGap is { } m ? $"{m.TotalMilliseconds:0}" : "n/a")} ms";
}
