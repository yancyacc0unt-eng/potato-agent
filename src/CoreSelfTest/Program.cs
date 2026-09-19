// ============================================================================
// 大脑（PotatoAgent.Core.Brain）层自测 —— 可复跑工程，退出码 0 = 全部通过。
//
// 覆盖四块：
//   一、SSE 解析    ：故意把响应切成 1 字节一片（含切开汉字、切开 data: 行）、\r\n 变体、
//                     [DONE]、末尾没有换行的尾巴、空行分事件、注释心跳、多条 data
//   二、工具调用循环：本地 HttpListener 假服务端返回 tool_calls（arguments 故意分两片），
//                     验证工具真的执行、参数完整、第二轮带上了 assistant.tool_calls + role:"tool"
//   三、错误处理    ：400/401、中途断流、流内非法 JSON、取消、超时、连不上、配置不全
//                     —— 各自抛出明确的 ProviderException 子类，绝不裸崩
//   四、配置与密钥  ：写 → 读回逐字符一致 → 磁盘上搜不到明文（原文/base64/前后片段）
//                     → 文件损坏 / 缺失 / 密钥解不开时安全降级
//
// ⚠ 安全前提（本工程所有路径都遵守）：
//   * HTTP 只打给【本进程自己起的 HttpListener】：127.0.0.1 + 系统分配的临时端口，不发任何真实外网请求；
//   * 配置只写在 %TEMP% 下的临时目录，绝不读、绝不写 %APPDATA%\PotatoAgent 下的任何真实文件；
//   * 用到的"密钥"是本文件里现编的假串，不是任何真实凭据。
//
// 跑法：
//   dotnet build build\CoreSelfTest.csproj -c Debug
//   dotnet run   --project build\CoreSelfTest.csproj -c Debug
// ============================================================================

using System.Text;
using PotatoAgent.Core.Brain;

namespace PotatoAgent.Core.SelfTest;

internal static class Program
{
    /// <summary>假密钥：临时编的，只用来验证"落盘不带明文"，不是任何真实凭据。</summary>
    internal const string FakeKey = "sk-selftest-9F3aQ7xLm2VpR8tYw1ZbN4jH";

    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        Console.WriteLine("==================== PotatoAgent.Core 大脑层自测 ====================");
        Console.WriteLine($"运行时   : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}   进程 {(Environment.Is64BitProcess ? 64 : 32)} 位   OS {Environment.OSVersion.VersionString}");
        Console.WriteLine($"临时目录 : {Path.GetTempPath()}");
        Console.WriteLine("网络范围 : 仅 127.0.0.1 的本地假服务端（HttpListener），不发外部请求");
        Console.WriteLine("配置文件 : 仅 %TEMP% 下的临时目录，绝不碰 %APPDATA%\\PotatoAgent");
        Console.WriteLine();

        // 收尾时用它证明"我们没往用户真实配置目录里写东西"。
        var appDataExistedBefore = Directory.Exists(ConfigStore.DefaultDirectory);

        try
        {
            SseTests.Run();
            ToolLoopTests.Run();
            ErrorTests.Run();
            ConfigTests.Run();
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            GuardRealConfigUntouched(appDataExistedBefore);
        }

        return Report();
    }

    // ==================== 护栏：绝不碰用户真实配置 ====================

    private static void GuardRealConfigUntouched(bool existedBefore)
    {
        Console.WriteLine("---- 护栏：用户真实配置目录 ----");
        var directory = ConfigStore.DefaultDirectory;
        Console.WriteLine($"    {directory}");

        if (existedBefore)
        {
            Console.WriteLine("    （自测前它就已经存在，不动它，只记录）");
            return;
        }

        Check(!Directory.Exists(directory), "自测全程没有创建过用户真实配置目录（证明配置测试走的是注入的临时路径）");
        Console.WriteLine();
    }

    // ==================== 断言与汇总（格式照抄 Win32SelfTest） ====================

    internal static bool Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
        return ok;
    }

    internal static void Fail(string what)
    {
        _checks++;
        _failures++;
        Console.WriteLine($"    [FAIL] {what}");
    }

    /// <summary>截断，避免把整份 payload 打进控制台。</summary>
    internal static string Trim(string? text, int limit = 90)
    {
        if (string.IsNullOrEmpty(text)) return "<空>";
        var oneLine = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return oneLine.Length <= limit ? oneLine : oneLine[..limit] + "…";
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
