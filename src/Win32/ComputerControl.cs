// 动作层本体：把"点一下 / 打几个字"变成可验证的成功或失败。
//
// 三条不可谈判的规矩：
//   1) 每个方法都返回 ActionResult（或 LaunchOutcome），成功失败必须明说；
//   2) 每一次注入之前都过 FocusGuard 门禁，过不去就【什么都不做】直接失败；
//   3) 不重试。失败是事实，报给调用方，由它决定怎么办。
//
// 键鼠一律走 SendInput；只有 TypeText 例外 —— 它首选 UIA 的 ValuePattern.SetValue，
// 拿不到 UIA 才退回 SendInput 逐字输入（KEYEVENTF_UNICODE，中文靠这个）。

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PotatoAgent.Win32;

public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>启动一个程序的结果。<see cref="WindowHandle"/> 可能是 Zero（窗口还没出来）。</summary>
public readonly record struct LaunchOutcome(bool Ok, string Message, int ProcessId, IntPtr WindowHandle)
{
    public override string ToString() => (Ok ? "OK   " : "FAIL ") + Message;
}

/// <summary>
/// 一个目标窗口上的鼠标 / 键盘 / 文本 / 聚焦 / 启动动作。
/// 默认只对 <see cref="Target"/> 这一个窗口动手 —— 这是那 103 个字符事故换来的默认值。
/// </summary>
public sealed class ComputerControl
{
    public ComputerControl(IntPtr target = default) => Target = target;

    /// <summary>目标窗口。门禁核对的就是它。Zero 表示没声明目标 —— 那样一切注入都会被拒绝。</summary>
    public IntPtr Target { get; set; }

    /// <summary>
    /// 焦点门禁开关。默认 true 且是 init-only：中途翻不掉，免得"临时关一下"变成永久关掉。
    /// 只有在确实没有目标窗口（例如对整个桌面做手势）时才显式设成 false。
    /// </summary>
    public bool RequireFocus { get; init; } = true;

    /// <summary>可选日志出口，每一步的真实结果都会写进去。</summary>
    public Action<string>? Trace { get; init; }

    // ==================== 鼠标 ====================

    /// <summary>把指针移到屏幕物理坐标 (x,y)。</summary>
    public ActionResult MoveTo(int x, int y)
    {
        string action = $"MoveTo({x},{y})";
        var gate = Gate(action);
        if (!gate.Ok) return gate;

        var (dx, dy) = Normalize(x, y);
        var sent = Inject(new[] { MouseInput(MoveFlags, dx, dy, 0) }, action);
        if (!sent.Ok) return sent;

        Native.GetCursorPos(out POINT cursor);
        if (Math.Abs(cursor.X - x) > 1 || Math.Abs(cursor.Y - y) > 1)
            return ActionResult.Failure($"{action}: the move was delivered but the pointer is at ({cursor.X},{cursor.Y})");

        return ActionResult.Success($"{action}: pointer at ({cursor.X},{cursor.Y})");
    }

    /// <summary>在屏幕物理坐标 (x,y) 单击一下。</summary>
    public ActionResult Click(int x, int y, MouseButton button = MouseButton.Left)
    {
        string action = $"Click({x},{y},{button})";
        var gate = Gate(action);
        if (!gate.Ok) return gate;

        var (down, up) = ButtonFlags(button);
        var (dx, dy) = Normalize(x, y);

        // 移动 + 按下 + 抬起放进同一批：按下时指针已经在目标点上，中间没有可乘之机。
        var inputs = new[]
        {
            MouseInput(MoveFlags, dx, dy, 0),
            MouseInput(down, 0, 0, 0),
            MouseInput(up, 0, 0, 0),
        };

        var sent = Inject(inputs, action);
        if (!sent.Ok) return sent;

        // 注入成功 ≠ 落点正确，回读指针：偏了就是失败，绝不让调用方以为点到了。
        Native.GetCursorPos(out POINT cursor);
        if (Math.Abs(cursor.X - x) > 1 || Math.Abs(cursor.Y - y) > 1)
            return ActionResult.Failure($"{action}: the click was delivered, but the pointer landed at ({cursor.X},{cursor.Y}), not ({x},{y})");

        return ActionResult.Success($"{action}: {button} click delivered at ({cursor.X},{cursor.Y})");
    }

