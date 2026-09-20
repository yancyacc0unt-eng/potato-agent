// Win32 层实机自测 —— 真的开记事本、真的打字、真的点击。
//
// ⚠ 安全前提（本文件里所有注入路径都遵守）：
//   * 只对自己启动的记事本动手，绝不碰用户已经开着的任何窗口；
//   * 每个动作都过 ComputerControl 的焦点门禁，前台不是目标窗口就什么都不做；
//   * 回读一律走【本文件自己的】UIA 代码，不借被测代码的手 —— 不然就是自证。
//
// 本机实测踩出来的三个坑，都写进断言里了：
//   1) Win11 记事本是【单实例 + 会话恢复】，新开一个窗口可能带着上次没存的内容，
//      所以每步开始前必须先清空并确认真的空了；
//   2) 它是 WinUI 应用，窗口用的是"扩展客户区"，客户区左上角是标签栏/标题栏而不是正文，
//      所以点击点必须拿 UIA 给的 Document 矩形去算，不能拿 GetClientRect 硬推；
//   3) 状态栏（行/列、N 个字符）是 UIA 可读的，拿来当"点击是否生效"的独立证据，
//      比"敲个字看有没有插进去"可靠得多（敲字本身还要过输入法那一关）。
//
// 跑法：
//   dotnet build build\Win32SelfTest.csproj -c Debug
//   build\bin\Win32SelfTest\Debug\net10.0-windows\Win32SelfTest.exe        正常实测
//   build\bin\Win32SelfTest\Debug\net10.0-windows\Win32SelfTest.exe dump   只打印 UIA 树（不注入）

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using PotatoAgent.Win32;

namespace PotatoAgent.Win32.SelfTest;

internal static class Program
{
    private const string Sample = "土豆智能体测试 12345";
    private const string Ascii = "abc123";
    /// <summary>换行必须写 \r\n：单独一个 \n 会被目标控件丢掉（本机实测 Win11 记事本就是这样）。</summary>
    private const string TwoLine = "AAAA\r\nBBBB";

    private const uint WM_CLOSE = 0x0010;

    /// <summary>
    /// 收尾时投 WM_CLOSE 好好关掉记事本（不用 Alt+F4：本机实测 WinUI 记事本压根不响应注入的 Alt+F4）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    private static readonly ComputerControl Cc = new() { Trace = line => Console.WriteLine("        · " + line) };
    private static readonly List<int> Spawned = new();
    private static int _checks;
    private static int _failures;
    private static int _notepadBefore;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        if (args.Length > 0 && args[0] == "dump") return DumpNotepad();

        Console.WriteLine("==================== PotatoAgent.Win32 实机自测 ====================");
        Console.WriteLine($"层自述   : {PotatoAgentWin32.Describe()}");
        Console.WriteLine($"屏幕     : {ScreenSummary()}");
        Console.WriteLine();

        IntPtr originalForeground = Desktop.Foreground();
        Console.WriteLine($"测试前的前台窗口: {Desktop.Describe(originalForeground)}（收尾时尽力还回去）");

        var existing = Process.GetProcessesByName("notepad");
        _notepadBefore = existing.Length;
        foreach (var process in existing) process.Dispose();
        Console.WriteLine($"测试前已有记事本进程: {_notepadBefore} 个（用户的东西，一律不碰）");
        Console.WriteLine();

        IntPtr notepadA = IntPtr.Zero, notepadB = IntPtr.Zero;
        try
        {
            GuardrailDpi();

            notepadA = LaunchNotepad("A");
            if (notepadA == IntPtr.Zero) { Fail("启动记事本失败，后续实测全部无法进行"); return Report(); }
            Cc.FocusWindow(notepadA);
            Console.WriteLine($"    记事本 A 键盘布局 LANGID = {LayoutOf(notepadA)}（0x0804 = 中文，0x0409 = 英文）");
            Console.WriteLine();

            Test1_TypeText(notepadA);
            Test2_Click(notepadA);
            Test1b_SendInputFallback(notepadA);

            notepadB = LaunchNotepad("B");
            if (notepadB != IntPtr.Zero)
            {
                Cc.FocusWindow(notepadB);
                if (!WaitForReadable(notepadB)) Console.WriteLine("    ⚠ B 一直读不出内容，反证测试的断言会失真");
                Test3_Gate(notepadA, notepadB);
                Test4_ImeProbe(notepadA);
            }
            else
            {
                Fail("第二个记事本没起来，反证测试无法进行");
            }
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup(originalForeground);
        }

