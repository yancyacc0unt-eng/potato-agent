// pc_shell：模型能在用户电脑上跑一条 PowerShell 命令，护栏是"每次都要用户点确认"（ToolRisk.Dangerous）。
//
// 这一层刻意【不做命令黑名单】：黑名单绕得过去、又会拦掉正当操作，还会给用户虚假的安全感。
// 真正的护栏是确认框 —— 它把模型给的参数原文摊开给用户看，用户点了"执行"才跑。
// 这里能加的只是几条物理性的硬约束：关掉 stdin（交互式命令快速失败而不是挂死）、
// 输出封顶 32 KB、超时杀整棵进程树、如实回报退出码。

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Shell.Tools;

/// <summary>
/// <c>pc_shell</c>：在用户的 Windows 上跑一条 PowerShell 命令，把工作目录 / 退出码 / 耗时 / stdout / stderr 交回给模型。
/// </summary>
/// <remarks>
/// <para>
/// 危险等级是 <see cref="ToolRisk.Dangerous"/>：基础档与高级档权限<b>都会</b>弹确认框，
/// 确认框里展示的就是模型给的参数原文 —— 这是本工具唯一、也是主要的护栏。
/// </para>
/// <para>
/// 跑的是 Windows PowerShell 5.1（<c>powershell.exe</c>，本机没有 PowerShell 7），隐藏窗口、非交互：
/// stdin 一启动就关掉，交互式命令立刻拿到 EOF 报错，不会把整次调用挂到超时。
/// </para>
/// <para>
/// 成功与失败的约定：命令只要<b>跑完</b>就是成功结果（<c>Success=true</c>），退出码如实放在文本和
/// <c>Data.exitCode</c> 里 —— 非零退出码是命令自己的结论（脚本写了 <c>exit 1</c>），不属于"工具坏了"。
/// 只有<b>超时</b>才是失败结果（<c>Success=false</c>，文本里说清进程树已杀、并带上已拿到的部分输出，
/// 结构化 JSON 附在文本末尾）；参数不对同样是失败；调用方取消则按 <see cref="ToolRegistry"/> 的约定上抛
/// <see cref="OperationCanceledException"/>。
/// </para>
/// <para>
/// 编码：命令前面会加一段内部前置（把 <c>[Console]::OutputEncoding</c> 与 <c>$OutputEncoding</c> 设成 UTF-8），
/// 加上 <c>StandardOutputEncoding</c> / <c>StandardErrorEncoding</c> 指定 UTF-8，中文来回都不乱码。
/// 那段前置是<b>实现细节</b>，只做前缀，不改写模型给的 <c>command</c> 本身。
/// </para>
/// </remarks>
public sealed class PcShellTool : ITool
{
    /// <summary>没给 <c>timeout_seconds</c> 时的默认超时（秒）。</summary>
    private const int DefaultTimeoutSeconds = 30;

    /// <summary>超时下限（秒）：0 或负数会被夹到这里，免得"不许超时"变成挂死。</summary>
    private const int MinTimeoutSeconds = 1;

    /// <summary>超时上限（秒）：模型给多大都不超过它。</summary>
    private const int MaxTimeoutSeconds = 300;

    private readonly Func<string?>? _workspaceRoot;