    /// <summary>
    /// 双击。两次点击在同一批事件里发出，间隔远小于系统双击时间（GetDoubleClickTime），
    /// 因此会被正确识别成一次双击而不是两次单击。
    /// </summary>
    public ActionResult DoubleClick(int x, int y, MouseButton button = MouseButton.Left)
    {
        string action = $"DoubleClick({x},{y},{button})";
        var gate = Gate(action);
        if (!gate.Ok) return gate;

        var (down, up) = ButtonFlags(button);
        var (dx, dy) = Normalize(x, y);

        var inputs = new[]
        {
            MouseInput(MoveFlags, dx, dy, 0),
            MouseInput(down, 0, 0, 0),
            MouseInput(up, 0, 0, 0),
            MouseInput(down, 0, 0, 0),
            MouseInput(up, 0, 0, 0),
        };

        var sent = Inject(inputs, action);
        if (!sent.Ok) return sent;

        Native.GetCursorPos(out POINT cursor);
        if (Math.Abs(cursor.X - x) > 1 || Math.Abs(cursor.Y - y) > 1)
            return ActionResult.Failure($"{action}: delivered, but the pointer landed at ({cursor.X},{cursor.Y})");

        return ActionResult.Success($"{action}: double click delivered at ({cursor.X},{cursor.Y}) (interval well under {NativeAction.GetDoubleClickTime()}ms)");
    }

    /// <summary>从一点拖到另一点。中途插值若干步，并保持按下状态直到抬起。</summary>
    public ActionResult Drag(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, int steps = 12)
    {
        string action = $"Drag(({fromX},{fromY})->({toX},{toY}),{button})";
        if (steps < 1) return ActionResult.Failure($"{action}: steps must be >= 1");

        var gate = Gate(action);
        if (!gate.Ok) return gate;

        var (down, up) = ButtonFlags(button);
        var (startX, startY) = Normalize(fromX, fromY);

        var pressed = Inject(
            new[] { MouseInput(MoveFlags, startX, startY, 0), MouseInput(down, 0, 0, 0) },
            action + " press");
        if (!pressed.Ok) return pressed;

        for (int step = 1; step <= steps; step++)
        {
            int x = fromX + (toX - fromX) * step / steps;
            int y = fromY + (toY - fromY) * step / steps;
            var (dx, dy) = Normalize(x, y);

            var moved = Inject(new[] { MouseInput(MoveFlags, dx, dy, 0) }, $"{action} step {step}/{steps}");
            if (!moved.Ok)
            {
                ReleaseButton(button, action);
                return ActionResult.Failure($"{action}: aborted at step {step}/{steps} — {moved.Message} (the button was released)");
            }

            Thread.Sleep(10);
        }

        var released = Inject(new[] { MouseInput(up, 0, 0, 0) }, action + " release");
        if (!released.Ok)
        {
            ReleaseButton(button, action);
            return ActionResult.Failure($"{action}: the drag reached the target but the button release failed — {released.Message}");
        }

        return ActionResult.Success($"{action}: dragged in {steps} step(s)");
    }

    public ActionResult Drag(POINT from, POINT to, MouseButton button = MouseButton.Left, int steps = 12) =>
        Drag(from.X, from.Y, to.X, to.Y, button, steps);

    /// <summary>
    /// 滚轮。amount 是"格"数：正数向上/向左，负数向下/向右；一格 = WHEEL_DELTA(120)。
    /// 注意滚轮事件跟着【指针】走而不是跟着前台窗口走，所以这里额外核对指针确实在目标窗口里。
    /// </summary>
    public ActionResult Scroll(int amount, bool horizontal = false)
    {
        string action = $"Scroll({amount}{(horizontal ? ",horizontal" : "")})";
        if (amount == 0) return ActionResult.Failure($"{action}: amount is 0, there is nothing to scroll");

        var gate = Gate(action);
        if (!gate.Ok) return gate;

        Native.GetCursorPos(out POINT cursor);
        if (!InsideWindow(Target, cursor))
            return ActionResult.Failure(
                $"{action}: REFUSED — the pointer is at ({cursor.X},{cursor.Y}), outside the target {Desktop.Describe(Target)}. " +
                "Wheel events follow the pointer, not the foreground window, so this would have scrolled something else. " +
                "MoveTo() into the window first.");

        uint flag = horizontal ? NativeAction.MOUSEEVENTF_HWHEEL : NativeAction.MOUSEEVENTF_WHEEL;
        int delta = (amount > 0 ? 1 : -1) * NativeAction.WHEEL_DELTA;

        var inputs = new NativeAction.INPUT[Math.Abs(amount)];
        for (int i = 0; i < inputs.Length; i++) inputs[i] = MouseInput(flag, 0, 0, (uint)delta);

        return Inject(inputs, action);
    }

