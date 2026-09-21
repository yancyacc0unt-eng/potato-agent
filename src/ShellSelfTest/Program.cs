// ============================================================================
// 受限控制台工具 pc_shell（PotatoAgent.Shell）自测 —— 可复跑工程，退出码 0 = 全部通过。
//
// 覆盖六块：
//   一、注册表    ：CreateAll / RegisterAll / 重名时 skipExisting 的行为 / Risk == Dangerous / Schema 合法
//   二、基本往返  ：echo 往返、中文"土豆 123"原样返回不乱码、退出码 7 如实传递、stderr 单独捕获
//   三、工作目录  ：相对 cwd 按 workspaceRoot 解析、默认 cwd 走 workspaceRoot、
//                   没有工作区时相对 cwd 报错、没有工作区时默认退回 %USERPROFILE%、目录不存在报错
//   四、超时      ：Start-Sleep 10 + 超时 2 秒 → 实测耗时、timedOut=true、部分输出保留、整棵进程树真被杀
//   五、输出封顶  ：stdout / stderr 各 32 KB 截断，带 [... truncated at 32768 bytes ...] 标记
//   六、护栏      ：空 command 报错、宽松取参、ct 取消能中断（OperationCanceledException）
//
// ⚠ 安全前提：所有目录都在 %TEMP% 下的一次性沙箱里，跑完递归删干净；
//   只起 powershell.exe / cmd.exe 做真实往返，不联网、不碰用户的任何真实目录；会话结束不留进程。
//
// 跑法：
//   dotnet build build\ShellSelfTest.csproj -c Debug
//   dotnet run   --project build\ShellSelfTest.csproj -c Debug
// ============================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;
using PotatoAgent.Shell;

namespace PotatoAgent.Shell.SelfTest;

internal static class Program
{
    private static int _pass;
    private static int _fail;

    /// <summary>一次性沙箱：所有被命令碰到的目录都在这里面。</summary>
    private static readonly string Sandbox =
        Path.Combine(Path.GetTempPath(), "potato-shell-selftest-" + Guid.NewGuid().ToString("N"));

    private static async Task<int> Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        var workspace = Path.Combine(Sandbox, "ws");
        var plain = Path.Combine(Sandbox, "plain");

        Console.WriteLine("==================== PotatoAgent.Shell pc_shell 自测 ====================");
        Console.WriteLine($"临时沙箱 : {Sandbox}");
        Console.WriteLine($"工作区   : {workspace}");
        Console.WriteLine("落盘范围 : 只在沙箱目录里；只起 powershell.exe / cmd.exe，不联网");
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(Sandbox);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(plain);

            Registration();

            var withWorkspace = ShellTools.CreateAll(() => workspace)[0];
            var withoutWorkspace = ShellTools.CreateAll(() => null)[0];

            await BasicRoundTrip(withWorkspace);
            await WorkingDirectory(workspace, plain, withoutWorkspace);
            await Timeouts(withWorkspace);
            await OutputCap(withWorkspace);
            await Guards(withWorkspace, workspace);
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup();

