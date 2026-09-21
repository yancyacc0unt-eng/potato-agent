// file_read：读文本文件的一段。只读，Safe。
//
// 三条硬要求（都来自任务契约）：
//   1) 前 8 KB 里有 NUL 字节就当二进制【报错】，不吐乱码
//   2) 返回里写清 总字节 / 总行数 / 正在显示第几段，被截断必须明确写出来
//   3) CRLF 与 LF 都要认（StreamReader.ReadLine 两种都当换行，这里再嗅探一下好告诉模型）

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_read</c>：按行读一段文本（带行号），并如实报告总大小、总行数和截断位置。
/// </summary>
/// <remarks>
/// 只读，<see cref="ToolRisk.Safe"/>。二进制文件（前 8 KB 出现 NUL）直接报错，
/// 不会把乱码灌进对话；文件很大时只显示请求的那一段，并在文本里写明"还剩多少行、从哪里接着读"。
/// </remarks>
public sealed class FileReadTool : ITool
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 2000;
    private const int DefaultMaxBytes = 200_000;
    private const int MaxBytesCap = 1_000_000;

    /// <summary>二进制嗅探窗口：只看前 8 KB。</summary>
    private const int SniffBytes = 8192;

    /// <summary>单行最长显示的字符数（防止一个几十 MB 的单行文件把返回撑爆）。</summary>
    private const int MaxLineChars = 200_000;

    /// <summary>超过这个大小就不为了"总行数"再扫一遍全文，如实说"数不清了"。</summary>
    private const long StopCountingBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileReadTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_read";

    /// <inheritdoc />
    public string Description =>
        "Read a text file as UTF-8 and return it with 1-based line numbers. Use this before editing a file, or whenever you " +
        "need the exact contents. Only the slice you ask for comes back: 'offset' is the first line (default 1), 'limit' the " +
        "maximum number of lines (default 500, max 2000) and 'max_bytes' the byte budget for the text returned (default " +
        "200000, max 1000000). The reply always states the total size in bytes, the total number of lines and exactly which " +
        "lines are shown; when the file is cut short the reply says so and tells you the offset to continue from. Files with a " +
        "NUL byte in the first 8 KB are treated as binary and refused instead of printing garbage. Both CRLF and LF files work. " +
        "Cost: one file read - cheap, but do not read a whole huge log at once, read it in slices.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File to read. Absolute, or relative to the open workspace."
            },
            "offset": {
              "type": "integer",
              "description": "First line to show, 1-based. Default 1."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of lines to show. Default 500, maximum 2000."
            },
            "max_bytes": {
              "type": "integer",
              "description": "Maximum number of bytes of text to return. Default 200000, maximum 1000000."
            }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Safe;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var rawPath = args.Text("path");
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return Task.FromResult(ToolResult.Error(FileArgs.Required("path")));
            }

            if (!FilePaths.TryResolve(rawPath, _workspaceRoot, out var full, out var error))
            {
                return Task.FromResult(ToolResult.Error(error!));
            }

            if (Directory.Exists(full))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} is a folder, not a file. Use file_list to see what is inside it."));
            }

            if (!File.Exists(full))
            {
                return Task.FromResult(ToolResult.Error(
                    $"No such file: {full}. Use file_list on its folder to check the name."));
            }

            var info = new FileInfo(full);
            var size = info.Length;

            var sniff = Sniff(full, SniffBytes);
            var nul = Array.IndexOf(sniff, (byte)0);
            if (nul >= 0)
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} looks like a binary file: there is a NUL byte at offset {FileArgs.N(nul)} within the first " +
                    $"{FileArgs.N(sniff.Length)} bytes. file_read only reads text files (.txt, .cs, .md, .json, ...); " +
                    "it will not print binary data."));
            }

            var hasBom = sniff.Length >= 3 && sniff[0] == 0xEF && sniff[1] == 0xBB && sniff[2] == 0xBF;
            var encoding = hasBom ? "UTF-8 with BOM" : "UTF-8 (no BOM found)";
            var endings = DescribeEndings(sniff);
            if (size > sniff.Length)
            {
                endings += $" (sniffed from the first {FileArgs.N(sniff.Length)} bytes)";
            }

            var offset = Math.Max(1, args.Integer("offset") ?? 1);
            var limit = args.Clamped("limit", DefaultLimit, 1, MaxLimit);
            var maxBytes = args.Clamped("max_bytes", DefaultMaxBytes, 1, MaxBytesCap);

            var lines = new List<string>(Math.Min(limit, 256));
            long sliceBytes = 0;
            long totalLines = 0;
            var totalKnown = true;
            var lineLimitHit = false;
            var byteLimitHit = false;
            var longLineCut = false;
            var stopCounting = size > StopCountingBytes;

            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true))
            {
                while (true)
                {
                    if ((totalLines & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        break;
                    }

                    totalLines++;

                    if (totalLines < offset)
                    {
                        continue;
                    }

                    if (lineLimitHit || byteLimitHit)
                    {
                        if (stopCounting)
                        {
                            // 几十 MB 以上的文件不值得为了"总行数"再扫一遍，如实说数不清了。
                            totalKnown = false;
                            break;
                        }

                        continue;
                    }

                    if (lines.Count >= limit)
                    {
                        lineLimitHit = true;
                        continue;
                    }

                    var cost = (long)Utf8.GetByteCount(line) + 1;
                    if (lines.Count > 0 && sliceBytes + cost > maxBytes)
                    {
                        byteLimitHit = true;
                        continue;
                    }

                    if (line.Length > MaxLineChars)
                    {
                        // 单个超长行（压缩过的 JSON、base64 之类）只显示开头，别把返回撑爆。
                        lines.Add(line[..MaxLineChars]);
                        sliceBytes += Utf8.GetByteCount(line[..MaxLineChars]) + 1;
                        longLineCut = true;
                    }
                    else
                    {
                        lines.Add(line);
                        sliceBytes += cost;
                    }
                }
            }

            return Task.FromResult(ToolResult.Ok(Compose(full, size, totalLines, totalKnown, encoding, endings, offset, limit,
                maxBytes, lines, sliceBytes, lineLimitHit, byteLimitHit, longLineCut)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"file_read failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>拼给模型看的正文：先报账（大小/行数/编码/当前段），再给带行号的内容。</summary>
    private static string Compose(
        string full,
        long size,
        long totalLines,
        bool totalKnown,
        string encoding,
        string endings,
        int offset,
        int limit,
        int maxBytes,
        List<string> lines,
        long sliceBytes,
        bool lineLimitHit,
        bool byteLimitHit,
        bool longLineCut)
    {
        var text = new StringBuilder();
        text.Append(full).Append('\n');
        text.Append($"{FileArgs.N(size)} bytes, ")
            .Append(totalKnown ? $"{FileArgs.N(totalLines)} line(s)" : $"more than {FileArgs.N(totalLines)} line(s)")
            .Append(". Encoding: ").Append(encoding)
            .Append(". Line endings: ").Append(endings)
            .Append(".\n");

        if (size == 0)
        {
            text.Append("The file is empty (0 bytes).");
            return text.ToString();
        }

        if (lines.Count == 0)
        {
            text.Append($"Nothing to show: offset {FileArgs.N(offset)} is past the end of the file, which has ")
                .Append(totalKnown ? $"{FileArgs.N(totalLines)} line(s)" : $"more than {FileArgs.N(totalLines)} line(s)")
                .Append(". Use offset=1 to start from the top.");
            return text.ToString();
        }

        var first = Math.Max(1, offset);
        var last = first + lines.Count - 1;
        var remaining = totalKnown ? Math.Max(0, totalLines - last) : -1;

        text.Append($"Showing lines {FileArgs.N(first)}-{FileArgs.N(last)} of ")
            .Append(totalKnown ? FileArgs.N(totalLines) : $"{FileArgs.N(totalLines)}+")
            .Append($" ({FileArgs.N(sliceBytes)} bytes of text). ");

        if (byteLimitHit)
        {
            text.Append($"TRUNCATED by 'max_bytes' (budget {FileArgs.N(maxBytes)} bytes): the next line does not fit");
        }
        else if (lineLimitHit)
        {
            text.Append($"TRUNCATED by 'limit' ({FileArgs.N(limit)} lines)");
        }
        else if (longLineCut)
        {
            text.Append("NOTE: at least one line is longer than " + FileArgs.N(MaxLineChars) + " characters and is shown cut off");
        }
        else
        {
            text.Append("This is the end of the file");
        }

        if (byteLimitHit || lineLimitHit)
        {
            text.Append(remaining >= 0
                ? $"; {FileArgs.N(remaining)} more line(s) are not shown. "
                : "; more lines follow (the total line count is unknown for a file this large). ");
            text.Append($"Call file_read again with offset={FileArgs.N(last + 1)} to continue.");
        }
        else
        {
            text.Append('.');
        }

        if (offset > 1)
        {
            text.Append($" (lines 1-{FileArgs.N(first - 1)} are not shown because offset={FileArgs.N(offset)}.)");
        }

        text.Append("\n\n");

        var width = Math.Max(4, last.ToString("D").Length);
        for (var i = 0; i < lines.Count; i++)
        {
            text.Append((first + i).ToString().PadLeft(width)).Append("| ").Append(lines[i]).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>读出前 <paramref name="count"/> 个字节（文件更短就有多少读多少）。</summary>
    private static byte[] Sniff(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, count);
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = stream.Read(buffer, read, length - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return read == length ? buffer : buffer[..read];
    }

    /// <summary>从嗅探到的字节里判断换行风格（CRLF / LF / 混合 / 没有换行）。</summary>
    private static string DescribeEndings(byte[] sniff)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;

        for (var i = 0; i < sniff.Length; i++)
        {
            if (sniff[i] == (byte)'\r')
            {
                if (i + 1 < sniff.Length && sniff[i + 1] == (byte)'\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (sniff[i] == (byte)'\n')
            {
                lf++;
            }
        }

        if (crlf > 0 && lf > 0)
        {
            return "mixed CRLF and LF";
        }

        if (crlf > 0)
        {
            return "CRLF";
        }

        if (lf > 0)
        {
            return "LF";
        }

        return cr > 0 ? "CR only" : "none found";
    }
}
