// file_delete：删进回收站。破坏性，Dangerous。
//
// 铁律：只走回收站。回收站不可用（网络盘 / 可移动盘 / shell 报错 / 路径还在）时，
// 如实报错 —— 绝不退化成永久删除，也绝不假装成功。

using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// <c>file_delete</c>：把文件（或 <c>recursive=true</c> 时的整个目录）送进回收站。
/// </summary>
/// <remarks>
/// 破坏性操作，<see cref="ToolRisk.Dangerous"/>（界面确认框要写清"将要发生什么"）。
/// 实现只调用 shell32 的 <c>SHFileOperationW</c> + <c>FOF_ALLOWUNDO</c>，用户可以从回收站还原；
/// 回收站不可用或调用失败时如实返回错误，<b>绝不退化成永久删除</b>。
/// </remarks>
public sealed class FileDeleteTool : ITool
{
    private readonly Func<string?>? _workspaceRoot;

    /// <summary>构造。</summary>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    public FileDeleteTool(Func<string?>? workspaceRoot = null) => _workspaceRoot = workspaceRoot;

    /// <inheritdoc />
    public string Name => "file_delete";

    /// <inheritdoc />
    public string Description =>
        "Delete a file, or a whole folder when 'recursive' is true. The item is moved to the Recycle Bin, never deleted " +
        "permanently, so the user can restore it. Use this only when the user clearly asked to remove something; deleting a " +
        "folder removes everything inside it, and the call refuses folders unless 'recursive' is true. It also refuses drive " +
        "roots. Returns the resolved path and what was recycled. Cost: the item disappears from its location and stays in the " +
        "Recycle Bin until the user empties it. It only works on local fixed drives that have a Recycle Bin - on other drives, " +
        "and on any failure, the call reports an error instead of deleting permanently.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "File or folder to move to the Recycle Bin. Absolute, or relative to the open workspace."
            },
            "recursive": {
              "type": "boolean",
              "description": "Required to be true when path is a folder (it deletes everything inside). Default false."
            }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Dangerous;

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

            var isFolder = Directory.Exists(full);
            if (!isFolder && !File.Exists(full))
            {
                return Task.FromResult(ToolResult.Error(
                    $"No such file or folder: {full}. It may already be gone - use file_list to check."));
            }

            if (isFolder && !args.Flag("recursive", false))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} is a folder. Deleting it moves the folder and everything inside it to the Recycle Bin. " +
                    "Call file_delete again with recursive=true if that is really what the user wants."));
            }

            if (FilePaths.IsDriveRoot(full))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} is a drive root. file_delete refuses to delete a whole drive. Nothing was deleted."));
            }

            if (!IsLocalFixedDrive(full, out var driveKind))
            {
                return Task.FromResult(ToolResult.Error(
                    $"{full} is on a {driveKind} location, where Windows has no Recycle Bin, and file_delete never deletes " +
                    "permanently. Nothing was deleted. Ask the user to remove it in Explorer if they really want it gone."));
            }

            var size = isFolder ? -1 : new FileInfo(full).Length;
            var code = RecycleBin.Delete(full, out var aborted);
            var gone = !File.Exists(full) && !Directory.Exists(full);

            if (!gone)
            {
                var reason = code == 0 && aborted
                    ? "Windows reported that the operation was cancelled"
                    : RecycleBin.Describe(code);
                return Task.FromResult(ToolResult.Error(
                    $"Could not move {full} to the Recycle Bin: {reason}. Nothing was deleted - the item is still in place, " +
                    "and file_delete will not fall back to a permanent delete."));
            }

            var what = isFolder ? "folder and everything inside it" : $"{FileArgs.N(size)}-byte file";
            var text = new StringBuilder();
            text.Append($"Moved to the Recycle Bin: {full} ({what}). ")
                .Append("The user can restore it from the Recycle Bin until the bin is emptied.");
            if (code != 0)
            {
                text.Append($" (The shell returned {RecycleBin.Describe(code)}, but the path is gone.)");
            }

            return Task.FromResult(ToolResult.Ok(text.ToString()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"file_delete failed: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// 这个位置是不是"本地固定盘"（只有这种盘才一定有回收站）。
    /// 拿不准就返回 false —— 宁可不删，也不冒险退化成永久删除。
    /// </summary>
    /// <param name="fullPath">已解析的绝对路径。</param>
    /// <param name="description">盘的类型（英文小写，用于错误文案）。</param>
    private static bool IsLocalFixedDrive(string fullPath, out string description)
    {
        description = "unknown";

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        if (root.StartsWith(@"\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
        {
            description = "network";
            return false;
        }

        try
        {
            var drive = new DriveInfo(root);
            description = drive.DriveType.ToString().ToLowerInvariant();
            return drive.DriveType == DriveType.Fixed && drive.IsReady;
        }
        catch (Exception)
        {
            description = "unknown";
            return false;
        }
    }
}
