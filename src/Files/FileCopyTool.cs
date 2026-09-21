// file_copy：复制单个文件。有副作用，Confirm。
//
// 本轮刻意【不做】目录树拷贝：目标里已经有同名目录时，直接给一句明确错误，
// 而不是装成"复制成功了"（半个目录树的后果比报错难查得多）。

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_copy</c>：把一个文件复制到另一个位置（原文件保留）。
/// </summary>
/// <remarks>
/// 有副作用，<see cref="ToolRisk.Confirm"/>。目标是已存在的目录时，拷进去沿用原来的文件名；
/// 目录树拷贝本轮不支持，会明确报错。目标文件已存在且 <c>overwrite=false</c> 时失败，不做任何改动。
/// </remarks>
public sealed class FileCopyTool : ITool
{
    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileCopyTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_copy";

    /// <inheritdoc />
    public string Description =>
        "Copy one file to another place, leaving the original in place. If 'destination' is an existing folder the file is " +
        "copied into it under its original name; otherwise 'destination' is the full new file path and its parent folder must " +
        "already exist (file_copy does not create folders). An existing file is only replaced when 'overwrite' is true; with " +
        "the default false the call fails and nothing is changed. Copying whole folder trees is not supported yet - the call " +
        "fails with a clear message instead of copying half a tree. Returns the resolved source and destination paths and the " +
        "size of the file. Cost: writes a new file on disk.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "source": {
              "type": "string",
              "description": "File to copy. Absolute, or relative to the open workspace."
            },
            "destination": {
              "type": "string",
              "description": "Folder to copy into (the original name is kept), or the full path of the new file."
            },
            "overwrite": {
              "type": "boolean",
              "description": "true replaces an existing destination file. Default false: the call fails instead."
            }
          },
          "required": ["source", "destination"]
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

            var sourceRaw = args.Text("source");
            var destRaw = args.Text("destination");
            if (string.IsNullOrWhiteSpace(sourceRaw))
            {
                return Task.FromResult(ToolResult.Error(FileArgs.Required("source")));
            }

            if (string.IsNullOrWhiteSpace(destRaw))
            {
                return Task.FromResult(ToolResult.Error(FileArgs.Required("destination")));
            }

            if (!FilePaths.TryResolve(sourceRaw, _workspaceRoot, out var source, out var error))
            {
                return Task.FromResult(ToolResult.Error(error!));
            }

            if (!FilePaths.TryResolve(destRaw, _workspaceRoot, out var destination, out error))
            {
                return Task.FromResult(ToolResult.Error(error!));
            }

            if (Directory.Exists(source))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{source} is a folder. Copying folder trees is not supported yet - copy the files inside it one by one " +
                    "with file_copy, or ask the user to do it in Explorer."));
            }

            if (!File.Exists(source))
            {
                return Task.FromResult(ToolResult.Error(
                    $"No such file: {source}. Use file_list on its folder to check the name."));
            }

            string target;
            var intoFolder = false;
            if (Directory.Exists(destination))
            {
                target = Path.Combine(destination, Path.GetFileName(source));
                intoFolder = true;
            }
            else
            {
                target = destination;
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    return Task.FromResult(ToolResult.Error(
                        $"The destination folder {parent} does not exist, and file_copy does not create folders. " +
                        "Pass an existing folder as the destination."));
                }
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ToolResult.Error(
                    $"The source and the destination are the same file ({source}). Nothing was copied."));
            }

            var overwrite = args.Flag("overwrite", false);
            var existed = File.Exists(target);
            if (existed && !overwrite)
            {
                return Task.FromResult(ToolResult.Error(
                    $"{target} already exists. Pass overwrite=true to replace it, or choose another destination. " +
                    "Nothing was copied."));
            }

            File.Copy(source, target, overwrite);
            var size = new FileInfo(target).Length;

            var text = new StringBuilder();
            text.Append($"Copied {source} -> {target} ({FileArgs.N(size)} bytes)")
                .Append(intoFolder ? ", into the existing folder" : string.Empty)
                .Append(existed ? "; the destination file was replaced." : "; the original is untouched.")
                .Append('\n');
            return Task.FromResult(ToolResult.Ok(text.ToString().TrimEnd('\n')));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"file_copy failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