    // ==================== 键盘 ====================

    /// <summary>发送按键表达式，见 Keys.cs 顶部的语法说明。走 SendInput，整串一次发出。</summary>
    /// <remarks>
    /// ⚠ SendKeys 发的是【按键】，不是【文本】。按键会经过目标窗口当前生效的输入法：
    /// 中文输入法开着的时候，字母键会被当成拼音组合成汉字 —— 本机实测 SendKeys("abc123")
    /// 打进 Win11 记事本后文档里出现的是"按不出"。
    /// 这【不是】注入失败：真人在这台机器上敲同样的键，输入法给出的结果一模一样
    /// （SendInput 的 12 个事件一个不少地投递成功了）。
    /// 所以：快捷键（^a、{DELETE}、{ESC}、^s 这类）用 SendKeys 没问题，都实测可用；
    /// 要输入确定的文本请用 <see cref="TypeText"/> —— UIA 与 Unicode 两条通道都绕开输入法。
    /// </remarks>
    public ActionResult SendKeys(string keys)
    {
        string action = $"SendKeys(\"{keys}\")";
        if (string.IsNullOrEmpty(keys)) return ActionResult.Failure($"{action}: keys is empty");

        var gate = Gate(action);
        if (!gate.Ok) return gate;

        List<KeyStroke> strokes;
        try
        {
            strokes = KeyParser.Parse(keys);
        }
        catch (ArgumentException error)
        {
            return ActionResult.Failure($"{action}: {error.Message}");
        }

        var inputs = new List<NativeAction.INPUT>(strokes.Count * 8);
        foreach (KeyStroke stroke in strokes) AppendStroke(inputs, stroke);

        var sent = Inject(inputs.ToArray(), action);
        if (!sent.Ok) return sent;
        return ActionResult.Success($"{action}: {strokes.Count} keystroke(s) delivered");
    }

    /// <summary>
    /// 输入一段文本。首选 UIA 的 ValuePattern.SetValue，拿不到 UIA 才退回 SendInput 逐字输入
    /// （KEYEVENTF_UNICODE，中文能用）。
    /// <para>
    /// ⚠ 两条路径的语义不同，调用方必须知道：
    ///   * UIA 路径 = 【整篇覆盖】。ValuePattern.SetValue 是"把整个文档设成这个值"，
    ///     不是"在光标处插入"，所以它会清掉原有内容、也不会理会插入符在哪；
    ///   * SendInput 路径 = 【在光标处插入】，像人打字一样。
    /// </para>
    /// 要"往光标处补一段"就用 preferUia: false；要"把内容设成这个"就用默认的 true。
    /// 两条路径都会回读校验，读完不一致就报失败 —— 不拿"没抛异常"当成功。
    /// </para>
    /// <para>
    /// ⚠ 换行请写 "\r\n"。本机实测：只写一个 "\n" 会被目标控件直接丢掉
    /// （Win11 记事本里 "AAAA\nBBBB" 落进去变成 "AAAABBBB"）。
    /// </para>
    /// <para>
    /// ⚠ 回读校验会先把行尾归一化再比。RichEdit 内部把 CRLF 存成单个 CR，
    /// 拿回来的是 "AAAA\rBBBB"，跟请求的 "AAAA\r\nBBBB" 逐字符比会误判成失败 ——
    /// 本机实测踩过这个坑，所以比较走 Normalize（CRLF/CR → LF）。
    /// </para>
    /// </summary>
    public ActionResult TypeText(string text, bool preferUia = true)
    {
        string action = $"TypeText({text?.Length ?? 0} char(s))";
        if (text is null) return ActionResult.Failure($"{action}: text is null");

        var gate = Gate(action);
        if (!gate.Ok) return gate;

        if (preferUia)
        {
            if (UiaText.TrySetValue(Target, text, out string message))
            {
                if (UiaText.TryRead(Target, out string readback, out string via))
                {
                    if (SameText(readback, text))
                        return ActionResult.Success($"{action}: written with {message}, verified by reading back {via}");

                    return ActionResult.Failure(
                        $"{action}: {message} reported success, but reading back with {via} returned " +
                        $"{readback.Length} char(s) that do not match what was asked for. Nothing else was typed.");
                }

                return ActionResult.Success($"{action}: written with {message} (no read-back verification available: {UiaText.Describe(Target)})");
            }

            Trace?.Invoke($"{action}: UIA path unavailable ({message}) — falling back to SendInput");
        }

        // 逐字：一个字符一发 SendInput（含 UTF-16 码元），字与字之间留一点间隔。
        // 为什么不是"26 个事件一把塞进去"：实测发现目标窗口里若有中文输入法（TSF）在跑，
        // 一次性灌进去的事件会被输入法当成拼音串吞掉、延迟甚至乱序提交。
        // 慢一点、一次一个，才是它真正能跟上的节奏 —— 这也正是"逐字输入"的本意。
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            var pair = new[]
            {
                KeyInput(0, character, NativeAction.KEYEVENTF_UNICODE),
                KeyInput(0, character, NativeAction.KEYEVENTF_UNICODE | NativeAction.KEYEVENTF_KEYUP),
            };

            var sent = Inject(pair, $"{action} char {index + 1}/{text.Length}");
            if (!sent.Ok) return sent;

            if (index + 1 < text.Length) Thread.Sleep(CharacterGapMs);
        }

