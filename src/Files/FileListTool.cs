// file_list：列目录。只读，Safe。
//
// 约定（给模型看的文本也照这个写）：
//   - 目录在前，再按名字排序（大小写不敏感，Windows 的习惯）
//   - 每行给 名字 / 类型 / 大小 / 修改时间
//   - 被截断时必须明说"还剩几条"，不能让模型以为这就是全部

using System.Globalization;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_list</c>：列出一个目录里有什么（名字 / 类型 / 大小 / 修改时间）。
/// </summary>
/// <remarks>
/// 只读，<see cref="ToolRisk.Safe"/>。返回的顺序是"目录在前，再按名字排序"，
/// 递归列举时名字一列显示相对路径，被截断时文本里会写明还剩多少条没列出。
/// </remarks>
public sealed class FileListTool : ITool
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    /// <summary>扫描上限：超大目录（几十万文件）时不再往下数，如实说"数量超过这个数"。</summary>
    private const int ScanCap = 50_000;

    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileListTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_list";

    /// <inheritdoc />
    public string Description =>
        "List what is inside a folder: for every entry it returns the name, the type (file or folder), the size in bytes " +
        "and the last-modified time, with folders first and then sorted by name. Use this before reading or writing files " +
        "so you know what is really on disk. 'pattern' is an optional glob matched against the file or folder name " +
        "(for example *.cs or report?.md) and only applies to names, not to paths. 'recursive' walks subfolders too. " +
        "At most 'limit' entries are returned (default 200, max 1000); when there are more, the reply says exactly how many " +
        "were left out so you can narrow the pattern or raise the limit. Cost: one directory scan - recursion over a huge tree " +
        "can take a while.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Folder to list. Absolute, or relative to the open workspace. Defaults to \".\" (the workspace root)."
            },
            "pattern": {
              "type": "string",
              "description": "Optional glob matched against file and folder names, e.g. \"*.cs\". Must not contain a path separator. Omit to list everything."
            },
            "recursive": {
              "type": "boolean",
              "description": "true walks subfolders as well. Defaults to false (only the folder itself)."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of entries to return. Default 200, maximum 1000."
            }
          },
          "required": []
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

            if (!FilePaths.TryResolve(args.Text("path") ?? ".", _workspaceRoot, out var folder, out var error))
            {
                return Task.FromResult(ToolResult.Error(error!));
            }

            if (File.Exists(folder))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{folder} is a file, not a folder. Use file_read to read it, or file_list on its parent folder."));
            }

            if (!Directory.Exists(folder))
            {
                return Task.FromResult(ToolResult.Error(
                    $"The folder {folder} does not exist. Use an absolute path, or file_list the workspace root to look around."));
            }

            var pattern = args.Text("pattern");
            if (GlobPattern.HasSeparator(pattern))
            {
                return Task.FromResult(ToolResult.Error(
                    $"The 'pattern' argument matches file and folder names only, so it must not contain a path separator " +
                    $"(got \"{pattern}\"). Put the folder in 'path' and use a plain name glob such as \"*.cs\"."));
            }

            var matcher = GlobPattern.Create(pattern);
            var recursive = args.Flag("recursive", false);
            var limit = args.Clamped("limit", DefaultLimit, 1, MaxLimit);
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var entries = new List<Entry>();
            var capped = false;
            var scanError = (string?)null;
            var scannedEverything = true;

            try
            {
                Collect(folder, directories: true, option, matcher, recursive, entries);
                if (entries.Count >= ScanCap)
                {
                    capped = true;
                    scannedEverything = false;
                }
                else
                {
                    Collect(folder, directories: false, option, matcher, recursive, entries);
                    if (entries.Count >= ScanCap)
                    {
                        capped = true;
                        scannedEverything = false;
                    }
                }
            }
            catch (Exception ex) when (FilePaths.IsBadPath(ex))
            {
                scanError = $"{ex.GetType().Name}: {ex.Message}";
                scannedEverything = false;
            }

            if (entries.Count == 0 && scanError is not null)
            {
                return Task.FromResult(ToolResult.Error($"Could not list {folder}: {scanError}"));
            }

            entries.Sort(Compare);
            var shown = entries.Count <= limit ? entries : entries.GetRange(0, limit);
            var folders = entries.Count(e => e.IsDirectory);
            var files = entries.Count - folders;

            var text = new StringBuilder();
            text.Append("Folder: ").Append(folder).Append('\n');
            text.Append("Filters: pattern ")
                .Append(matcher is null ? "(none)" : $"\"{pattern}\"")
                .Append(", recursive=")
                .Append(recursive ? "true" : "false")
                .Append('\n');

            var matched = capped ? $"{FileArgs.N(ScanCap)}+" : FileArgs.N(entries.Count);
            text.Append($"{FileArgs.N(folders)} folder(s), {FileArgs.N(files)} file(s) - {matched} entries matched.");

            if (entries.Count == 0)
            {
                text.Append(scanError is null
                    ? " The folder is empty.\n"
                    : $" (the scan stopped early: {scanError})\n");
                return Task.FromResult(ToolResult.Ok(text.ToString()));
            }

            var remaining = entries.Count - shown.Count;
            if (remaining > 0)
            {
                text.Append($" TRUNCATED: only the first {FileArgs.N(shown.Count)} are listed, {FileArgs.N(remaining)} more " +
                            $"are not shown. Raise 'limit' (max {FileArgs.N(MaxLimit)}) or narrow 'pattern'.\n");
            }
            else if (capped)
            {
                text.Append($" TRUNCATED: counting stopped after {FileArgs.N(ScanCap)} entries, there may be more. " +
                            "Narrow 'pattern' or turn 'recursive' off.\n");
            }
            else
            {
                text.Append(" Showing all of them.\n");
            }

            if (scanError is not null)
            {
                text.Append($"Note: part of the tree could not be read ({scanError}); the list above is incomplete.\n");
            }

            text.Append('\n');
            foreach (var entry in shown)
            {
                var kind = entry.IsDirectory ? "[dir ]" : "[file]";
                var size = entry.IsDirectory ? "-" : entry.Size < 0 ? "?" : FileArgs.N(entry.Size);
                var when = entry.Modified == default
                    ? "????-??-?? ??:??"
                    : entry.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                text.Append(kind).Append(' ').Append(size.PadLeft(14)).Append("  ").Append(when).Append("  ")
                    .Append(entry.Label).Append('\n');
            }

            if (!scannedEverything)
            {
                text.Append("(the scan was cut short - see the notes above)\n");
            }

            return Task.FromResult(ToolResult.Ok(text.ToString()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"file_list failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>收集一层（或整棵树）的条目；调用方负责兜异常。</summary>
    private static void Collect(
        string folder, bool directories, SearchOption option, Func<string, bool>? matcher, bool recursive, List<Entry> into)
    {
        var source = directories
            ? Directory.EnumerateDirectories(folder, "*", option)
            : Directory.EnumerateFiles(folder, "*", option);

        foreach (var path in source)
        {
            var name = Path.GetFileName(path);
            if (matcher is not null && !matcher(name))
            {
                continue;
            }

            into.Add(Entry.Create(path, folder, recursive, directories));
            if (into.Count >= ScanCap)
            {
                return;
            }
        }
    }

    /// <summary>目录在前，再按名字排序（Windows 习惯：大小写不敏感）。</summary>
    private static int Compare(Entry a, Entry b)
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一行目录项。</summary>
    private readonly record struct Entry(string Label, bool IsDirectory, long Size, DateTime Modified)
    {
        internal static Entry Create(string fullPath, string folder, bool recursive, bool isDirectory)
        {
            var name = Path.GetFileName(fullPath);
            var label = recursive ? Path.GetRelativePath(folder, fullPath) : name;

            if (isDirectory)
            {
                DateTime stamp;
                try
                {
                    stamp = Directory.GetLastWriteTime(fullPath);
                }
                catch (Exception ex) when (FilePaths.IsBadPath(ex))
                {
                    stamp = default;
                }

                return new Entry(label, true, -1, stamp);
            }

            try
            {
                var info = new FileInfo(fullPath);
                return new Entry(label, false, info.Length, info.LastWriteTime);
            }
            catch (Exception ex) when (FilePaths.IsBadPath(ex))
            {
                return new Entry(label, false, -1, default);
            }
        }
    }
}
