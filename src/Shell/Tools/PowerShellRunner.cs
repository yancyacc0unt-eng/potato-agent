// pc_shell 的执行器：起 powershell.exe、抓 stdout/stderr、超时杀整棵进程树。
//
// 这里只管"把一条命令跑完并如实拿到结果"，不碰确认框、不碰参数解析（那些在 PcShellTool 里）。
//
// 几条硬约束都落在这个文件里：
//   1. stdin 一启动就关掉：交互式命令（Read-Host / pause / more）拿到 EOF 立刻报错，而不是把整次调用挂到超时。
//   2. 输出封顶 32 KB：超了截断并写明标记，同时继续把管道读干净（丢弃多余部分），
//      否则子进程会因为我们不再读管道而卡在写上面，超时都会变得不准。
//   3. 超时 Kill(entireProcessTree: true)：连孙进程一起杀，并把【已经读到的部分输出】照样带回给模型。
//   4. 取消（ct）按 ToolRegistry 的约定上抛 OperationCanceledException —— 先杀进程树再抛，绝不留下孤儿进程。

using System.Diagnostics;
using System.Text;

namespace PotatoAgent.Shell.Tools;

/// <summary>一次命令执行的结果（给 <see cref="PcShellTool"/> 拼给模型看的文本用）。</summary>
internal sealed class ShellRunResult
{
    /// <summary>是不是被超时杀掉的（true 时退出码没有意义，一律 -1）。</summary>
    internal bool TimedOut { get; init; }

    /// <summary>stdout 有没有被截断。</summary>
    internal bool StdoutTruncated { get; init; }

    /// <summary>stderr 有没有被截断。</summary>
    internal bool StderrTruncated { get; init; }

    /// <summary>进程退出码；没拿到（被强杀）时是 -1。</summary>
    internal int ExitCode { get; init; }

    /// <summary>从 Start 到拿到结果的实测耗时（毫秒）。</summary>
    internal long DurationMs { get; init; }

    /// <summary>stdout 文本（已截断，尾部半个汉字产生的替换字符已剥掉）。</summary>
    internal string Stdout { get; init; } = string.Empty;

    /// <summary>stderr 文本。</summary>
    internal string Stderr { get; init; } = string.Empty;

    /// <summary>任意一路被截断就算截断（回给模型的 truncated 字段）。</summary>
    internal bool Truncated => StdoutTruncated || StderrTruncated;
}

/// <summary>跑一次 <c>powershell.exe</c> 并如实回收结果。</summary>
internal static class PowerShellRunner
{
    /// <summary>stdout / stderr 各自的封顶字节数。</summary>
    internal const int OutputCapBytes = 32768;

    /// <summary>截断标记（跟着 <see cref="OutputCapBytes"/> 走，不会说一套写一套）。</summary>
    internal static readonly string TruncationMarker = $"[... truncated at {OutputCapBytes} bytes ...]";

    /// <summary>杀完进程后，等管道里剩余字节的上限（毫秒）。</summary>
    private const int DrainGraceMs = 2000;

    /// <summary>等进程真正退出的上限（毫秒），防止 Kill 失败时把整次调用挂死。</summary>
    private const int ReapGraceMs = 5000;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    // 内部前置：让 PowerShell 按 UTF-8 往被重定向的管道里写，中文才不会变成乱码。
    // 它只是【加在模型给的命令前面】，不改写命令本身（模型说的每个字符都原样进 -Command）。
    private const string EncodingPrelude =
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8;";