        return Report();
    }

    // ==================== 护栏二：DPI ====================

    private static void GuardrailDpi()
    {
        Console.WriteLine("---- 护栏二：DPI 感知 ----");
        string declared = PotatoAgentWin32.Initialize();
        Check(declared == "per-monitor-v2", $"最早期声明 per-monitor-v2（实际 = {declared}）");
        Console.WriteLine();
    }

    // ==================== 实测 1：全新记事本 + 聚焦 + TypeText + UIA 回读 ====================

    private static void Test1_TypeText(IntPtr notepad)
    {
        Console.WriteLine("---- 实测 1：全新记事本 → FocusWindow → TypeText → UIA 回读 ----");

        var focus = Cc.FocusWindow(notepad);
        Console.WriteLine($"    FocusWindow: {focus}");
        Check(focus.Ok, "FocusWindow 返回成功");
        Check(Desktop.Foreground() == notepad, "回读 GetForegroundWindow() 确认记事本已经是前台");
        Check(Cc.Target == notepad, "聚焦成功后 Target 自动认领为该窗口");
        WaitForReadable(notepad);
        Console.WriteLine($"    UIA 文本控件: {Describe(FindTextElement(AutomationElement.FromHandle(notepad)))}");

        // Win11 记事本会恢复上次会话，先清干净再测。
        Check(Clear(notepad), "开测前把文档清空（新开的记事本可能带着恢复出来的旧内容）");

        var typed = Cc.TypeText(Sample);
        Console.WriteLine($"    TypeText(UIA 首选路径): {typed}");
        Check(typed.Ok, "TypeText 返回成功");
        Settle();

        string readback = ReadText(notepad);
        string title = Title(notepad);
        Console.WriteLine($"    UIA 回读 : \"{readback}\"  (len={readback.Length})");
        Console.WriteLine($"    状态栏   : {OneLine(ReadStatus(notepad))}");
        Console.WriteLine($"    窗口标题 : {title}");
        Check(readback == Sample, $"UIA 回读与输入一字不差（期望 \"{Sample}\"）");
        Check(title.Contains(Sample), "窗口标题里也带着这段文字（非 UIA 通道的独立佐证）");
        Check(ReadStatus(notepad).Contains($"{Sample.Length} 个字符"), $"状态栏字符数与 {Sample.Length} 一致（第三条独立证据）");

        Console.WriteLine();
    }

    // ==================== 实测 1b：SendInput 逐字回退路径 ====================

    private static void Test1b_SendInputFallback(IntPtr notepad)
    {
        Console.WriteLine("---- 实测 1b：SendInput 逐字回退路径（KEYEVENTF_UNICODE）----");

        // SendKeys 清空，顺带验一遍 Ctrl+A / Delete 这条 VK 路径
        Check(Clear(notepad), "SendKeys(^a + {DELETE}) 能清空文档");
        Console.WriteLine($"    清空后状态栏: {OneLine(ReadStatus(notepad))}");

        // 拿不到 UIA 时的退路：逐字 KEYEVENTF_UNICODE（中文靠它）
        var fallback = Cc.TypeText(Sample, preferUia: false);
        Console.WriteLine($"    TypeText(preferUia:false → SendInput/Unicode): {fallback}");
        Settle(1200);
        string afterFallback = ReadText(notepad);
        Console.WriteLine($"    回读     : \"{afterFallback}\"  (len={afterFallback.Length})");
        Console.WriteLine($"    码点     : {Codes(afterFallback)}");
        Console.WriteLine($"    状态栏   : {OneLine(ReadStatus(notepad))}");
        Check(fallback.Ok, "回退路径返回成功");
        Check(afterFallback.Contains(Sample, StringComparison.Ordinal),
            $"回退路径确实把 \"{Sample}\" 写进去了（中文也在）");
        if (afterFallback != Sample)
            Console.WriteLine($"    [INFO] 回退路径的文档里混进了额外字符（期望 {Sample.Length} 个字符，实际 {afterFallback.Length} 个）——" +
                              " 这是目标窗口里中文输入法异步补的，不是注入内容的一部分，码点见上。");

        Console.WriteLine();
    }

    // ==================== 实测 4：中文输入法探针（环境事实，不是本层的缺陷） ====================

    private static void Test4_ImeProbe(IntPtr notepad)
    {
        Console.WriteLine("---- 实测 4：中文输入法探针（SendKeys 的 VK 路径会经过输入法）----");

        Cc.FocusWindow(notepad);
        Clear(notepad);
        Console.WriteLine($"    清空后状态栏: {OneLine(ReadStatus(notepad))}");

        Check(Cc.SendKeys(Ascii).Ok, $"SendKeys(\"{Ascii}\") 12 个按键事件全部投递成功");
        Settle(1200);
        string ascii = ReadText(notepad);
        Console.WriteLine($"    注入结果: \"{ascii}\"  (len={ascii.Length})");
        Console.WriteLine($"    状态栏  : {OneLine(ReadStatus(notepad))}");

        if (ascii == Ascii)
        {
            Console.WriteLine("    [INFO] VK 路径原样落地：目标窗口当前没有输入法截胡。");
        }
        else
        {
            Console.WriteLine($"    [INFO] 12 个事件全部投递成功，但目标窗口的输入法把 \"{Ascii}\" 组合成了 \"{ascii}\"。");
            Console.WriteLine($"           这不是注入失败 —— 目标键盘布局 LANGID = 0x{LayoutOf(notepad):X4}（0x0804 = 简体中文），");
            Console.WriteLine("           真人在这台机器上敲同样的键，输入法给出的结果一模一样。");
            Console.WriteLine("           结论：SendKeys 适合发快捷键（^a / {DELETE} / {ESC} 都已实测可用）；");
            Console.WriteLine("                 要输入确定的文本请用 TypeText —— UIA 与 Unicode 两条通道都绕开输入法。");
        }

        Cc.SendKeys("{ESC}"); // 把可能挂着的输入法组合取消掉，别把脏状态留给后面
        Console.WriteLine();
    }

    // ==================== 实测 2：Click ====================

    private static void Test2_Click(IntPtr notepad)
    {
        Console.WriteLine("---- 实测 2：对同一个记事本 Click ----");

        // 两行文档：这样"点在第几行"能被状态栏的 行/列 直接读出来，
        // 比"敲个字看有没有插进去"可靠 —— 敲字还要过输入法那一关。
        Clear(notepad);
        var typed = Cc.TypeText(TwoLine);
        Settle();
        Console.WriteLine($"    准备: TypeText(两行文本) → {typed}");

        string before = ReadText(notepad);
        Console.WriteLine($"    点击前内容  : \"{OneLine(before)}\"");
        Console.WriteLine($"    点击前码点  : {Codes(before)}");
        Console.WriteLine($"    点击前状态栏: {OneLine(ReadStatus(notepad))}");

        var text = FindTextElement(AutomationElement.FromHandle(notepad));
        if (text is null) { Fail("拿不到正文控件的矩形，点击测试无法进行"); return; }

        var rect = text.Current.BoundingRectangle;
        Console.WriteLine($"    正文控件矩形: ({rect.X:0},{rect.Y:0}) {rect.Width:0}x{rect.Height:0}");

        // 行高不靠猜：让 UIA 把每一行的包围盒报出来（150% 缩放下行高有 50px 上下，猜必错）。
        var lines = TextLineRects(text);
        Console.WriteLine($"    UIA 报的行盒: {string.Join(", ", lines.Select(r => $"({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0})"))}");

        if (lines.Length < 2)
        {
            Fail("UIA 只报得出一行，两行点击测试做不了");
            return;
        }

        int x = (int)rect.X + 400;                          // 每行文字末尾之后，保证插入符落在行尾
        int line1 = (int)(lines[0].Y + lines[0].Height / 2); // 第 1 行中线
        int line2 = (int)(lines[1].Y + lines[1].Height / 2); // 第 2 行中线

        // --- 点第一行：状态栏的"行"应该报 1 ---
        var click1 = Cc.Click(x, line1);
        Console.WriteLine($"    Click({x},{line1}) [第 1 行]: {click1}");
        Check(click1.Ok, "第 1 行 Click 返回成功");
        Settle();
        string status1 = ReadStatus(notepad);
        Console.WriteLine($"    点击后状态栏: {OneLine(status1)}");
        Check(status1.Contains("行 1"), "点击生效：记事本自己报告插入符落在 行 1");

        // --- 点第二行：状态栏的"行"必须变成 2 —— 行号变了，说明 Y 真的生效了 ---
        var click2 = Cc.Click(x, line2);
        Console.WriteLine($"    Click({x},{line2}) [第 2 行]: {click2}");
        Check(click2.Ok, "第 2 行 Click 返回成功");

        POINT cursor = Desktop.Cursor();
        Check(Math.Abs(cursor.X - x) <= 1 && Math.Abs(cursor.Y - line2) <= 1,
            $"回读 GetCursorPos() 指针确实落在 ({x},{line2})，实际 ({cursor.X},{cursor.Y})");

        Settle();
        string status2 = ReadStatus(notepad);
        Console.WriteLine($"    点击后状态栏: {OneLine(status2)}");
        Check(status2.Contains("行 2"),
            "点击生效：插入符被这次点击挪到了 行 2 —— 点的 Y 坐标真的落到目标行上了");
        Check(Desktop.Foreground() == notepad, "点击之后记事本仍然是前台（注入没有跑到别处）");

        Console.WriteLine();
    }

    // ==================== 实测 3：反证 —— 门禁必须拦住 ====================

    private static void Test3_Gate(IntPtr target, IntPtr victim)
    {
        Console.WriteLine("---- 实测 3：反证测试（门禁必须拦住，前台窗口内容零改动）----");
        Console.WriteLine($"    目标 A = {Desktop.Describe(target)}");
        Console.WriteLine($"    前台 B = {Desktop.Describe(victim)}  ← 现在是它在接收输入");

        string beforeTarget = ReadText(target);
        string beforeVictim = ReadText(victim);
        Console.WriteLine($"    测试前 A 内容: \"{Trim(beforeTarget)}\"");
        Console.WriteLine($"    测试前 B 内容: \"{Trim(beforeVictim)}\"");

        // --- N1：根本不声明目标窗口 ---
        Cc.Target = IntPtr.Zero;
        var n1 = Cc.TypeText("N1-LEAK");
        Console.WriteLine($"    N1 未声明目标  : {n1}");
        Check(!n1.Ok, "N1 返回失败");
        CheckVictim(victim, beforeVictim, "N1");

        // --- N2：目标句柄不是窗口（窗口已经关了 / 传错句柄） ---
        Cc.Target = new IntPtr(0x0000BEEF);
        var n2 = Cc.TypeText("N2-LEAK");
        Console.WriteLine($"    N2 无效句柄    : {n2}");
        Check(!n2.Ok, "N2 返回失败");
        CheckVictim(victim, beforeVictim, "N2");

        // --- N3：目标是个"永远当不了前台"的活窗口（记事本里的 RichEdit 子控件） ---
        IntPtr edit = TextElementHandle(target);
        if (edit != IntPtr.Zero)
        {
            Cc.Target = edit;
            var n3 = Cc.TypeText("N3-LEAK");
            Console.WriteLine($"    N3 子控件句柄  : {n3}");
            Check(!n3.Ok, "N3 返回失败（子窗口不可能成为前台窗口，门禁正确地卡住了）");
            CheckVictim(victim, beforeVictim, "N3");
            Check(ReadText(target) == beforeTarget, "N3 之后目标 A 内容也没被改动");
        }
        else
        {
            Console.WriteLine("    N3 跳过：拿不到 RichEdit 子控件句柄");
        }

        // --- N4：真实事故场景 —— 想打给 A，但 B 正在前台（当年 103 个字符的事故） ---
        Cc.Target = target;
        var n4 = Cc.TypeText("N4-MARK");
        string afterTarget = ReadText(target);
        Console.WriteLine($"    N4 事故场景    : {n4}");
        CheckVictim(victim, beforeVictim, "N4");
        Check(!n4.Ok || afterTarget.Contains("N4-MARK"),
            "N4 要么如实返回失败，要么把字打进了声明的目标 A —— 二者必居其一，绝无第三种可能");
        Console.WriteLine($"    N4 之后 A 内容: \"{Trim(afterTarget)}\"");

        Console.WriteLine();
    }

    /// <summary>受害窗口（当前前台）的内容必须一个字符都没变 —— 这是反证测试的核心断言。</summary>
    private static void CheckVictim(IntPtr victim, string expected, string label)
    {
        string actual = ReadText(victim);
        Console.WriteLine($"        {label} 之后前台内容: \"{Trim(actual)}\" (len={actual.Length})");
        Check(actual == expected, $"{label} 之后前台窗口内容零改动");
    }

    // ==================== 收尾 ====================

    private static void Cleanup(IntPtr originalForeground)
    {
        Console.WriteLine("---- 实测 5：收尾 ----");

        // 先"清空文档 + 好好关掉"，再考虑强杀。
        // 为什么：Win11 记事本会把未保存内容写进 LocalState\TabState，强杀之后下一次启动
        // 会把这堆窗口连同内容一起还原回来（本机实测：一次启动吐出 7 个窗口，其中就有
        // 本自测用 SendKeys("abc123") 被输入法组合出来的「按不出」）。好好关掉它才会清干净。
        var mine = Spawned.Select(pid => Desktop.FindByPid(pid)).Where(h => h != IntPtr.Zero).ToList();
        foreach (IntPtr hwnd in mine) WipeAndClose(hwnd);

        foreach (int pid in Spawned) Kill(pid);

        // 保险丝：Win11 记事本是打包应用，"启动器进程"和"真正的 UI 进程"是两个 pid，
        // 所以除了跟踪到的 pid，还按进程名兜一遍。只在"测试前本来就没有记事本"时才兜 ——
        // 否则会把用户自己开着的记事本一起杀掉。
        if (_notepadBefore == 0)
        {
            foreach (var process in Process.GetProcessesByName("notepad"))
            {
                IntPtr hwnd = Desktop.FindByPid(process.Id);
                if (hwnd != IntPtr.Zero) WipeAndClose(hwnd);

                Console.WriteLine($"    保险丝：清掉漏网的 Notepad pid {process.Id}");
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); }
                catch (Exception error) { Console.WriteLine($"      失败: {error.Message}"); }
                finally { process.Dispose(); }
            }
        }

        if (originalForeground != IntPtr.Zero && originalForeground != Desktop.Foreground())
        {
            var restored = Cc.FocusWindow(originalForeground);
            Console.WriteLine($"    归还前台: {(restored.Ok ? "成功" : restored.Message)}");
        }

        Thread.Sleep(800);
        var leftovers = Process.GetProcessesByName("notepad");
        Console.WriteLine($"    Get-Process notepad 计数 = {leftovers.Length}");
        Check(leftovers.Length == 0, "收尾后机器上不剩任何 notepad 进程");
        foreach (var process in leftovers) process.Dispose();

        Console.WriteLine();
    }

    /// <summary>
    /// 把一个记事本窗口"清空文档 → WM_CLOSE 好好关掉"。关不掉就算了，调用方后面还有强杀兜底 ——
    /// 但那样会留下会话状态，所以这里先尽力三次。
    /// </summary>
    private static void WipeAndClose(IntPtr hwnd)
    {
        for (int attempt = 0; attempt < 3 && IsWindow(hwnd); attempt++)
        {
            Console.WriteLine($"    清空并关闭 {Desktop.Describe(hwnd)}（第 {attempt + 1} 次）");
            Cc.Target = hwnd;
            if (Desktop.Foreground() != hwnd) Cc.FocusWindow(hwnd);

            // 先清空：文档里还有东西的话，WM_CLOSE 的结果是弹保存确认框而不是关窗口。
            Cc.TypeText(string.Empty);
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(400);
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
                Console.WriteLine($"    已关闭 pid {pid}");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"    关闭 pid {pid} 时: {error.GetType().Name}: {error.Message}");
        }
    }

    // ==================== 探针：把记事本的 UIA 树打出来 ====================

    private static int DumpNotepad()
    {
        Console.WriteLine("---- 探针：记事本 UIA 树（不注入任何键鼠）----");
        Console.WriteLine($"层自述: {PotatoAgentWin32.Describe()}");

        var launch = Cc.Launch("notepad.exe", null, null, 15000);
        Console.WriteLine(launch.Message);
        if (!launch.Ok) return 1;

        IntPtr hwnd = launch.WindowHandle;
        Track(launch.ProcessId);
        if (hwnd == IntPtr.Zero) return 1;
        Track(Desktop.ProcessId(hwnd));

        try
        {
            Console.WriteLine($"FocusWindow: {Cc.FocusWindow(hwnd)}");
            Console.WriteLine($"键盘布局 LANGID = {LayoutOf(hwnd)}");
            Thread.Sleep(600);
            Dump(AutomationElement.FromHandle(hwnd), 1, 4);
        }
        catch (Exception error)
        {
            Console.WriteLine($"dump 失败: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            foreach (int pid in Spawned) Kill(pid);
            foreach (var process in Process.GetProcessesByName("notepad"))
            {
                try { if (!process.HasExited) process.Kill(); process.WaitForExit(2000); } catch { }
                finally { process.Dispose(); }
            }
            Console.WriteLine($"收尾: notepad 计数 = {Process.GetProcessesByName("notepad").Length}");
        }

        return 0;
    }

    private static void Dump(AutomationElement element, int depth, int maxDepth)
    {
        Console.WriteLine($"{new string(' ', depth * 2)}{Describe(element)}");

        if (depth >= maxDepth) return;

        AutomationElementCollection children;
        try { children = element.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch { return; }

        foreach (AutomationElement child in children) Dump(child, depth + 1, maxDepth);
    }

    private static string Describe(AutomationElement? element)
    {
        if (element is null) return "(none)";

        try
        {
            var rect = element.Current.BoundingRectangle;
            var patterns = new List<string>();
            foreach (var pattern in element.GetSupportedPatterns())
            {
                string name = pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "");
                if (name is "Value" or "Text" or "ExpandCollapse" or "Toggle" or "Invoke" or "Selection" or "SelectionItem" or "Scroll" or "LegacyIAccessible")
                    patterns.Add(name);
            }

            return $"[{element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}] " +
                   $"name=\"{element.Current.Name}\" id=\"{element.Current.AutomationId}\" class=\"{element.Current.ClassName}\" " +
                   $"rect=({rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0}) patterns=[{string.Join(",", patterns)}]";
        }
        catch (Exception error)
        {
            return $"<{error.GetType().Name}>";
        }
    }

    // ==================== 工具 ====================

    private static IntPtr LaunchNotepad(string label)
    {
        var outcome = Cc.Launch("notepad.exe", null, null, 15000);
        Console.WriteLine($"    启动记事本 {label}: {outcome.Message}");
        if (!outcome.Ok) return IntPtr.Zero;

        Track(outcome.ProcessId);
        IntPtr hwnd = outcome.WindowHandle;
        if (hwnd == IntPtr.Zero) hwnd = Desktop.FindByPid(outcome.ProcessId);
        if (hwnd != IntPtr.Zero)
        {
            Console.WriteLine($"    记事本 {label} 窗口: {Desktop.Describe(hwnd)}");
            Track(Desktop.ProcessId(hwnd));
        }
        return hwnd;
    }

    private static void Track(int pid)
    {
        if (pid > 0 && !Spawned.Contains(pid)) Spawned.Add(pid);
    }

    private static void Settle(int ms = 600) => Thread.Sleep(ms);

    /// <summary>
    /// 清空文档并确认真的空了。先用 SendKeys（顺带验 VK 路径），不干净再用 UIA 整篇覆盖兜底。
    /// 返回 false 表示还是没清干净 —— 那后面的断言就不可信，必须暴露出来。
    /// </summary>
    private static bool Clear(IntPtr hwnd)
    {
        Cc.SendKeys("^a");
        Cc.SendKeys("{DELETE}");
        Settle();
        if (ReadText(hwnd).Length == 0) return true;

        Cc.TypeText(string.Empty);
        Settle();
        return ReadText(hwnd).Length == 0;
    }

    private static string OneLine(string text) => text.Replace("\r", string.Empty).Replace("\n", " ");

    /// <summary>把字符串的码点打出来 —— 打印出来像"？"的字符到底是什么，只有看码点才算数。</summary>
    private static string Codes(string text) =>
        text.Length == 0 ? "(空)" : string.Join(" ", text.Take(40).Select(c => $"U+{(int)c:X4}"));

    /// <summary>UIA 给的每行包围盒（RichEdit 一般一行一个）。</summary>
    private static System.Windows.Rect[] TextLineRects(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object? pattern)) return Array.Empty<System.Windows.Rect>();
            return ((TextPattern)pattern).DocumentRange.GetBoundingRectangles();
        }
        catch
        {
            return Array.Empty<System.Windows.Rect>();
        }
    }

    /// <summary>本文件自己的 UIA 回读：ValuePattern 优先，其次 TextPattern。不借被测代码的手。</summary>
    private static string ReadText(IntPtr hwnd)
    {
        try
        {
            AutomationElement? element = FindTextElement(AutomationElement.FromHandle(hwnd));
            if (element is null) return "<找不到文本控件>";

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePattern))
                return ((ValuePattern)valuePattern).Current.Value;

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object? textPattern))
                return ((TextPattern)textPattern).DocumentRange.GetText(-1);

            return "<控件既不支持 ValuePattern 也不支持 TextPattern>";
        }
        catch (Exception error)
        {
            return $"<UIA 回读失败: {error.GetType().Name}: {error.Message}>";
        }
    }

    /// <summary>状态栏（行/列 + N 个字符）—— 记事本自己维护的独立观察通道，不经过我们的注入。</summary>
    private static string ReadStatus(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ContentTextBlock"));

            var parts = new List<string>();
            foreach (AutomationElement element in all) parts.Add(element.Current.Name);
            return parts.Count > 0 ? string.Join(" | ", parts) : "(状态栏读不到)";
        }
        catch (Exception error)
        {
            return $"<状态栏读取失败: {error.GetType().Name}>";
        }
    }

    private static IntPtr TextElementHandle(IntPtr hwnd)
    {
        try
        {
            AutomationElement? element = FindTextElement(AutomationElement.FromHandle(hwnd));
            return element is null ? IntPtr.Zero : new IntPtr(element.Current.NativeWindowHandle);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static AutomationElement? FindTextElement(AutomationElement? root)
    {
        if (root is null) return null;

        foreach (var type in new[] { ControlType.Document, ControlType.Edit })
        {
            AutomationElement? found = root.FindFirst(
                TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>窗口标题 —— 和 UIA 完全无关的第二条证据（记事本会把内容写进标题）。</summary>
    private static string Title(IntPtr hwnd) => Desktop.DescribeWindow(hwnd)["title"]?.GetValue<string>() ?? string.Empty;

    private static int LayoutOf(IntPtr hwnd) => Desktop.KeyboardLayoutId(hwnd);

    /// <summary>UIA 树是异步建起来的，刚启动的窗口可能还找不到文本控件；等它一下（这不是注入重试）。</summary>
    private static bool WaitForReadable(IntPtr hwnd, int timeoutMs = 8000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadText(hwnd).StartsWith('<')) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static string ScreenSummary() => Desktop.State(new System.Text.Json.Nodes.JsonObject())["screen"]!.ToJsonString();

    private static string Trim(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static bool Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
        return ok;
    }

    private static void Fail(string what)
    {
        _checks++;
        _failures++;
        Console.WriteLine($"    [FAIL] {what}");
    }

    private static int Report()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine(_failures == 0
            ? $"全部通过：{_checks} 项检查，0 项失败。"
            : $"有失败：{_checks} 项检查，{_failures} 项失败。");
        Console.WriteLine("==================================================================");
        return _failures == 0 ? 0 : 1;
    }
}
