// 三条护栏里的两条（DPI 那条在 Native.cs / PotatoAgentWin32.cs）：
//
//   护栏一 · 焦点门禁（最重要）：
//       任何键盘/鼠标注入之前，回读 GetForegroundWindow() 核对是不是目标窗口；
//       不对就先尝试聚焦，再回读一次；仍然不对 → 直接返回失败，绝不注入。
//       真实事故：少做了这一步，103 个字符打进了用户的浏览器。
//
//   护栏三 · 权限天花板：
//       目标以管理员运行 / UAC 弹窗 / 锁屏 → 注入不进去是正常现象。
//       明确报错说明，不死磕、不重试（本文件里没有任何重试循环，这是刻意的）。
//
// 注入本身走 InputSender.Send：SendInput 的返回值 + GetLastError 一起看，
// 事件数对不上就是失败，绝不假装成功。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PotatoAgent.Win32;

public static class FocusGuard
{
    /// <summary>
    /// 门禁本体。返回 Ok 才允许注入。
    /// <paramref name="target"/> 是调用方声明的目标窗口，必须非零 ——
    /// 不声明目标就等于"往当前前台随便打"，那是事故的起点，直接拒绝。
    /// </summary>
    public static ActionResult Check(IntPtr target, string action, bool attemptFocus = true)
    {
        if (target == IntPtr.Zero)
            return ActionResult.Failure(
                $"{action}: REFUSED — no target window was declared (target=0x00000000). " +
                "Refusing to inject into whatever happens to be in the foreground.");

        if (!Native.IsWindow(target))
            return ActionResult.Failure($"{action}: REFUSED — the target window no longer exists (stale handle {Format(target)}).");

        // 步骤 1：回读前台
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground == target)
            return ActionResult.Success($"{action}: gate passed — target {Desktop.Describe(target)} is already the foreground window.");

        if (!attemptFocus)
            return ActionResult.Failure(
                $"{action}: REFUSED to inject — foreground is {Desktop.Describe(foreground)}, " +
                $"but the target is {Desktop.Describe(target)}.");

        // 步骤 2：先尝试聚焦
        var focus = Focus(target, out string how);

        // 步骤 3：再回读一次
        IntPtr again = Native.GetForegroundWindow();
        if (again == target)
            return ActionResult.Success($"{action}: gate passed — focused the target via {how}.");