        // 等目标把输入队列消化完再回读 —— 这不是"重试注入"，注入已经成功了，
        // 这里只是等应用把字真的落进文档里。等不到就是失败，绝不假装成功。
        if (UiaText.TryRead(Target, out string content, out string fallbackVia))
        {
            string wanted = Normalize(text);
            long deadline = Environment.TickCount64 + 1500;
            while (!Normalize(content).Contains(wanted, StringComparison.Ordinal) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(100);
                if (!UiaText.TryRead(Target, out content, out fallbackVia)) break;
            }

            if (!Normalize(content).Contains(wanted, StringComparison.Ordinal))
                return ActionResult.Failure(
                    $"{action}: all {text.Length} character(s) were delivered by SendInput, but after waiting 1.5s " +
                    $"the target still reads \"{content}\" ({fallbackVia}) and does not contain what was asked for. " +
                    "Injected characters can be swallowed or reordered by an active IME — prefer the UIA path.");

            return ActionResult.Success($"{action}: typed character by character with SendInput(KEYEVENTF_UNICODE), verified by reading back {fallbackVia}");
        }

        return ActionResult.Success($"{action}: typed character by character with SendInput(KEYEVENTF_UNICODE)");
    }

    // ==================== 窗口 / 进程 ====================

    /// <summary>
    /// 把窗口弄到前台。阶梯：ShowWindow(SW_RESTORE) + SetForegroundWindow →
    /// AttachThreadInput 再试 → 临时清零前台锁定超时。聚焦成功即认领为 Target。
    /// </summary>
    public ActionResult FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return ActionResult.Failure("FocusWindow: target handle is null");

        var result = FocusGuard.Focus(hwnd, out string how);
        if (!result.Ok) return ActionResult.Failure($"FocusWindow: {result.Message}");

        Target = hwnd;
        Trace?.Invoke($"FocusWindow: {Desktop.Describe(hwnd)} via {how}");
        return ActionResult.Success($"FocusWindow: {result.Message} (via {how}); Target is now {Desktop.Describe(hwnd)}");
    }

    /// <summary>
    /// 启动一个程序（ShellExecuteEx）。Ok 表示"确实起来了"，
    /// WindowHandle 是尽力等到的主窗口（等不到就是 Zero，不是失败）。
    /// </summary>
    public LaunchOutcome Launch(string path, string? arguments = null, string? workingDirectory = null, int waitForWindowMs = 8000)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new LaunchOutcome(false, "Launch: path is required", 0, IntPtr.Zero);

        // 启动前先拍快照：等下只认"新冒出来的窗口"。
        // 否则用户本来就开着一个记事本时，我们会把人家那个认成自己启动的。
        var before = Desktop.VisibleWindows();

        var info = new NativeAction.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<NativeAction.SHELLEXECUTEINFO>(),
            fMask = NativeAction.SEE_MASK_NOCLOSEPROCESS | NativeAction.SEE_MASK_FLAG_NO_UI | NativeAction.SEE_MASK_NOASYNC,
            lpVerb = "open",
            lpFile = path,
            lpParameters = string.IsNullOrEmpty(arguments) ? null : arguments,
            lpDirectory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            nShow = NativeAction.SW_SHOWNORMAL,
        };

        if (!NativeAction.ShellExecuteEx(ref info))
        {
            int error = Marshal.GetLastWin32Error();
            return new LaunchOutcome(false, $"Launch(\"{path}\"): ShellExecuteEx failed with Win32 error {error}", 0, IntPtr.Zero);
        }

        int pid = 0;
        if (info.hProcess != IntPtr.Zero)
        {
            pid = NativeAction.GetProcessId(info.hProcess);
            NativeAction.CloseHandle(info.hProcess);
        }

        // 一次等待、两个条件，共用一个截止时间：
        // 打包应用（记事本/画图这类）常常是"启动器进程退场、UI 进程另起一个"，
        // 只按 pid 找必然扑空，所以补一条"按可执行文件名匹配"。
        // 两个条件是 OR，不是先等满一个再等另一个 —— 那样第一个就会把预算吃光。
        string stem = Path.GetFileNameWithoutExtension(path);
        IntPtr window = Desktop.WaitFor(
            hwnd => !before.Contains(hwnd) && (Native.ProcessId(hwnd) == pid || MatchesStem(hwnd, stem)),
            Math.Max(0, waitForWindowMs));

        string message = window != IntPtr.Zero
            ? $"Launch(\"{path}\"): started pid {pid}, window {Desktop.Describe(window)}"
            : $"Launch(\"{path}\"): started pid {pid}, but no NEW window appeared within {waitForWindowMs}ms";

        return new LaunchOutcome(true, message, pid, window);
    }

    // ==================== 内部 ====================

    /// <summary>门禁。RequireFocus=false 是显式拆护栏，日志里必须留痕。</summary>
    private ActionResult Gate(string action)
    {
        if (!RequireFocus) return ActionResult.Success($"{action}: focus gate DISABLED (RequireFocus=false)");
        return FocusGuard.Check(Target, action);
    }

    /// <summary>
    /// 注入出口。除了 SendInput 本身，这里还做最后一道 TOCTOU 核对：
    /// 从门禁通过到真正发事件之间，前台窗口可能被用户换掉 —— 那就再拦一次。
    /// </summary>
    private ActionResult Inject(NativeAction.INPUT[] inputs, string action)
    {
        if (RequireFocus)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != Target)
            {
                var result = ActionResult.Failure(
                    $"{action}: REFUSED at the last moment — the foreground window changed to " +
                    $"{Desktop.Describe(foreground)} (target is {Desktop.Describe(Target)}). Nothing was injected.");
                Trace?.Invoke(result.ToString());
                return result;
            }
        }

        var sent = InputSender.Send(inputs, action);
        Trace?.Invoke(sent.ToString());
        return sent;
    }

    /// <summary>
    /// 失败收尾用的"抬起鼠标键"。故意不走门禁重核对：把用户的手指（指针）留在按下状态，
    /// 比多发一个抬起事件危险得多。
    /// </summary>
    private void ReleaseButton(MouseButton button, string action)
    {
        var (_, up) = ButtonFlags(button);
        InputSender.Send(new[] { MouseInput(up, 0, 0, 0) }, action + " emergency release");
    }

    private static void AppendStroke(List<NativeAction.INPUT> inputs, KeyStroke stroke)
    {
        if (stroke.IsLiteral)
        {
            // 当前键盘布局打不出来的字符（中文就是这一类）：直接投递 UTF-16 码元。
            inputs.Add(KeyInput(0, stroke.Literal, NativeAction.KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, stroke.Literal, NativeAction.KEYEVENTF_UNICODE | NativeAction.KEYEVENTF_KEYUP));
            return;
        }

        // 修饰键按 Win→Ctrl→Alt→Shift 按下，反过来抬起；整串在同一批事件里，
        // 就算中途出事也不会把修饰键卡在按下状态。
        var modifiers = new List<ushort>(4);
        if (stroke.Win) modifiers.Add(KeyParser.VK_LWIN);
        if (stroke.Ctrl) modifiers.Add(KeyParser.VK_CONTROL);
        if (stroke.Alt) modifiers.Add(KeyParser.VK_MENU);
        if (stroke.Shift) modifiers.Add(KeyParser.VK_SHIFT);

        foreach (ushort modifier in modifiers) inputs.Add(VirtualKey(modifier, up: false));
        inputs.Add(VirtualKey(stroke.Vk, up: false));
        inputs.Add(VirtualKey(stroke.Vk, up: true));
        for (int i = modifiers.Count - 1; i >= 0; i--) inputs.Add(VirtualKey(modifiers[i], up: true));
    }

    /// <summary>绝对坐标 + 虚拟桌面标志：多显示器下才是对的。</summary>
    private const uint MoveFlags = NativeAction.MOUSEEVENTF_MOVE | NativeAction.MOUSEEVENTF_ABSOLUTE | NativeAction.MOUSEEVENTF_VIRTUALDESK;

    /// <summary>逐字输入时字与字之间的间隔。目标里有输入法时，太快会被吞。</summary>
    private const int CharacterGapMs = 15;

    /// <summary>行尾归一化：CRLF 与单个 CR 都算 LF。</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>比较文本前先归一化行尾 —— RichEdit 会把 CRLF 存成单个 CR，直接比会误判。</summary>
    private static bool SameText(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>屏幕物理像素 → SendInput 的 0..65535 归一化坐标（按整个虚拟桌面归一）。</summary>
    private static (int dx, int dy) Normalize(int x, int y)
    {
        RECT screen = Native.VirtualScreen();
        int width = Math.Max(1, screen.Width - 1);
        int height = Math.Max(1, screen.Height - 1);

        int dx = (int)Math.Round((double)(x - screen.Left) * 65535.0 / width);
        int dy = (int)Math.Round((double)(y - screen.Top) * 65535.0 / height);

        return (Math.Clamp(dx, 0, 65535), Math.Clamp(dy, 0, 65535));
    }

    private static (uint down, uint up) ButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Right => (NativeAction.MOUSEEVENTF_RIGHTDOWN, NativeAction.MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (NativeAction.MOUSEEVENTF_MIDDLEDOWN, NativeAction.MOUSEEVENTF_MIDDLEUP),
        _ => (NativeAction.MOUSEEVENTF_LEFTDOWN, NativeAction.MOUSEEVENTF_LEFTUP),
    };

    private static NativeAction.INPUT MouseInput(uint flags, int dx, int dy, uint data) => new()
    {
        type = NativeAction.INPUT_MOUSE,
        U = new NativeAction.InputUnion
        {
            mi = new NativeAction.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static NativeAction.INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = NativeAction.INPUT_KEYBOARD,
        U = new NativeAction.InputUnion
        {
            ki = new NativeAction.KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static NativeAction.INPUT VirtualKey(ushort vk, bool up)
    {
        uint flags = up ? NativeAction.KEYEVENTF_KEYUP : 0;
        if (KeyParser.IsExtended(vk)) flags |= NativeAction.KEYEVENTF_EXTENDEDKEY;
        return KeyInput(vk, (ushort)NativeAction.MapVirtualKey(vk, 0), flags);
    }

    private static bool InsideWindow(IntPtr hwnd, POINT point)
    {
        if (hwnd == IntPtr.Zero) return false;
        RECT bounds = Native.WindowBounds(hwnd);
        return point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }

    private static bool MatchesStem(IntPtr hwnd, string stem)
    {
        if (stem.Length == 0) return false;
        if (Native.WindowText(hwnd).Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            int pid = Native.ProcessId(hwnd);
            return pid > 0 && Process.GetProcessById(pid).ProcessName.Contains(stem, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 提权/已退出的进程读不到名字，按不匹配处理。
            return false;
        }
    }
}
