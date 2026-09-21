// file_move：移动 / 改名。有副作用，Confirm。
//
// 和 file_copy 的两处差别：
//   1) 缺父目录就【建】（契约明确要求；copy 不建）
//   2) 结果里新旧绝对路径都要回报 —— 用户要能看出文件从哪搬到了哪

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_move</c>：把一个文件移动（或改名）到别处，原路径不再存在。
/// </summary>
/// <remarks>
/// 有副作用，<see cref="ToolRisk.Confirm"/>。目标缺父目录时自动创建；
/// 目标是已存在的目录时移动进去并沿用原名；目标文件已存在且 <c>overwrite=false</c> 时失败。
/// 目录的移动本轮不支持，会明确报错。
/// </remarks>
public sealed class FileMoveTool : ITool
{
    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileMoveTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_move";

    /// <inheritdoc />
    public string Description =>
        "Move (or rename) one file. The old path does not exist afterwards, so only use it when a move is what the user " +
        "asked for; use file_copy when the original must stay. Missing parent folders of the destination are created " +
        "automatically. If 'destination' is an existing folder the file is moved into it under its original name. An existing " +
        "file at the destination is only replaced when 'overwrite' is true; with the default false the call fails and nothing " +
        "is changed. Moving folders is not supported yet. Returns both the old and the new absolute path. " +
        "Cost: the file leaves its old location.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "source": {
              "type": "string",
              "description": "File to move. Absolute, or relative to the open workspace."
            },
            "destination": {
              "type": "string",
              "description": "Folder to move into (the original name is kept), or the full new path of the file."
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
                    $"{source} is a folder. Moving folders is not supported yet - move the files inside it one by one " +
                    "with file_move."));
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
            }

            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ToolResult.Error(
                    $"The source and the destination are the same file ({source}). Nothing was moved."));
            }

            var overwrite = args.Flag("overwrite", false);
            var existed = File.Exists(target);
            if (existed && !overwrite)
            {
                return Task.FromResult(ToolResult.Error(
                    $"{target} already exists. Pass overwrite=true to replace it, or choose another destination. " +
                    "Nothing was moved."));
            }

            var createdFolders = new List<string>();
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                createdFolders.Add(parent);
            }

            var size = new FileInfo(source).Length;
            File.Move(source, target, overwrite);

            var text = new StringBuilder();
            text.Append($"Moved {source} -> {target} ({FileArgs.N(size)} bytes)")
                .Append(intoFolder ? ", into the existing folder" : string.Empty)
                .Append(existed ? "; the destination file was replaced." : ".")
                .Append('\n');

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
            return Task.FromResult(ToolResult.Error($"file_move failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