        // 步骤 3 失败：绝不注入
        return ActionResult.Failure(
            $"{action}: REFUSED to inject — the target {Desktop.Describe(target)} could not be focused " +
            $"({focus.Message}); the foreground is still {Desktop.Describe(again)}. " +
            "Nothing was injected.");
    }

    /// <summary>
    /// 把窗口弄到前台。阶梯式尝试，每一级最多做一次，绝不循环重试：
    ///   a) SetForegroundWindow（最小化先 SW_RESTORE；没最小化只 SW_SHOW，不动用户的窗口尺寸）
    ///   b) AttachThreadInput 挂到目标线程上，再 BringWindowToTop + SetForegroundWindow + SetFocus
    ///   c) 临时把前台锁定超时清零再 SetForegroundWindow，然后立刻还原
    /// </summary>
    public static ActionResult Focus(IntPtr target, out string how)
    {
        how = "none";

        if (target == IntPtr.Zero)
            return ActionResult.Failure("FocusWindow: target handle is null");
        if (!Native.IsWindow(target))
            return ActionResult.Failure($"FocusWindow: {Format(target)} is not a window (it was closed)");
        if (Native.IsCloaked(target))
            return ActionResult.Failure($"FocusWindow: {Desktop.Describe(target)} is cloaked (suspended UWP app or another virtual desktop)");

        if (Native.GetForegroundWindow() == target)
        {
            how = "already-foreground";
            return ActionResult.Success("already the foreground window");
        }

        // 护栏三：先把"天花板"问清楚，别对着提权窗口白打一遍。
        string? ceiling = Privilege.Ceiling(target);
        if (ceiling is not null) return ActionResult.Failure($"FocusWindow: {ceiling}");

        if (Native.IsIconic(target)) NativeAction.ShowWindow(target, NativeAction.SW_RESTORE);
        else NativeAction.ShowWindow(target, NativeAction.SW_SHOW);

        // ---- a) 最朴素的一招 ----
        NativeAction.SetForegroundWindow(target);
        if (Native.GetForegroundWindow() == target)
        {
            how = "SetForegroundWindow";
            return ActionResult.Success("focused with SetForegroundWindow");
        }

        // ---- b) 挂输入队列 ----
        // 关键细节（第一次实测就是栽在这里）：前台锁是按【线程】持有的，
        // 而且持锁的是"当前拥有前台窗口的那个线程"。所以必须挂到【前台线程】上，
        // 系统才会承认我们有权改前台；只挂目标线程只能让我们在目标里 SetFocus，改不了前台。
        // 两个都挂上，收尾时反序摘掉。
        uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        uint targetThread = Native.GetWindowThreadProcessId(target, out _);
        uint ourThread = NativeAction.GetCurrentThreadId();

        bool attachedForeground = foregroundThread != 0 && foregroundThread != ourThread
                                  && NativeAction.AttachThreadInput(ourThread, foregroundThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != ourThread && targetThread != foregroundThread
                              && NativeAction.AttachThreadInput(ourThread, targetThread, true);
        try
        {
            NativeAction.BringWindowToTop(target);
            NativeAction.SetForegroundWindow(target);
            NativeAction.SetFocus(target);
        }
        finally
        {
            // 一定要摘下来，否则两个线程的输入队列被绑在一起，后续行为会很怪。
            if (attachedTarget) NativeAction.AttachThreadInput(ourThread, targetThread, false);
            if (attachedForeground) NativeAction.AttachThreadInput(ourThread, foregroundThread, false);
        }

        if (Native.GetForegroundWindow() == target)
        {
            how = "AttachThreadInput";
            return ActionResult.Success("focused with AttachThreadInput (foreground thread + target thread)");
        }

        // ---- c) 最后一级：临时解锁"前台锁定超时"，用完还原；再挂一次前台线程 ----
        if (NativeAction.TryReadForegroundLockTimeout(out uint saved) && saved != 0)
        {
            uint current = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
            bool attached = current != 0 && current != ourThread && NativeAction.AttachThreadInput(ourThread, current, true);
            try
            {
                NativeAction.SetForegroundLockTimeout(0);
                NativeAction.SetForegroundWindow(target);
            }
            finally
            {
                if (attached) NativeAction.AttachThreadInput(ourThread, current, false);
                NativeAction.SetForegroundLockTimeout(saved);
            }

            if (Native.GetForegroundWindow() == target)
            {
                how = "foreground-lock-timeout";
                return ActionResult.Success("focused after temporarily zeroing the foreground lock timeout");
            }
        }

        return ActionResult.Failure(
            $"could not bring {Desktop.Describe(target)} to the foreground; " +
            $"the foreground is still {Desktop.Describe(Native.GetForegroundWindow())}");
    }

    internal static string Format(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X8");
}

/// <summary>护栏三：权限天花板。只负责"说清楚"，不负责"想办法突破"。</summary>
public static class Privilege
{
    private static readonly Lazy<bool> Elevated = new(() => NativeAction.IsElevated(0) == true);

    /// <summary>本进程自己是不是管理员。</summary>
    public static bool SelfElevated => Elevated.Value;

    /// <summary>某个窗口所属进程是否以管理员运行；读不到返回 null。</summary>
    public static bool? IsTargetElevated(IntPtr hwnd) => NativeAction.IsElevated(Native.ProcessId(hwnd));

    /// <summary>
    /// 返回"天花板的原因"，null 表示没有天花板。
    /// 有值就代表：再怎么试也进不去，调用方应当直接失败并如实告诉用户。
    /// </summary>
    public static string? Ceiling(IntPtr target)
    {
        // 锁屏 / UAC 安全桌面：输入桌面都不是我们这一个，注入必然石沉大海。
        if (!NativeAction.InputDesktopAccessible())
            return "the input desktop is not reachable from this process — this is almost always a locked session " +
                   "or a UAC prompt / secure desktop. Injected input cannot be delivered in that state; " +
                   "wait for the user, and do NOT retry.";

        if (target == IntPtr.Zero) return null;

        int pid = Native.ProcessId(target);
        bool? elevated = NativeAction.IsElevated(pid);
        if (elevated == true && !SelfElevated)
        {
            string name = ProcessName(pid);
            return $"the target {name}#{pid} runs elevated while this process does not — Windows UIPI blocks " +
                   "injected keys and mouse events across integrity levels, so SendInput will fail with " +
                   "ERROR_ACCESS_DENIED (5). This is expected behaviour, not a bug: run the agent elevated " +
                   "if you really need to drive that window. Do NOT retry.";
        }

        return null;
    }

    /// <summary>把 SendInput 的 Win32 错误码翻译成人话。</summary>
    internal static string Explain(int error) => error switch
    {
        NativeAction.ERROR_ACCESS_DENIED =>
            " (ERROR_ACCESS_DENIED — UIPI refused the input: the foreground window belongs to a " +
            "higher-integrity process, e.g. one started as administrator. Not retryable.)",
        0 => " (no error reported — input was blocked, typically by BlockInput or a secure desktop)",
        87 => " (ERROR_INVALID_PARAMETER — this is a bug in the calling code, not the environment)",
        _ => string.Empty,
    };

    private static string ProcessName(int pid)
    {
        try
        {
            return pid > 0 ? Process.GetProcessById(pid).ProcessName : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}

/// <summary>统一的注入出口：SendInput 的返回值就是成功/失败的唯一依据。</summary>
internal static class InputSender
{
    /// <summary>
    /// 送一批输入事件。全部送进去才算成功；一个没进去就是失败，并带上 Win32 错误码的解释。
    /// 注意：这里不重试。失败是事实，交给调用方决定怎么办。
    /// </summary>
    internal static ActionResult Send(NativeAction.INPUT[] inputs, string action)
    {
        if (inputs.Length == 0) return ActionResult.Failure($"{action}: nothing to send (0 input events were built)");

        uint sent = NativeAction.SendInput((uint)inputs.Length, inputs, NativeAction.InputSize);
        if (sent == inputs.Length)
            return ActionResult.Success($"{action}: {sent} input event(s) delivered");

        int error = Marshal.GetLastWin32Error();

        if (sent == 0)
            return ActionResult.Failure(
                $"{action}: SendInput delivered 0 of {inputs.Length} event(s), Win32 error {error}{Privilege.Explain(error)}");

        return ActionResult.Failure(
            $"{action}: SendInput delivered only {sent} of {inputs.Length} event(s), Win32 error {error}{Privilege.Explain(error)}");
    }
}
