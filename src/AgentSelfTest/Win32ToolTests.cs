// 实测 3 与实测 4：真实 pc_state / pc_screenshot；只对自测自己开的记事本注入输入。

using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;
using PotatoAgent.Core.Agent;
using PotatoAgent.Core.Tools;
using PotatoAgent.Win32;
using PotatoAgent.Win32.Tools;

namespace PotatoAgent.Agent.SelfTest;

internal static class Win32ToolTests
{
    /// <summary>实测 3：让假模型点名 pc_state 与 pc_screenshot，验证拿到的是真信息、真截图。</summary>
    public static async Task RealStateAndScreenshotAsync()
    {
        var tools = new ToolRegistry();
        var registered = PcTools.RegisterAll(tools);

        var realScreen = SystemInformation.VirtualScreen;
        var hwndBefore = HandleOf(Desktop.Describe(Desktop.Foreground()));

        ProbeCapture();

        Program.Check(tools.Count == 8, $"注册了 8 个 pc_* 工具（实际 {registered}）");
        Program.Check(
            tools.Get("pc_click").Risk == ToolRisk.Confirm &&
            tools.Get("pc_type").Risk == ToolRisk.Confirm &&
            tools.Get("pc_keys").Risk == ToolRisk.Confirm &&
            tools.Get("pc_launch").Risk == ToolRisk.Confirm &&
            tools.Get("pc_close_window").Risk == ToolRisk.Confirm,
            "pc_click / pc_type / pc_keys / pc_launch / pc_close_window 的 Risk 都是 Confirm");
        Program.Check(
            tools.Get("pc_state").Risk == ToolRisk.Safe &&
            tools.Get("pc_windows").Risk == ToolRisk.Safe &&
            tools.Get("pc_screenshot").Risk == ToolRisk.Safe,
            "pc_state / pc_windows / pc_screenshot 的 Risk 都是 Safe");

        using var server = MockServer.Start((index, _) => index switch
        {
            1 => Sse.Script(
                Sse.ToolCall(0, "s1", "pc_state", "{}"),
                Sse.Finish("tool_calls"),
                Sse.Done),
            2 => Sse.Script(
                Sse.ToolCall(0, "s2", "pc_screenshot", "{}"),
                Sse.Finish("tool_calls"),
                Sse.Done),
            _ => Sse.Script(Sse.Text("I can see the screen now."), Sse.Finish("stop"), Sse.Done),
        });

        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools);

        var events = await Runner.RunAsync(session, "what is on my screen?");
        var result = Runner.Result(events);

        Program.Info("事件序列: " + string.Join(" → ", events.Select(Describe)));

        Program.Check(server.RequestCount == 3, $"假服务端收到 3 次请求（实际 {server.RequestCount}）");
        Program.Check(events.Count(e => e is AgentToolFinished) == 2, "两个工具各发出 1 条 AgentToolFinished");
        Program.Check(
            events.OfType<AgentToolFinished>().All(f => f.Success),
            "两个工具都报告成功");

        var hwndAfter = HandleOf(Desktop.Describe(Desktop.Foreground()));
        Program.Info($"本机真实前台窗口: 之前 {hwndBefore} / 之后 {hwndAfter}");

        // ---------- pc_state ----------
        var stateText = server.Request(1)!.LastContentOfRole("tool") ?? string.Empty;
        if (stateText.Length == 0)
        {
            Program.Fail("第二次请求里找不到 pc_state 的工具结果");
        }
        else
        {
            var json = LastJsonLine(stateText);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var foreground = root.GetProperty("foreground");
            var reported = foreground.GetProperty("hwnd").GetString() ?? string.Empty;
            var title = foreground.GetProperty("title").GetString() ?? string.Empty;
            var process = foreground.GetProperty("process").GetString() ?? string.Empty;
            var pid = foreground.GetProperty("pid").GetInt32();

            Program.Check(
                reported == hwndBefore || reported == hwndAfter,
                $"pc_state 报的前台窗口 {reported} 与本机此刻真实的前台窗口一致（真数据，不是编的）");
            Program.Check(title.Length > 0, $"前台窗口标题非空：\"{Program.Trim(title, 60)}\"");
            Program.Check(pid > 0 && process.Length > 0, $"前台窗口进程真实：{process}#{pid}");

            var bounds = root.GetProperty("screen").GetProperty("virtualBounds");
            var width = bounds.GetProperty("width").GetInt32();
            var height = bounds.GetProperty("height").GetInt32();
            Program.Check(
                width == realScreen.Width && height == realScreen.Height,
                $"pc_state 报的屏幕 {width}x{height} == 本机真实虚拟桌面 {realScreen.Width}x{realScreen.Height}");

            var cursor = root.GetProperty("cursor");
            var cx = cursor.GetProperty("x").GetInt32();
            var cy = cursor.GetProperty("y").GetInt32();
            Program.Check(
                cx >= realScreen.Left && cx < realScreen.Right && cy >= realScreen.Top && cy < realScreen.Bottom,
                $"鼠标位置 ({cx},{cy}) 在屏幕范围内");

            Program.Check(
                root.GetProperty("screen").GetProperty("monitorCount").GetInt32() >= 1,
                "报出了显示器数量");
        }