            Section("护栏");
            Check("沙箱目录跑完已删干净", !Directory.Exists(Sandbox));
            Console.WriteLine();
        }

        return Report();
    }

    // ==================== 一、注册表 ====================

    private static void Registration()
    {
        Section("一、注册表");

        var created = ShellTools.CreateAll();
        Check("CreateAll 默认建出 1 个工具", created.Count == 1);
        Check("工具名是 pc_shell", created.Count == 1 && created[0].Name == "pc_shell");
        Check("Risk == ToolRisk.Dangerous（基础档和高级档都会弹确认框）",
            created.Count == 1 && created[0].Risk == ToolRisk.Dangerous);

        var schemaIsObject = false;
        var commandIsRequired = false;
        var timeoutIsDocumented = false;
        try
        {
            using var schema = JsonDocument.Parse(created[0].ParametersJsonSchema);
            schemaIsObject = schema.RootElement.ValueKind == JsonValueKind.Object;
            commandIsRequired = schema.RootElement.TryGetProperty("required", out var required) &&
                                required.EnumerateArray().Any(e => e.GetString() == "command");
            timeoutIsDocumented = schema.RootElement.TryGetProperty("properties", out var props) &&
                                  props.TryGetProperty("timeout_seconds", out _);
        }
        catch (JsonException)
        {
            schemaIsObject = false;
        }

        Check("ParametersJsonSchema 是合法 JSON 对象", schemaIsObject);
        Check("Schema 把 command 列为必填", commandIsRequired);
        Check("Schema 里写了 cwd / timeout_seconds", timeoutIsDocumented);

        var registry = new ToolRegistry();
        Check("首次 RegisterAll 返回 1", ShellTools.RegisterAll(registry) == 1);
        Check("注册表里确实有 pc_shell", registry.Count == 1 && registry.TryGet("pc_shell", out _));

        Check("重名 + skipExisting=true 跳过，返回 0", ShellTools.RegisterAll(registry, null, skipExisting: true) == 0);
        Check("重名跳过后注册表还是 1 个", registry.Count == 1);

        var threw = false;
        try
        {
            ShellTools.RegisterAll(registry, null, skipExisting: false);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Check("重名 + skipExisting=false 直接抛 ArgumentException", threw);
        Check("带 workspaceRoot 回调的 RegisterAll 也能注册", ShellTools.RegisterAll(new ToolRegistry(), () => Sandbox) == 1);

        var nullRegistryThrew = false;
        try
        {
            ShellTools.RegisterAll(null!);
        }
        catch (ArgumentNullException)
        {
            nullRegistryThrew = true;
        }

        Check("registry 传 null 抛 ArgumentNullException", nullRegistryThrew);
    }

    // ==================== 二、基本往返 ====================

    private static async Task BasicRoundTrip(ITool tool)
    {
        Section("二、基本往返");

        var version = await Call(tool, Args(new { command = "$PSVersionTable.PSVersion.ToString()" }));
        Info($"子进程 PowerShell 版本: {Trim(version.Content)}");

        var echo = await Call(tool, Args(new { command = "echo hello-from-pc-shell" }));
        Check("echo 往返成功（Success=true）", echo.Success);
        Check("stdout 里能拿到 hello-from-pc-shell", echo.Content.Contains("hello-from-pc-shell", StringComparison.Ordinal));
        Check("退出码是 0，且 Data 里带了 exitCode", DataInt(echo, "exitCode") == 0);
        Check("Data 里有 cwd / durationMs / truncated / timedOut 四个字段",
            !string.IsNullOrEmpty(DataString(echo, "cwd")) &&
            DataLong(echo, "durationMs") >= 0 &&
            DataBool(echo, "truncated") == false &&
            DataBool(echo, "timedOut") == false);
        Check("文本里写明了 cwd / exit code / duration / stdout / stderr",
            echo.Content.Contains("cwd: ", StringComparison.Ordinal) &&
            echo.Content.Contains("exit code: 0", StringComparison.Ordinal) &&
            echo.Content.Contains("duration: ", StringComparison.Ordinal) &&
            echo.Content.Contains("--- stdout ---", StringComparison.Ordinal) &&
            echo.Content.Contains("--- stderr ---", StringComparison.Ordinal));
        Info($"echo 实测耗时 {DataLong(echo, "durationMs")} ms");

        var chinese = await Call(tool, Args(new { command = "Write-Output '土豆 123'" }));
        Check("中文原样返回（Content 含 土豆 123）", chinese.Content.Contains("土豆 123", StringComparison.Ordinal));
        Check("中文没有乱码（不含 U+FFFD 替换字符）", !chinese.Content.Contains('\uFFFD'));
        Info($"中文往返原文: {Trim(chinese.Content)}");

        var mixed = await Call(tool, Args(new { command = "echo '混合 Mixed 456 中文'" }));
        Check("中英混排原样返回", mixed.Content.Contains("混合 Mixed 456 中文", StringComparison.Ordinal));

        var seven = await Call(tool, Args(new { command = "exit 7" }));
        Check("退出码 7 如实传递（Data.exitCode == 7）", DataInt(seven, "exitCode") == 7);
        Check("跑完的命令算成功，退出码是数据不是异常（Success=true）", seven.Success);
        Check("文本里写明 exit code: 7", seven.Content.Contains("exit code: 7", StringComparison.Ordinal));
        Check("文本开头就提示非零退出码", seven.Content.Contains("exited with code 7", StringComparison.Ordinal));
        Check("timedOut=false（有退出码就说明没被超时杀掉）", DataBool(seven, "timedOut") == false);

        var stderr = await Call(tool, Args(new { command = "[Console]::Error.WriteLine('boom-中文错误')" }));
        Check("stderr 单独捕获（有 --- stderr --- 段）", stderr.Content.Contains("--- stderr ---", StringComparison.Ordinal));
        Check("stderr 里的中文也不乱码", stderr.Content.Contains("boom-中文错误", StringComparison.Ordinal));
    }

    // ==================== 三、工作目录 ====================

    private static async Task WorkingDirectory(string workspace, string plain, ITool withoutWorkspace)
    {
        Section("三、工作目录");

        var sub = Path.Combine(workspace, "sub");
        Directory.CreateDirectory(sub);

        var withWorkspace = ShellTools.CreateAll(() => workspace)[0];

        var relative = await Call(withWorkspace, Args(new { command = "(Get-Location).Path", cwd = "sub" }));
        Check("相对 cwd 按 workspaceRoot 解析成功", relative.Success);
        Check("子进程实际就在 <工作区>\\sub 里跑", relative.Content.Contains(sub, StringComparison.OrdinalIgnoreCase));
        Check("Data.cwd 就是解析后的绝对路径", Same(DataString(relative, "cwd"), sub));

        var defaultCwd = await Call(withWorkspace, Args(new { command = "(Get-Location).Path" }));
        Check("不给 cwd 时默认走 workspaceRoot", defaultCwd.Success && defaultCwd.Content.Contains(workspace, StringComparison.OrdinalIgnoreCase));
        Check("Data.cwd 就是工作区根", Same(DataString(defaultCwd, "cwd"), workspace));

        var absolute = await Call(withoutWorkspace, Args(new { command = "(Get-Location).Path", cwd = plain }));
        Check("没有工作区时，绝对 cwd 照样能用", absolute.Success && absolute.Content.Contains(plain, StringComparison.OrdinalIgnoreCase));

        var noWorkspaceRelative = await Call(withoutWorkspace, Args(new { command = "echo never-runs", cwd = "sub" }));
        Check("没有工作区 + 相对 cwd → 失败", !noWorkspaceRelative.Success);
        Check("报错说清了原因（提到 workspace）", noWorkspaceRelative.Content.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Check("报错时没有真的去跑命令", !noWorkspaceRelative.Content.Contains("never-runs", StringComparison.Ordinal));

        var fallback = await Call(withoutWorkspace, Args(new { command = "(Get-Location).Path" }));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check("没有工作区 + 没给 cwd → 落在 %USERPROFILE%", fallback.Success && Same(DataString(fallback, "cwd"), profile));
        Info($"无工作区默认 cwd: {DataString(fallback, "cwd")}");

        var missing = await Call(withWorkspace, Args(new { command = "echo never-runs", cwd = "no-such-dir-xyz" }));
        Check("cwd 不存在 → 失败并说明", !missing.Success && missing.Content.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 四、超时与进程树 ====================

    private static async Task Timeouts(ITool tool)
    {
        Section("四、超时与进程树");

        var watch = Stopwatch.StartNew();
        var timedOut = await Call(tool, Args(new
        {
            command = "Write-Output 'marker-before-timeout'; [Console]::Out.Flush(); Start-Sleep -Seconds 10",
            timeout_seconds = 2,
        }));
        watch.Stop();

        Check("超时算失败（Success=false）", !timedOut.Success);
        Check($"实测耗时 {watch.ElapsedMilliseconds} ms < 6000 ms（没等满 10 秒）", watch.ElapsedMilliseconds < 6000);
        Check("文本里说清超时与进程树已杀（TIMEOUT）", timedOut.Content.Contains("TIMEOUT", StringComparison.Ordinal));
        Check("文本里带上了超时秒数（2 s）", timedOut.Content.Contains("after 2 s", StringComparison.Ordinal));
        Check("结构化字段里 timedOut=true", timedOut.Content.Contains("\"timedOut\":true", StringComparison.Ordinal));
        Check("超时也把已经拿到的部分输出带回来", timedOut.Content.Contains("marker-before-timeout", StringComparison.Ordinal));
        Info($"超时用例（timeout 2s）实测 {watch.ElapsedMilliseconds} ms");

        // 进程树：让 powershell 起一个会活 60 秒的 cmd.exe 孙进程并把 pid 写进文件，
        // 超时后如果整棵树真被杀掉，这个 pid 就该消失。
        var pidFile = Path.Combine(Sandbox, "child.pid");
        var script =
            "$c = Start-Process -FilePath cmd.exe -ArgumentList '/c','ping -n 60 127.0.0.1 > nul' -PassThru -WindowStyle Hidden; " +
            "Set-Content -Path '" + pidFile + "' -Value $c.Id; " +
            "Start-Sleep -Seconds 30";

        var treeWatch = Stopwatch.StartNew();
        var tree = await Call(tool, Args(new { command = script, timeout_seconds = 5 }));
        treeWatch.Stop();

        Check($"进程树用例超时后 {treeWatch.ElapsedMilliseconds} ms 内返回", !tree.Success && treeWatch.ElapsedMilliseconds < 10000);
        Check("进程树用例的 timedOut=true", tree.Content.Contains("\"timedOut\":true", StringComparison.Ordinal));

        // 交互式命令：stdin 一启动就关掉，所以 Read-Host 这类必须【快速失败】，而不是一直等人敲键盘、
        // 把整次调用拖到超时。这里故意把 timeout 给到 30 秒 —— 真挂住了就必然超时。
        var interactiveWatch = Stopwatch.StartNew();
        var interactive = await Call(tool, Args(new
        {
            command = "Read-Host -Prompt 'type something'",
            timeout_seconds = 30,
        }));
        interactiveWatch.Stop();

        Check($"交互式命令快速失败而不是挂住（实测 {interactiveWatch.ElapsedMilliseconds} ms < 6000）",
            interactiveWatch.ElapsedMilliseconds < 6000);
        Check("快速失败的那次没有被误判成超时", !interactive.Content.Contains("TIMEOUT", StringComparison.Ordinal));
        Info($"Read-Host 实测 {interactiveWatch.ElapsedMilliseconds} ms，Success={interactive.Success}，退出码 {DataInt(interactive, "exitCode")}");
        Info($"Read-Host 输出: {Trim(interactive.Content)}");

        if (!File.Exists(pidFile))
        {
            Fail("孙进程的 pid 文件没写出来，进程树用例不可判（可能是机器太慢）");
        }
        else
        {
            var childPid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
            var gone = await WaitGoneAsync(childPid, 5000);

            Check($"整棵进程树真被杀（孙进程 cmd.exe pid {childPid} 已消失）", gone);
            Info($"孙进程 pid {childPid}: {(gone ? "已消失" : "还活着")}");

            if (!gone)
            {
                KillQuietly(childPid);
            }
        }
    }

    // ==================== 五、输出封顶 ====================

    private static async Task OutputCap(ITool tool)
    {
        Section("五、输出封顶 32 KB");

        var big = await Call(tool, Args(new { command = "1..4000 | ForEach-Object { 'x' * 20 }" }));
        Check("超长 stdout 仍然算成功（退出码 0）", big.Success);
        Check("Data.truncated == true", DataBool(big, "truncated") == true);
        Check("带 [... truncated at 32768 bytes ...] 标记",
            big.Content.Contains("[... truncated at 32768 bytes ...]", StringComparison.Ordinal));
        Check($"Content 长度被压住（实测 {big.Content.Length} 字符 < 45000）", big.Content.Length < 45000);
        Info($"stdout 用例：Content {big.Content.Length} 字符（原始输出约 84000 字节）");

        var bigErr = await Call(tool, Args(new { command = "1..4000 | ForEach-Object { [Console]::Error.WriteLine('y' * 20) }" }));
        Check("超长 stderr 也被截断（truncated=true）", DataBool(bigErr, "truncated") == true);
        Check("stderr 段里也有截断标记",
            bigErr.Content.Contains("--- stderr ---", StringComparison.Ordinal) &&
            bigErr.Content.Contains("[... truncated at 32768 bytes ...]", StringComparison.Ordinal));
        Info($"stderr 用例：Content {bigErr.Content.Length} 字符（原始输出约 84000 字节）");
    }

    // ==================== 六、护栏与宽松取参 ====================

    private static async Task Guards(ITool tool, string workspace)
    {
        Section("六、护栏与宽松取参");

        var empty = await Call(tool, Args(new { command = string.Empty }));
        Check("空 command → 失败", !empty.Success);
        Check("报错里点明 command 参数", empty.Content.Contains("command", StringComparison.OrdinalIgnoreCase));

        var missing = await Call(tool, "{}");
        Check("完全没给 command → 失败", !missing.Success);

        var blank = await Call(tool, Args(new { command = "   " }));
        Check("全是空白的 command → 失败", !blank.Success);

        var wrongType = await Call(tool, Args(new { command = "echo loose-args-ok", timeout_seconds = "abc" }));
        Check("timeout_seconds 类型写错时退回默认值，照样跑", wrongType.Success && wrongType.Content.Contains("loose-args-ok", StringComparison.Ordinal));

        var huge = await Call(tool, Args(new { command = "echo clamp-ok", timeout_seconds = 99999 }));
        Check("timeout_seconds 超上限被夹住（照样跑完）", huge.Success && huge.Content.Contains("clamp-ok", StringComparison.Ordinal));

        var zero = await Call(tool, Args(new { command = "echo zero-ok", timeout_seconds = 0 }));
        Check("timeout_seconds 给 0 被夹到下限（不挂死）", zero.Success && zero.Content.Contains("zero-ok", StringComparison.Ordinal));

        // 取消：ToolRegistry 的约定是上抛 OperationCanceledException，不伪装成"工具失败"。
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(700);

        var cancelWatch = Stopwatch.StartNew();
        var cancelled = false;
        try
        {
            await Call(tool, Args(new { command = "Start-Sleep -Seconds 30" }), cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        cancelWatch.Stop();

        Check("ct 取消 → 上抛 OperationCanceledException", cancelled);
        Check($"取消后 {cancelWatch.ElapsedMilliseconds} ms 内就返回（没等满 30 秒）", cancelWatch.ElapsedMilliseconds < 6000);

        using var already = new CancellationTokenSource();
        already.Cancel();
        var preCancelled = false;
        try
        {
            await Call(tool, Args(new { command = "echo never-runs" }), already.Token);
        }
        catch (OperationCanceledException)
        {
            preCancelled = true;
        }

        Check("调用前就已取消 → 立刻上抛", preCancelled);

        // 经 ToolRegistry 调用时，取消也要按同样的契约上抛（接线方走的就是这条路）。
        var registry = new ToolRegistry();
        ShellTools.RegisterAll(registry, () => workspace);

        using var viaRegistry = new CancellationTokenSource();
        viaRegistry.CancelAfter(700);
        var registryThrew = false;
        try
        {
            using var doc = JsonDocument.Parse(Args(new { command = "Start-Sleep -Seconds 30" }));
            await registry.InvokeAsync("pc_shell", doc.RootElement, viaRegistry.Token);
        }
        catch (OperationCanceledException)
        {
            registryThrew = true;
        }

        Check("经 ToolRegistry.InvokeAsync 取消时同样上抛（契约一致）", registryThrew);
        Check("取消后注册表里的工具还在（取消不破坏注册表）", registry.Count == 1);
    }

    // ==================== 调用与断言的小工具 ====================

    /// <summary>把匿名对象序列化成参数 JSON（路径里有反斜杠，手拼字符串很容易拼错）。</summary>
    private static string Args(object value) => JsonSerializer.Serialize(value);

    /// <summary>直接调工具（不经过注册表；注册表那条路第六节单独验）。</summary>
    private static async Task<ToolResult> Call(ITool tool, string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return await tool.InvokeAsync(doc.RootElement, ct);
    }

    private static int? DataInt(ToolResult result, string name) =>
        result.Data is { } data && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static long? DataLong(ToolResult result, string name) =>
        result.Data is { } data && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static string? DataString(ToolResult result, string name) =>
        result.Data is { } data && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? DataBool(ToolResult result, string name) =>
        result.Data is { } data && data.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>等一个 pid 消失（最多 <paramref name="milliseconds"/> 毫秒）。</summary>
    private static async Task<bool> WaitGoneAsync(int pid, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (!IsAlive(pid))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>兜底收尾：用例失败时别把孙进程留在机器上。</summary>
    private static void KillQuietly(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 已经没了就是最好的结果。
        }
    }

    private static void Check(string name, bool condition)
    {
        if (condition) { _pass++; } else { _fail++; }
        Console.WriteLine($"    [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    private static void Fail(string name)
    {
        _fail++;
        Console.WriteLine($"    [FAIL] {name}");
    }

    private static void Section(string title) => Console.WriteLine($"---- {title} ----");

    private static void Info(string text) => Console.WriteLine($"    ....   {text}");

    /// <summary>截断长文本，取证时不要刷屏（顺便把换行压成空格）。</summary>
    private static string Trim(string? text, int max = 120)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<空>";
        }

        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>两个路径是不是同一个（Windows：大小写不敏感、尾分隔符不算差别）。</summary>
    private static bool Same(string? actual, string expected) =>
        actual is not null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(actual),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            StringComparison.OrdinalIgnoreCase);

    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(Sandbox))
            {
                Directory.Delete(Sandbox, recursive: true);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"    （沙箱没删干净，请手工删 {Sandbox}: {error.GetType().Name}: {error.Message}）");
        }
    }

    private static int Report()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine($"PASS {_pass} / FAIL {_fail}");
        Console.WriteLine("==================================================================");
        return _fail == 0 ? 0 : 1;
    }
}
