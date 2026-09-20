// 自测程序：把 AgentSession 的离线闭环、权限门、真实 pc_* 工具逐条跑一遍。
//
// 全程不连外网（模型侧是 127.0.0.1 上的假服务端），不碰用户的键盘鼠标
// （唯一的输入注入是打给自测自己启动的记事本，跑完连进程一起收掉）。

using System.Diagnostics;
using System.Text;

namespace PotatoAgent.Agent.SelfTest;

internal static class Program
{
    private static int _failed;
    private static int _passed;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 没有真控制台（重定向）时设置编码会抛，忽略即可。
        }

        Console.WriteLine("=== potatoAgent AgentSession + pc_* 自测 ===");
        Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine();

        // key 是给命令行过滤用的 ASCII 短名（中文标题在没设 UTF-8 的控制台里传进来会被吃掉）。
        var tests = new (string Key, string Title, Func<Task> Run)[]
        {
            ("chain", "【实测 1】离线全链路（假服务器 + 假工具）", ChainTests.OfflineToolLoopAsync),
            ("gate", "【实测 2】权限门（无 approver / 拒绝 / 允许 / 记住允许）", ChainTests.ApprovalGateAsync),
            ("screen", "【实测 3】真实 pc_state + pc_screenshot", Win32ToolTests.RealStateAndScreenshotAsync),
            ("notepad", "【实测 4】只对自测自己开的记事本注入输入", Win32ToolTests.NotepadIsolationAsync),
            ("stream", "【实测 7】流式：增量是不是逐个、及时冒出来的", StreamingTests.IncrementalDeliveryAsync),
        };

        // 可选过滤：传一个 key 就只跑那几条（复跑一条实测时省时间，也不会去碰别的窗口）。
        var filter = args.Length > 0 ? args[0] : null;
        if (filter is not null)
        {
            Console.WriteLine($"过滤: 只跑 key 含 \"{filter}\" 的用例");
            Console.WriteLine();
        }

        foreach (var (key, title, run) in tests)
        {
            if (filter is not null && !key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await RunAsync(title, run);
        }

        Console.WriteLine();
        Console.WriteLine($"=== 通过 {_passed}，失败 {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string title, Func<Task> test)
    {
        Console.WriteLine(title);
        try
        {
            await test();
        }
        catch (Exception ex)
        {
            Fail($"测试自己崩了: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>断言一条。</summary>
    public static void Check(bool ok, string label)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [OK]   {label}");
        }
        else
        {
            Fail(label);
        }
    }

    /// <summary>断言失败。</summary>
    public static void Fail(string label)
    {
        _failed++;
        Console.WriteLine($"  [FAIL] {label}");
    }

    /// <summary>一条不带判断的观察记录（给报告取证用）。</summary>
    public static void Info(string text) => Console.WriteLine($"  ....   {text}");

    /// <summary>截断长文本，避免刷屏。</summary>
    public static string Trim(string? text, int max = 160)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<空>";
        }

        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>当前有几个 notepad 进程，返回它们的 pid。</summary>
    public static HashSet<int> NotepadPids()
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("notepad"))
        {
            pids.Add(process.Id);
            process.Dispose();
        }

        return pids;
    }
}
