// "刚刚是不是已经有人启动过这个程序了？" —— pc_launch 的第二道防线。
//
// 三道防线各管一段：
//   1) AgentSession：同一回合内，同名工具 + 同一个程序身份只启动一次（参数换个写法也拦得住）；
//   2) 本文件：进程内记账 —— 一次启动还没等到窗口（或刚等到）时，同一程序不许再启动，
//      第二次进来只会【等】那次启动的窗口，然后如实说明"什么都没新起"；
//   3) RunningProgram：目标程序本来就在跑（连进程都没有窗口的那种）→ 聚焦已有窗口。
//
// 为什么需要第 2 道：一次启动的耗时里有一大段"窗口还没画出来"的空档
//（打包应用尤其明显：启动器进程退场、UI 进程再起、WinUI 才把窗口画出来）。
// 那段空档里"已经在跑"是查不到的 —— 模型只要在这会儿再调一次，就会真的再起一个。
//
// 记账是进程内静态表，边界清清楚楚：
//   * 窗口还活着 → 记着（重复调用会被导向那个窗口）；
//   * 窗口没了、进程也没了、而且已经过了等待期 → 自动作废，下次照常能启动。

using System.Diagnostics;

namespace PotatoAgent.Win32.Tools;

/// <summary>账本里的一条：某个程序被我们启动过，现在到哪一步了。</summary>
internal sealed record LaunchClaim(string Identity, string Display, string Stem, int Pid, IntPtr Window, long ClaimedAt)
{
    /// <summary>
    /// 这次认领还有效吗：窗口还在（而且确实还是那个程序的窗口）/ 进程还在 / 刚认领还没过等待期。
    /// 窗口句柄是会被系统回收复用的，所以除了 IsWindow 还要核对它属于哪个程序 ——
    /// 否则一个复用了旧句柄的无关窗口会让这条记录永远"活着"，那个程序就再也启动不了。
    /// </summary>
    internal bool IsLive(long now)
    {
        if (Window != IntPtr.Zero)
        {
            return Native.IsWindow(Window) && ProgramMatch.MatchesWindow(Window, Stem);
        }

        return Pid > 0
            ? ProgramMatch.ProcessAlive(Pid)
            : now - ClaimedAt < LaunchLedger.PendingTtlMs;
    }
}

/// <summary>pc_launch 的"同一程序不许启动第二次"账本。</summary>
internal static class LaunchLedger
{
    /// <summary>还没等到窗口的认领最多留这么久；超时就当那次启动已经失败，允许重新启动。</summary>
    internal const int PendingTtlMs = 30_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, LaunchClaim> Claims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 认领一次启动。返回 null = 认领成功，可以放心启动；
    /// 返回一条记录 = 这个程序已经有人启动过（或正在启动），<b>不要</b>再启动，照那条记录处理。
    /// </summary>
    internal static LaunchClaim? Claim(string identity, string display, string stem)
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;

            if (Claims.TryGetValue(identity, out var existing))
            {
                if (existing.IsLive(now))
                {
                    return existing;
                }

                // 死的记录：窗口没了、进程也没了、等待期也过了 —— 让位给这一次启动。
                Claims.Remove(identity);
            }

            Claims[identity] = new LaunchClaim(identity, display, stem, 0, IntPtr.Zero, now);
            return null;
        }
    }

    /// <summary>把等到的新窗口写回账本。</summary>
    internal static void Resolve(string identity, int pid, IntPtr window)
    {
        lock (Gate)
        {
            if (Claims.TryGetValue(identity, out var claim))
            {
                Claims[identity] = claim with { Pid = pid, Window = window };
            }
        }
    }

    /// <summary>这次启动失败：把认领撤掉，别挡住下一次。</summary>
    internal static void Release(string identity)
    {
        lock (Gate)
        {
            Claims.Remove(identity);
        }
    }
}
