// 实测：流式到底是不是"一点点吐出来的"。
//
// 做法：让一个假服务端【故意】把回答切成 N 小块、每块之间隔 50ms 一块一块 flush，
// 然后在两个层面同时量：
//   1) Provider 层（OpenAiProvider.LastStreamTiming）：首字节 / 首个文本增量 / 增量个数 / 增量间隔；
//   2) AgentSession 层：每个 AgentTextDelta 事件被调用方看到的时刻。
//
// 结论只有两种，且必须量化：
//   * 两层的时间线都对得上服务端的节奏 → 我们是逐个及时吐的，慢只可能慢在首字延迟；
//   * Provider 层 50ms 一个、AgentSession 层却全挤在最后 → 是我们自己（或调用方）在攒，必须修。

using System.Diagnostics;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Agent.SelfTest;

internal static class StreamingTests
{
    /// <summary>假服务端吐多少块。</summary>
    private const int Frames = 15;

    /// <summary>块与块之间的间隔（毫秒）—— 服务端一侧的真实节奏。</summary>
    private const int GapMs = 50;

    /// <summary>宽容度：机器上可能有别的负载，单块延迟放它到 300ms 以内都算"及时"。</summary>
    private const int ToleranceMs = 300;

    public static async Task IncrementalDeliveryAsync()
    {
        var spread = (Frames - 1) * GapMs;   // 750ms：真正的"铺开"应该有这么长

        using var server = SlowMockServer.Start(Frames, GapMs);
        using var provider = Provider.For(server.BaseUrl);
        var session = new AgentSession(provider, new ToolRegistry());

        var arrivals = new List<long>();
        var texts = new List<string>();
        var clock = Stopwatch.StartNew();

        await foreach (var evt in session.SendAsync("answer slowly, piece by piece"))
        {
            if (evt is AgentTextDelta delta)
            {
                arrivals.Add(clock.ElapsedMilliseconds);
                texts.Add(delta.Text);
            }
        }

        clock.Stop();

        // ---------------- Provider 层实测数字 ----------------
        var timing = provider.LastStreamTiming;
        if (timing is null)
        {
            Program.Fail("Provider 没有留下 LastStreamTiming —— 时序埋点没生效");
            return;
        }

        Program.Info($"服务端节奏: {Frames} 块 × {GapMs}ms（应铺开约 {spread}ms）");
        Program.Info($"Provider 实测: {timing}");
        Program.Info("Provider 增量间隔(ms): " + string.Join(", ", timing.TextDeltaGaps.Select(g => g.TotalMilliseconds.ToString("0"))));
        Program.Info($"会话层实测: 首个增量 {arrivals.FirstOrDefault()}ms，最后一个 {arrivals.LastOrDefault()}ms，" +
                     $"共 {arrivals.Count} 个，铺开 {arrivals.LastOrDefault() - arrivals.FirstOrDefault()}ms");
        Program.Info("会话层增量到达(ms): " + string.Join(", ", arrivals));

        Program.Check(timing.FirstByte is not null, "Provider 记到了首字节时间");
        Program.Check(timing.FirstTextDelta is not null, "Provider 记到了首个文本增量时间");
        Program.Check(
            timing.TextDeltaCount == Frames,
            $"Provider 数到 {Frames} 个文本增量（实际 {timing.TextDeltaCount}）");
        Program.Check(
            timing.TextDeltaGaps.Count == Frames - 1,
            $"Provider 记到 {Frames - 1} 个增量间隔（实际 {timing.TextDeltaGaps.Count}）");

        var providerGaps = timing.TextDeltaGaps.Select(g => g.TotalMilliseconds).ToList();
        Program.Check(
            providerGaps.Count > 0 && providerGaps.All(g => g > GapMs * 0.4 && g < GapMs + ToleranceMs),
            $"Provider 的增量间隔都贴近服务端的 {GapMs}ms 节奏（min {providerGaps.DefaultIfEmpty(0).Min():0} / " +
            $"max {providerGaps.DefaultIfEmpty(0).Max():0} ms）—— 网络层确实是一块一块来的");

        // ---------------- AgentSession 层：是不是"逐个、及时" ----------------
        Program.Check(
            texts.Count == Frames && string.Concat(texts) == SlowMockServer.FullText(Frames),
            $"会话层收到 {Frames} 个 AgentTextDelta，拼起来 == 服务端发的全文（实际 {texts.Count} 个）");

        Program.Check(
            arrivals.Count == 0 || arrivals[0] <= GapMs + ToleranceMs,
            $"首个文本增量 {arrivals.FirstOrDefault()}ms 就到达了（服务端 {server.SentAt.FirstOrDefault()}ms 发的）—— " +
            "不是攒到整轮结束才给");

        Program.Check(
            arrivals.Count >= 2 && arrivals[^1] - arrivals[0] >= spread * 0.6,
            $"会话层的增量确实铺开了 {arrivals.LastOrDefault() - arrivals.FirstOrDefault()}ms（服务端铺开 {spread}ms）—— 逐个冒出来");

        var sessionGaps = new List<long>();
        for (var i = 1; i < arrivals.Count; i++)
        {
            sessionGaps.Add(arrivals[i] - arrivals[i - 1]);
        }

        Program.Check(
            sessionGaps.Count > 0 && sessionGaps.All(g => g is > GapMs / 2 and < GapMs + ToleranceMs),
            $"相邻增量之间没有长停顿（max {sessionGaps.DefaultIfEmpty(0).Max()}ms，服务端 {GapMs}ms）");

        // 最强的一条反证：把服务端每一块的发出时刻和客户端看到它的时刻对齐 ——
        // 若我们攒到最后再吐，后面的块会出现"我看它比它发出还早"的负延迟。
        var sentAt = server.SentAt;
        if (sentAt.Count == arrivals.Count && arrivals.Count > 0)
        {
            var offset = arrivals[0] - sentAt[0];
            var latencies = new List<long>();
            for (var i = 0; i < arrivals.Count; i++)
            {
                latencies.Add(arrivals[i] - sentAt[i] - offset);
            }

            Program.Info("逐块延迟(ms，相对第一块对齐): " + string.Join(", ", latencies));
            Program.Check(
                latencies.All(l => l > -GapMs && l < ToleranceMs),
                $"每一块从服务端发出到界面看到都在 {ToleranceMs}ms 内（max {latencies.Max()}ms）—— 没有攒批");
        }
        else
        {
            Program.Fail($"服务端发出 {sentAt.Count} 块 / 会话层收到 {arrivals.Count} 个增量，对不上");
        }
    }
}