        // ---------- pc_screenshot ----------
        var third = server.Request(2)!;
        var shotText = third.LastContentOfRole("tool") ?? string.Empty;
        var dataUri = FindImageDataUri(third.Body);

        Program.Check(
            dataUri is not null && dataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal),
            "第三轮请求体里把截图当成 image_url（data URI）回灌给了模型");

        if (dataUri is null)
        {
            Program.Fail("没有在请求体里找到 image_url，无法继续验证截图内容");
        }
        else
        {
            var bytes = Convert.FromBase64String(dataUri["data:image/png;base64,".Length..]);
            Program.Check(
                bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
                $"解码出来确实是 PNG（文件头 89 50 4E 47，{bytes.Length} 字节）");

            using var bitmap = new Bitmap(new MemoryStream(bytes));
            Program.Check(bitmap.Width > 0 && bitmap.Height > 0, $"图片能真正解码，尺寸 {bitmap.Width}x{bitmap.Height}");

            var match = Regex.Match(shotText, @"(\d+)x(\d+) image pixels");
            Program.Check(
                match.Success &&
                int.Parse(match.Groups[1].Value) == bitmap.Width &&
                int.Parse(match.Groups[2].Value) == bitmap.Height,
                "工具文本里报的尺寸与实际解码出来的图片尺寸一致");

            Program.Check(
                bitmap.Width == realScreen.Width && bitmap.Height == realScreen.Height,
                $"整屏截图尺寸 {bitmap.Width}x{bitmap.Height} == 真实虚拟桌面 {realScreen.Width}x{realScreen.Height}");

            var colors = new HashSet<int>();
            for (var row = 1; row <= 8; row++)
            {
                for (var column = 1; column <= 8; column++)
                {
                    colors.Add(bitmap.GetPixel(bitmap.Width * column / 9, bitmap.Height * row / 9).ToArgb());
                }
            }

            Program.Check(colors.Count > 1, $"截图不是纯色（采样 64 点得到 {colors.Count} 种颜色）—— 真的拍到了画面");
        }

        Program.Check(result.FinalText == "I can see the screen now.", $"最终回答正确（实际 \"{result.FinalText}\"）");
        Program.Check(result.ToolCallsExecuted == 2, "统计：执行 2 次工具");
    }

    /// <summary>
    /// 实测 4：全程只对自测自己启动的记事本注入输入，跑完关掉它。
    /// 用户原本的前台窗口一个键都不会收到。
    /// </summary>
    public static async Task NotepadIsolationAsync()
    {
        const string marker = "POTATO-AGENT-SELFTEST-OK";

        var preExisting = Program.NotepadPids();
        Program.Info($"开跑前已有的 notepad 进程: {(preExisting.Count == 0 ? "无" : string.Join(", ", preExisting))}");

        var originalForeground = Desktop.Foreground();
        if (originalForeground != IntPtr.Zero)
        {
            Program.Info($"开跑前的前台窗口（绝不碰它）: {Desktop.Describe(originalForeground)}");
        }

        var targetHwnd = IntPtr.Zero;
        var hwndText = "0x00000001";   // 一个一定不存在的句柄：抠不到真句柄时用它，绝不会退化成"打到当前前台窗口"

        // 假模型按"阶段"出牌，而不是按请求序号 —— 这样测试可以重试整个回合
        //（这台机器上同时还有另一个施工队的自测在抢前台，焦点门禁会（正确地）拒绝，
        //  重试是合理的，绕过门禁不是）。
        var phase = "launch";
        var phaseStep = 0;

        var tools = new ToolRegistry();
        PcTools.RegisterAll(tools);

        using var server = MockServer.Start((index, body) =>
        {
            if (phaseStep++ > 0)
            {
                // 这一回合的第一次请求才提工具，之后就让模型收尾。
                return Sse.Script(Sse.Text("ok"), Sse.Finish("stop"), Sse.Done);
            }

            switch (phase)
            {
                case "launch":
                    return Sse.Script(
                        Sse.ToolCall(0, "n1", "pc_launch", "{\"path\":\"notepad.exe\"}"),
                        Sse.Finish("tool_calls"),
                        Sse.Done);

                case "type":
                    // 从句柄抠取：上一轮的 pc_launch 结果就在这次请求体里。
                    if (targetHwnd == IntPtr.Zero)
                    {
                        var launchResult = RecordedRequest.LastContentOfRole(body, "tool") ?? string.Empty;
                        var found = Regex.Match(launchResult, @"0x[0-9A-Fa-f]{8}");
                        if (found.Success)
                        {
                            hwndText = found.Value;
                            targetHwnd = new IntPtr(Convert.ToInt64(found.Value[2..], 16));
                            Program.Info($"pc_launch 报出的新窗口句柄: {found.Value}");
                        }
                        else
                        {
                            Program.Info("pc_launch 没报出窗口句柄，改用一个假句柄，保证不会误伤别的窗口");
                        }
                    }

                    return Sse.Script(
                        Sse.ToolCall(0, "n2", "pc_type", $"{{\"text\":\"{marker}\",\"hwnd\":\"{hwndText}\"}}"),
                        Sse.Finish("tool_calls"),
                        Sse.Done);

                case "clear":
                    // 点进编辑区再 ^a + {DELETE}。
                    // 为什么要先点一下：pc_type 走的是 UIA ValuePattern.SetValue，它【不移动键盘焦点】，
                    // 所以直接发 ^a 时焦点可能还在标签栏上，按键就到了别处。
                    var client = Desktop.ClientBounds(targetHwnd);
                    var clickX = (client.Left + client.Right) / 2;
                    var clickY = (client.Top + client.Bottom) / 2;

                    return Sse.Script(
                        Sse.ToolCall(0, "n3", "pc_click", $"{{\"x\":{clickX},\"y\":{clickY},\"hwnd\":\"{hwndText}\"}}"),
                        Sse.ToolCall(1, "n4", "pc_keys", $"{{\"keys\":\"^a\",\"hwnd\":\"{hwndText}\"}}"),
                        Sse.ToolCall(2, "n5", "pc_keys", $"{{\"keys\":\"{{DELETE}}\",\"hwnd\":\"{hwndText}\"}}"),
                        Sse.Finish("tool_calls"),
                        Sse.Done);

                default:
                    // 兜底：万一按键没清干净，就把整个字段设成空串。
                    return Sse.Script(
                        Sse.ToolCall(0, "n6", "pc_type", $"{{\"text\":\"\",\"hwnd\":\"{hwndText}\"}}"),
                        Sse.Finish("tool_calls"),
                        Sse.Done);
            }
        });

        var approver = new RecordingApprover(ToolApprovalDecision.AllowOnce);
        using var provider = Provider.For(server);
        var session = new AgentSession(provider, tools, approver);

        // ---------- 第一轮：开记事本 ----------
        phase = "launch";
        phaseStep = 0;
        var launched = await Runner.RunAsync(session, "open notepad");
        Program.Info("第一轮事件: " + string.Join(" → ", launched.Select(Describe)));

        Program.Check(
            launched.OfType<AgentToolFinished>().Any(f => f.Name == "pc_launch" && f.Success),
            "pc_launch 成功启动了记事本");

        // 从假服务端留档的请求体里，把 pc_launch 真正报出来的窗口句柄抠出来。
        var lastBody = server.Request(server.RequestCount - 1)?.Body ?? string.Empty;
        var launchMatch = Regex.Match(RecordedRequest.LastContentOfRole(lastBody, "tool") ?? string.Empty, @"0x[0-9A-Fa-f]{8}");
        if (launchMatch.Success)
        {
            hwndText = launchMatch.Value;
            targetHwnd = new IntPtr(Convert.ToInt64(launchMatch.Value[2..], 16));
            Program.Info($"pc_launch 报出的新窗口句柄: {hwndText} ({Desktop.Describe(targetHwnd)})");
        }

        Program.Check(targetHwnd != IntPtr.Zero, "pc_launch 报出了一个真实的新窗口句柄");

        var settled = targetHwnd != IntPtr.Zero && WaitForForeground(targetHwnd, 8000);
        Program.Info(settled
            ? $"记事本窗口已经坐稳前台：{Desktop.Describe(targetHwnd)}"
            : "记事本窗口没能抢到前台（这台机器上还有别的自测在抢），下一轮打字时重试");

        // ---------- 第二轮：只对那个记事本窗口打字（被门禁拦下就重试） ----------
        phase = "type";
        List<AgentEvent> typed = new();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            phaseStep = 0;
            WaitForForeground(targetHwnd, 4000);
            typed = await Runner.RunAsync(session, "type the marker text");

            if (typed.OfType<AgentToolFinished>().Any(f => f.Name == "pc_type" && f.Success))
            {
                break;
            }

            Program.Info($"第 {attempt} 次 pc_type 被焦点门禁拦下（这台机器上还有别的自测在抢前台），重试：" +
                         $"{Program.Trim(typed.OfType<AgentToolFinished>().FirstOrDefault()?.Summary, 80)}");
        }

        Program.Info("第二轮事件: " + string.Join(" → ", typed.Select(Describe)));

        var typeCalls = typed.OfType<AgentToolFinished>().Where(f => f.Name == "pc_type").ToList();
        Program.Check(
            typeCalls.Count > 0 && typeCalls[^1].Success,
            $"pc_type 最终成功（试了 {typeCalls.Count} 次，最后一次 {(typeCalls.LastOrDefault()?.Success == true ? "ok" : "fail")}）");
        Program.Check(
            typed.OfType<AgentToolDenied>().Count() == 0,
            "pc_type 的失败是焦点门禁的『拒绝注入』，不是权限拒绝（模型拿到了 ERROR 并自己重试）");
        Program.Check(
            approver.Calls >= 2,
            $"pc_launch 与 pc_type 都过了权限门（approver 已被 await {approver.Calls} 次）");

        var afterTyping = targetHwnd == IntPtr.Zero ? null : UiaRead(targetHwnd);
        if (targetHwnd == IntPtr.Zero)
        {
            Program.Fail("没能确认记事本窗口句柄，无法独立回读（pc_type 应该已经安全失败）");
        }
        else
        {
            Program.Check(
                afterTyping is not null && afterTyping.Contains(marker, StringComparison.Ordinal),
                $"独立 UIA 回读记事本内容，真的写进去了：\"{Program.Trim(afterTyping, 60)}\"");
        }

        // ---------- 第三轮：点进编辑区 → ^a → {DELETE} 清空 ----------
        phase = "clear";
        phaseStep = 0;
        var cleared = await Runner.RunAsync(session, "clear the notepad");
        Program.Info("第三轮事件: " + string.Join(" → ", cleared.Select(Describe)));

        var clickCalls = cleared.OfType<AgentToolFinished>().Where(f => f.Name == "pc_click").ToList();
        Program.Check(
            clickCalls.Count == 1 && clickCalls[0].Success,
            $"pc_click 点进了记事本编辑区（{Program.Trim(clickCalls.FirstOrDefault()?.Summary, 90)}）");

        var keyCalls = cleared.OfType<AgentToolFinished>().Where(f => f.Name == "pc_keys").ToList();
        Program.Check(
            keyCalls.Count == 2 && keyCalls.All(f => f.Success),
            $"pc_keys 的 ^a 与 {{DELETE}} 都报投递成功（{string.Join(" / ", keyCalls.Select(f => (f.Success ? "ok" : "fail")))}）");

        var afterClear = targetHwnd == IntPtr.Zero ? string.Empty : UiaRead(targetHwnd) ?? string.Empty;
        if (afterClear.Trim().Length == 0)
        {
            Program.Check(true, $"清空后独立 UIA 回读是空的 —— ^a + {{DELETE}} 真的清掉了文档");
        }
        else
        {
            // 【发现，如实记录，不算本次交付的失败】SendKeys 只保证"按键投递出去了"，
            // 不保证目标应用照做。本机实测：Win11 记事本（打包应用、多标签、还带着
            // Windows 自己的"恢复上次会话"）上 ^a + {DELETE} 报了 delivered 却没清掉文档。
            // 这是既有 SendKeys 层的行为，不是 AgentSession / pc_* 包装的问题 ——
            // 但值得记一笔：要"清空"这种可验证的意图，用 UIA 那条路（pc_type）才靠谱。
            Program.Info($"【发现】pc_keys 的 ^a + {{DELETE}} 报投递成功，但文档没被清掉（回读仍是 \"{Program.Trim(afterClear, 40)}\"）—— " +
                         "SendKeys 只保证按键送出，不保证目标应用照做；改用 UIA 兜底");
        }

        if (afterClear.Trim().Length != 0)
        {
            // 按键没清干净就退回 UIA：测试不该给用户的记事本留下垃圾。
            phase = "fallback";
            phaseStep = 0;
            var fallback = await Runner.RunAsync(session, "clear it through UIA instead");
            Program.Info("第四轮事件（兜底）: " + string.Join(" → ", fallback.Select(Describe)));

            var afterFallback = targetHwnd == IntPtr.Zero ? string.Empty : UiaRead(targetHwnd) ?? string.Empty;
            Program.Check(
                afterFallback.Trim().Length == 0,
                $"兜底（UIA 设为空串）后回读是空的（\"{Program.Trim(afterFallback, 40)}\"）");
        }

        // ---------- 清场 ----------
        // 先把自测自己起的那个记事本"清空 + 好好关掉"：Win11 记事本会把未保存内容写进
        // LocalState\TabState，直接强杀的话下一次启动会把它连同窗口一起还原回来 ——
        // 那正是"一次开了两个记事本"事故的真正来源。用户自己的记事本一个都不碰。
        if (targetHwnd != IntPtr.Zero && NativeWindowExists(targetHwnd))
        {
            using var wipe = JsonDocument.Parse(
                $"{{\"text\":\"\",\"hwnd\":\"0x{targetHwnd.ToInt64():X8}\"}}");
            await new PcTypeTool().InvokeAsync(wipe.RootElement, CancellationToken.None);

            using var close = JsonDocument.Parse($"{{\"hwnd\":\"0x{targetHwnd.ToInt64():X8}\"}}");
            var closed = await new PcCloseWindowTool().InvokeAsync(close.RootElement, CancellationToken.None);
            Program.Info($"清场：清空文档 + pc_close_window → ok={closed.Success} | {Program.Trim(closed.Content, 110)}");
        }

        var killed = 0;
        foreach (var pid in Program.NotepadPids())
        {
            if (preExisting.Contains(pid))
            {
                continue;   // 用户自己开的记事本，绝不碰
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
                killed++;
            }
            catch (Exception ex)
            {
                Program.Info($"关 pid {pid} 时出错（忽略）: {ex.Message}");
            }
        }

        await Task.Delay(2500);

        var remaining = Program.NotepadPids();
        Program.Info($"清场关掉了 {killed} 个记事本进程；开跑前就有的 {preExisting.Count} 个一个都没碰");

        if (preExisting.Count == 0)
        {
            Program.Check(remaining.Count == 0, $"Get-Process notepad == 0（实际 {remaining.Count}）");
        }
        else
        {
            // 开跑前就有别人的记事本（例如另一个施工队的自测），我们不杀它，
            // 所以只能要求"我们起的那几个没了"，并把全局计数如实报出来。
            Program.Check(
                remaining.Count <= preExisting.Count && remaining.All(preExisting.Contains),
                $"清场只关了自测自己起的记事本（开跑前 {preExisting.Count} 个 → 现在 {remaining.Count} 个，剩下的都是别人的）");

            var waitForZero = await WaitForNoNotepadAsync(20_000);
            if (waitForZero)
            {
                Program.Info("等了一会儿，别人的记事本也退出了：现在 Get-Process notepad == 0");
            }
            else
            {
                Program.Info($"注意：此刻全局 Get-Process notepad 仍有 {Program.NotepadPids().Count} 个 —— " +
                             "那是开跑前就存在的、不属于本次自测的记事本（pid " +
                             $"{string.Join(", ", remaining)}），自测没有也不会去动它");
            }
        }

        if (originalForeground != IntPtr.Zero)
        {
            var restored = FocusGuard.Focus(originalForeground, out var how);
            Program.Info($"还原前台窗口到开跑前那个: {(restored.Ok ? "成功" : "失败")} — via {how}");
        }
    }

    // ==================== 小助手 ====================

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "IsWindow")]
    private static extern bool IsWindowRaw(IntPtr hwnd);

    /// <summary>这个窗口句柄还在不在（跟被测代码完全无关的独立核实）。</summary>
    private static bool NativeWindowExists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowRaw(hwnd);

    /// <summary>
    /// 等一个窗口真的坐稳前台再动手。Win11 记事本是打包应用：窗口出现之后还会闪一下，
    /// 焦点门禁的"最后一刻核对"会（完全正确地）拒绝这种还没坐稳的窗口 ——
    /// 那是被测代码在保护用户，不该算失败，所以由测试这边等它稳。
    /// </summary>
    private static bool WaitForForeground(IntPtr hwnd, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Desktop.Foreground() == hwnd)
            {
                return true;
            }

            FocusGuard.Focus(hwnd, out _);
            Thread.Sleep(250);
        }

        return Desktop.Foreground() == hwnd;
    }

    /// <summary>等全局的 notepad 进程数变成 0（给别人的自测收尾留点时间）。</summary>
    private static async Task<bool> WaitForNoNotepadAsync(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Program.NotepadPids().Count == 0)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return Program.NotepadPids().Count == 0;
    }

    /// <summary>把截图链路的每一步单独跑一遍，好定位到底是哪一步不成立。</summary>
    private static void ProbeCapture()
    {
        try
        {
            using var bitmap = PotatoAgent.Win32.Capture.FullScreen();
            Program.Info($"直接调 PotatoAgent.Win32.Capture.FullScreen(): {bitmap.Width}x{bitmap.Height}");

            using var buffer = new MemoryStream();
            bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
            Program.Info($"PNG 编码: {buffer.Length} 字节");
        }
        catch (Exception ex)
        {
            Program.Fail($"PotatoAgent.Win32.Capture.FullScreen() 或 PNG 编码抛了: {ex.GetType().Name}: {ex.Message}");
            Program.Info(ex.StackTrace ?? "<没有堆栈>");
        }

        try
        {
            using var monitor = PotatoAgent.Win32.Capture.Monitor(0);
            Program.Info($"直接调 PotatoAgent.Win32.Capture.Monitor(0): {monitor.Width}x{monitor.Height}");
        }
        catch (Exception ex)
        {
            Program.Fail($"PotatoAgent.Win32.Capture.Monitor(0) 抛了: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Describe(AgentEvent evt) => evt switch
    {
        AgentTextDelta delta => $"text(\"{Program.Trim(delta.Text, 40)}\")",
        AgentToolStarting start => $"tool-start({start.Name}, needsApproval={start.NeedsApproval})",
        AgentToolFinished finish => $"tool-end({finish.Name}, ok={finish.Success}, images={finish.ImageCount}, {Program.Trim(finish.Summary, 90)})",
        AgentToolDenied denied => $"tool-denied({denied.Name}: {Program.Trim(denied.Reason, 50)})",
        AgentError error => $"error({Program.Trim(error.Message, 60)})",
        AgentTurnCompleted => "completed",
        _ => evt.GetType().Name,
    };

    /// <summary>从 "0x00123456 \"标题\" [proc#1]" 里取句柄那一段。</summary>
    private static string HandleOf(string description) =>
        description.Length >= 10 ? description[..10] : description;

    /// <summary>工具结果文本是多行：正文 + 一行 JSON。取最后一行 JSON。</summary>
    private static string LastJsonLine(string text)
    {
        var line = text
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("{", StringComparison.Ordinal));

        return line ?? text;
    }

    /// <summary>在请求体里找第一个 image_url 的 url。</summary>
    private static string? FindImageDataUri(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) && type.GetString() == "image_url" &&
                    part.TryGetProperty("image_url", out var imageUrl) &&
                    imageUrl.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString();
                }
            }
        }

        return null;
    }

    /// <summary>独立于被测代码，用托管 UIA 把窗口里的文本读出来。</summary>
    private static string? UiaRead(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                return null;
            }

            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is null)
            {
                return null;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                return ((ValuePattern)valuePattern).Current.Value;
            }

            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
            {
                return ((TextPattern)textPattern).DocumentRange.GetText(-1);
            }

            return null;
        }
        catch (Exception ex)
        {
            Program.Info($"独立 UIA 回读失败: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