    /// <summary>
    /// 建一个 <c>pc_shell</c> 工具。
    /// </summary>
    /// <param name="workspaceRoot">
    /// 返回"当前工作区根目录"的回调；没有工作区时返回 null。
    /// 每次执行命令时才去问它（用户中途切工作区立刻生效），用来解析相对 <c>cwd</c> 和决定默认工作目录。
    /// 传 null 等同于"永远没有工作区"：相对 <c>cwd</c> 直接报错，默认工作目录退回 %USERPROFILE%。
    /// </param>
    public PcShellTool(Func<string?>? workspaceRoot = null)
    {
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>工具名：<c>pc_shell</c>（小写 snake_case）。</summary>
    public string Name => "pc_shell";

    /// <summary>
    /// 英文描述，老实告诉模型这个工具能干什么、边界在哪（危险、每次弹确认、非交互、输出封顶、超时被杀）。
    /// </summary>
    public string Description =>
        "Run one PowerShell command on the user's Windows computer and return its output. " +
        "This is a real shell with the user's own privileges: the command can read and change files, start programs, " +
        "change settings and reach the network. There is no sandbox and no command blacklist - the safety net is that " +
        "the user sees the exact command you wrote and has to confirm it before anything runs, so never hide what a " +
        "command does and do not ask for destructive work you would not explain out loud. " +
        "It runs Windows PowerShell 5.1 (powershell.exe) with -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass. " +
        "Standard input is closed, so interactive commands (Read-Host, pause, more, ...) fail immediately instead of " +
        "waiting forever - pass everything the command needs as arguments. " +
        "The working directory is the open workspace folder (or the user profile when no workspace is open), and can be " +
        "overridden with 'cwd'. Returns the working directory, the exit code, the duration in milliseconds and the " +
        "captured stdout / stderr. Each stream is cut off after 32 KB and marked with a truncation note. " +
        "The command is killed together with its whole process tree after timeout_seconds (default 30, max 300); " +
        "a timeout is reported as a failed call that still carries the partial output. A finished command always " +
        "comes back as a successful call, so read the exit code and stderr before assuming it did what you wanted.";

    /// <summary>
    /// 参数 JSON Schema：<c>command</c>（必填 string）、<c>cwd</c>（可选 string）、<c>timeout_seconds</c>（可选 int，1 到 300）。
    /// </summary>
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "The PowerShell command (or several statements separated by ';') to run. It runs with the user's privileges, so keep it exactly as destructive as you mean it to be."
            },
            "cwd": {
              "type": "string",
              "description": "Working directory for the command. An absolute path is used as is; a relative path is resolved against the open workspace folder. Defaults to the workspace folder, or to the user profile when no workspace is open."
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "How long the command may run before it and its whole process tree are killed. Default 30, minimum 1, maximum 300.",
              "minimum": 1,
              "maximum": 300
            }
          },
          "required": ["command"]
        }
        """;

    /// <summary>危险等级：<see cref="ToolRisk.Dangerous"/> —— 两档权限都要弹确认框。</summary>
    public ToolRisk Risk => ToolRisk.Dangerous;

    /// <summary>
    /// 跑一条命令并如实回报。参数坏掉时返回英文失败结果（不抛异常）；
    /// <paramref name="ct"/> 取消时先杀进程树再上抛 <see cref="OperationCanceledException"/>（ToolRegistry 的约定）。
    /// </summary>
    /// <param name="args">模型给的参数对象（字段可能缺、类型可能不对，这里自己兜住）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>给模型看的结果：cwd / 退出码 / 耗时 / stdout / stderr，外加一份结构化 JSON。</returns>
    public async Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var command = ShellArgs.Text(args, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return ToolResult.Error(
                ShellArgs.Required("command") + " Give the PowerShell command to run as a plain string, e.g. \"Get-ChildItem\".");
        }

        if (!TryResolveWorkingDirectory(args, out var cwd, out var cwdError))
        {
            return ToolResult.Error(cwdError!);
        }

        var timeoutSeconds = Math.Clamp(
            ShellArgs.Integer(args, "timeout_seconds") ?? DefaultTimeoutSeconds,
            MinTimeoutSeconds,
            MaxTimeoutSeconds);

        ShellRunResult run;
        try
        {
            run = await PowerShellRunner.RunAsync(command!, cwd, timeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户按了停止：原样上抛，别伪装成"工具失败"（ToolRegistry 也是这个约定）。
            throw;
        }
        catch (Exception error)
        {
            return ToolResult.Error($"The command could not be started: {error.GetType().Name}: {error.Message}");
        }

        var text = BuildText(run, cwd, timeoutSeconds);

        var dataJson = JsonSerializer.Serialize(new
        {
            exitCode = run.ExitCode,
            durationMs = run.DurationMs,
            cwd,
            truncated = run.Truncated,
            timedOut = run.TimedOut,
        });

        if (run.TimedOut)
        {
            // 超时是唯一"失败但仍有结果"的分支：ToolResult 的失败出口没有 Data 槽位（Error() 只吃文本），
            // 所以把同一份结构化 JSON 附在文本末尾 —— 模型和自动化检查照样拿得到这几个字段。
            return ToolResult.Error(text + Environment.NewLine + dataJson);
        }

        // 命令只要跑完了就算成功，退出码如实放在文本和 Data 的 exitCode 里。
        // 非零退出码是命令自己的结论（findstr 没找到、脚本里写了 exit 1），属"结果"不属"工具坏了"：
        // 把它改写成另一种含义，模型反而读不懂到底发生了什么。
        return ToolResult.OkJson(text, dataJson);
    }

    /// <summary>
    /// 解析工作目录：给了 <c>cwd</c> 就按它（相对路径按工作区根解析，没工作区就报错）；
    /// 没给就用工作区根，再没有就用 %USERPROFILE%。解析完必须真实存在。
    /// </summary>
    private bool TryResolveWorkingDirectory(JsonElement args, out string cwd, out string? error)
    {
        error = null;
        cwd = string.Empty;

        var root = CurrentWorkspaceRoot();
        var raw = ShellArgs.Text(args, "cwd");

        string full;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var candidate = raw!.Trim();

            if (Path.IsPathFullyQualified(candidate))
            {
                full = Path.GetFullPath(candidate);
            }
            else if (root is not null)
            {
                full = Path.GetFullPath(Path.Combine(root, candidate));
            }
            else
            {
                error =
                    $"The 'cwd' argument is relative ('{candidate}') but no workspace is open, so there is nothing to " +
                    "resolve it against. Open a workspace, or pass an absolute path.";
                return false;
            }
        }
        else
        {
            full = root ?? UserProfile();
        }

        cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(full));

        if (!Directory.Exists(cwd))
        {
            error = $"The working directory '{cwd}' does not exist. Create it first, or pass a directory that exists.";
            return false;
        }

        return true;
    }

    /// <summary>当前工作区根目录；没有工作区、或回调自己炸了都返回 null（缺工作区不该让工具崩）。</summary>
    private string? CurrentWorkspaceRoot()
    {
        if (_workspaceRoot is null)
        {
            return null;
        }

        try
        {
            var root = _workspaceRoot();
            return string.IsNullOrWhiteSpace(root) ? null : root!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>没有工作区时的默认工作目录：%USERPROFILE%（拿不到就退回临时目录）。</summary>
    private static string UserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? Path.GetTempPath() : profile;
    }

    /// <summary>
    /// 拼给模型看的文本：先说清楚是不是超时 / 非零退出，再给 cwd、退出码、耗时，最后是两路输出。
    /// </summary>
    private static string BuildText(ShellRunResult run, string cwd, int timeoutSeconds)
    {
        var text = new StringBuilder();

        if (run.TimedOut)
        {
            text.Append("TIMEOUT: the command was still running after ")
                .Append(timeoutSeconds.Invariant())
                .Append(" s, so it and its whole process tree were killed. ")
                .AppendLine("Whatever it had already printed is shown below.");
        }
        else if (run.ExitCode != 0)
        {
            text.Append("The command exited with code ").Append(run.ExitCode.Invariant()).AppendLine(".");
        }

        text.Append("cwd: ").AppendLine(cwd);
        text.Append("exit code: ").AppendLine(run.ExitCode.Invariant());
        text.Append("duration: ").Append(run.DurationMs.Invariant()).AppendLine(" ms");

        text.AppendLine("--- stdout ---");
        AppendStream(text, run.Stdout);

        text.AppendLine("--- stderr ---");
        AppendStream(text, run.Stderr);

        return text.ToString().TrimEnd();
    }

    /// <summary>写一路输出；空的写 <c>(no output)</c>，没换行结尾的补一个，免得两段粘在一起。</summary>
    private static void AppendStream(StringBuilder text, string content)
    {
        if (content.Length == 0)
        {
            text.AppendLine("(no output)");
            return;
        }

        text.Append(content);

        if (!content.EndsWith('\n'))
        {
            text.AppendLine();
        }
    }
}
