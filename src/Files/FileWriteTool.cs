// file_write：写 / 覆盖 / 追加。有副作用，Confirm。
//
// 两条容易踩的细节：
//   1) 覆盖已存在的文件时【保留它原来的 UTF-8 BOM】；新建文件一律 UTF-8 无 BOM
//   2) 内容原样落盘，不做任何换行翻译（模型给 \n 就写 \n，给 \r\n 就写 \r\n）

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_write</c>：新建 / 覆盖 / 追加一个文本文件。
/// </summary>
/// <remarks>
/// 有副作用，<see cref="ToolRisk.Confirm"/>（界面弹确认）。内容按 UTF-8 原样写入，不做换行翻译；
/// 覆盖已存在的文件时保留原有 BOM，新文件不带 BOM。返回写入字节数与写完后的文件大小。
/// </remarks>
public sealed class FileWriteTool : ITool
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileWriteTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_write";

    /// <inheritdoc />
    public string Description =>
        "Create, overwrite or append to a text file. Use it to save results, write code, or update notes. The content is " +
        "written exactly as you send it (UTF-8, no newline translation). When an existing file is overwritten its UTF-8 BOM is " +
        "kept; new files are written without a BOM. 'mode' is \"overwrite\" (default) or \"append\". 'create_directories' " +
        "(default true) creates missing parent folders; set it to false to fail instead. The reply states how many bytes were " +
        "written and the resulting file size. Cost: this changes the user's disk and cannot be undone by the tool, so make sure " +
        "the path is the file the user meant.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File to write. Absolute, or relative to the open workspace."
            },
            "content": {
              "type": "string",
              "description": "Exact text to write. May be an empty string (that truncates the file to 0 bytes in overwrite mode)."
            },
            "mode": {
              "type": "string",
              "enum": ["overwrite", "append"],
              "description": "overwrite (default) replaces the whole file; append adds the text at the end."
            },
            "create_directories": {
              "type": "boolean",
              "description": "true (default) creates the parent folders when they are missing; false fails with an error instead."
            }
          },
          "required": ["path", "content"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Confirm;

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

            if (!args.Has("content"))
            {
                return Task.FromResult(ToolResult.Error(FileArgs.Required("content")));
            }

            if (!FilePaths.TryResolve(rawPath, _workspaceRoot, out var full, out var error))
            {
                return Task.FromResult(ToolResult.Error(error!));
            }

            var content = ReadContent(args);
            var mode = (args.Text("mode") ?? "overwrite").ToLowerInvariant();
            var append = mode switch
            {
                "overwrite" or "" => false,
                "append" => true,
                _ => (bool?)null,
            };

            if (append is null)
            {
                return Task.FromResult(ToolResult.Error(
                    $"The 'mode' argument must be \"overwrite\" or \"append\" (got \"{mode}\")."));
            }

            if (Directory.Exists(full))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} is a folder, not a file. Pass a file path, for example {Path.Combine(full, "notes.txt")}."));
            }

            var createDirectories = args.Flag("create_directories", true);
            var parent = Path.GetDirectoryName(full);
            var createdFolders = new List<string>();

            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                if (!createDirectories)
                {
                    return Task.FromResult(ToolResult.Error(
                        $"The parent folder {parent} does not exist. Pass create_directories=true to create it, " +
                        "or write into an existing folder."));
                }

                Directory.CreateDirectory(parent);
                createdFolders.Add(parent);
            }

            var existedBefore = File.Exists(full);
            var keptBom = existedBefore && !append.Value && HasUtf8Bom(full);
            var bytes = Utf8.GetBytes(content);

            using (var stream = new FileStream(
                       full,
                       append.Value ? FileMode.Append : FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                if (keptBom)
                {
                    stream.Write(Bom, 0, Bom.Length);
                }

                stream.Write(bytes, 0, bytes.Length);
            }

            var sizeAfter = new FileInfo(full).Length;
            var text = new StringBuilder();
            text.Append($"Wrote {FileArgs.N(bytes.Length)} bytes to {full} (")
                .Append(append.Value ? "append" : "overwrite")
                .Append(", UTF-8 ")
                .Append(keptBom ? "keeping the existing BOM"
                    : append.Value ? "no extra BOM added"
                    : "without BOM")
                .Append("). The file is now ").Append(FileArgs.N(sizeAfter)).Append(" bytes");
            text.Append(append.Value ? " and already existed.\n" : existedBefore ? "; it existed before and was replaced.\n" : "; it is new.\n");

            if (createdFolders.Count > 0)
            {
                text.Append("Created missing folder(s): ").Append(string.Join(", ", createdFolders)).Append(".\n");
            }

            return Task.FromResult(ToolResult.Ok(text.ToString().TrimEnd('\n')));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"file_write failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// 取 content：字符串原样用；字段缺失/类型不对时也尽量给出可用文本（数字/布尔用字面量，对象用 JSON）。
    /// </summary>
    private static string ReadContent(JsonElement args)
    {
        if (!args.TryGetProperty("content", out var element))
        {
            return string.Empty;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText(),
        };
    }

    /// <summary>文件是不是以 UTF-8 BOM 开头（覆盖时靠它决定要不要保留 BOM）。</summary>
    private static bool HasUtf8Bom(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 3)
            {
                return false;
            }

            var head = new byte[3];
            var read = stream.Read(head, 0, 3);
            return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (Exception ex) when (FilePaths.IsBadPath(ex))
        {
            return false;
        }
    }
}