    /// <summary>
    /// 跑一条 PowerShell 命令。
    /// </summary>
    /// <param name="command">模型给的命令原文（必填、非空白，调用方已经校验过）。</param>
    /// <param name="cwd">已经解析并确认存在的绝对工作目录。</param>
    /// <param name="timeoutSeconds">超时秒数（调用方已经夹到 1 到 300）。</param>
    /// <param name="ct">取消令牌：取消时先杀整棵进程树，再上抛 <see cref="OperationCanceledException"/>。</param>
    /// <returns>执行结果；超时也算正常返回（<see cref="ShellRunResult.TimedOut"/> 为 true）。</returns>
    internal static async Task<ShellRunResult> RunAsync(string command, string cwd, int timeoutSeconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        // 用 ArgumentList 逐项传参，绝不自己拼引号：命令里的引号、空格、& 、$ 都不会被命令行解析吃掉。
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(EncodingPrelude + " " + command);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("powershell.exe did not start.");
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"could not start powershell.exe: {error.Message}", error);
        }

        // 立刻关掉 stdin：交互式命令马上拿到 EOF 报错（快速失败），而不是挂到超时。
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // 进程可能秒退，管道关不上不影响结果。
        }

        var stdout = new CappedCapture(OutputCapBytes);
        var stderr = new CappedCapture(OutputCapBytes);
        var stdoutPump = PumpAsync(process.StandardOutput.BaseStream, stdout);
        var stderrPump = PumpAsync(process.StandardError.BaseStream, stderr);

        var timedOut = false;

        try
        {
            using var clock = CancellationTokenSource.CreateLinkedTokenSource(ct);
            clock.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await process.WaitForExitAsync(clock.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            if (ct.IsCancellationRequested)
            {
                await ReapAsync(process).ConfigureAwait(false);
                await DrainAsync(stdoutPump, stderrPump).ConfigureAwait(false);

                // 用户按了停止：按 ToolRegistry 的约定上抛，别伪装成"工具失败"。
                throw new OperationCanceledException(
                    "pc_shell was cancelled by the caller; the PowerShell process tree was killed.", ct);
            }

            timedOut = true;
        }

        if (timedOut)
        {
            await ReapAsync(process).ConfigureAwait(false);
        }

        // 进程已经没了，管道里剩的字节读干净（或有上限地等一等）再收工。
        await DrainAsync(stdoutPump, stderrPump).ConfigureAwait(false);
        stopwatch.Stop();

        return new ShellRunResult
        {
            TimedOut = timedOut,
            ExitCode = TryGetExitCode(process),
            DurationMs = stopwatch.ElapsedMilliseconds,
            StdoutTruncated = stdout.IsTruncated,
            StderrTruncated = stderr.IsTruncated,
            Stdout = stdout.Text(),
            Stderr = stderr.Text(),
        };
    }

    /// <summary>杀掉整棵进程树（含孙进程）；进程已经退出、或权限不足时都当"已经不在跑"。</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 刚好自己退了 / 杀不动：都不该把结果搞丢，如实返回就行。
        }
    }

    /// <summary>有上限地等进程真的退出，避免 WaitForExit 把调用挂死。</summary>
    private static async Task ReapAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(ReapGraceMs)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 同 KillTree：拿不到就是拿不到。
        }
    }

    /// <summary>有上限地等两路输出读完；读不完就用已经拿到的部分（超时路径下管道可能被孙进程攥着）。</summary>
    private static async Task DrainAsync(params Task[] pumps)
    {
        var all = Task.WhenAll(pumps);
        await Task.WhenAny(all, Task.Delay(DrainGraceMs)).ConfigureAwait(false);
    }

    /// <summary>退出码；进程还没退出（强杀后没回收成功）时返回 -1。</summary>
    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>把一路管道读到 EOF（或读不动为止），封顶部分交给 <see cref="CappedCapture"/>。</summary>
    private static async Task PumpAsync(Stream stream, CappedCapture capture)
    {
        var buffer = new byte[8192];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                capture.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception)
        {
            // 强杀时管道被掐断是超时 / 取消路径的正常现象；已经读到的部分照样给模型看。
        }
    }

    /// <summary>只留前 <c>cap</c> 个字节，但把整条管道继续读干净（否则子进程会卡在写上面）。</summary>
    private sealed class CappedCapture
    {
        private readonly int _cap;
        private readonly MemoryStream _kept;
        private readonly object _gate = new();
        private long _total;

        internal CappedCapture(int cap)
        {
            _cap = cap;
            _kept = new MemoryStream(cap);
        }

        internal bool IsTruncated
        {
            get
            {
                lock (_gate)
                {
                    return _total > _cap;
                }
            }
        }

        internal void Append(ReadOnlySpan<byte> chunk)
        {
            lock (_gate)
            {
                _total += chunk.Length;

                var room = _cap - (int)_kept.Length;
                if (room > 0)
                {
                    _kept.Write(chunk[..Math.Min(room, chunk.Length)]);
                }
            }
        }

        internal string Text()
        {
            lock (_gate)
            {
                var text = Utf8.GetString(_kept.ToArray());

                if (_total > _cap)
                {
                    // 截断点可能正好切在一个多字节汉字中间，尾巴会留一个 U+FFFD：剥掉它再补标记，
                    // 别让模型以为原始输出里就有个怪字符。
                    return text.TrimEnd('\uFFFD') + Environment.NewLine + TruncationMarker + Environment.NewLine;
                }

                return text;
            }
        }
    }
}
