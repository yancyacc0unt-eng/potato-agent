// 六个文件工具共用的路径解析 —— 只此一处，别的工具不许自己拼路径。
//
// 规则：
//   绝对路径            -> 原样使用（只做 GetFullPath + 去掉尾分隔符的规范化）
//   相对路径 + 有工作区  -> 拼到工作区根目录下
//   相对路径 + 没工作区  -> 报英文人话错误，绝不偷偷拿进程当前目录去猜
//
// 刻意【不做】"越界禁止"式的路径黑名单：agent 本来就能操作整台电脑，
// 护栏靠 ToolRisk + 界面确认框，而不是靠这里拦路径（拦了只会让模型一头雾水地重试）。

using System.Security;

namespace PotatoAgent.Files;

/// <summary>
/// 文件工具共用的路径解析器：把模型给的 <c>path</c> 变成"确定的绝对路径"。
/// </summary>
/// <remarks>
/// 用法固定是 <see cref="TryResolve"/>：成功拿到绝对路径，失败拿到一句给模型看的英文人话。
/// 结果里回报的路径一律是解析后的绝对路径 —— 用户要看得到自己批的到底是哪个文件。
/// </remarks>
public static class FilePaths
{
    /// <summary>
    /// 解析一个路径。成功时 <paramref name="fullPath"/> 是规范化后的绝对路径（无尾分隔符）；
    /// 失败时返回 false，<paramref name="error"/> 是一句可以直接回灌给模型的原因说明。
    /// </summary>
    /// <param name="path">模型给的路径，可以是绝对路径，也可以是相对工作区根的相对路径。</param>
    /// <param name="workspaceRoot">工作区回调：返回当前工作区根目录，没有工作区时返回 null。</param>
    /// <param name="fullPath">解析结果（绝对路径）。失败时为空串。</param>
    /// <param name="error">失败原因（英文）。成功时为 null。</param>
    public static bool TryResolve(string? path, Func<string?>? workspaceRoot, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "The path is empty. Pass an absolute path (for example C:\\Users\\you\\notes.txt), " +
                    "or a path relative to the open workspace.";
            return false;
        }

        var raw = path.Trim();

        try
        {
            if (Path.IsPathRooted(raw))
            {
                fullPath = Normalize(raw);
                return true;
            }

            var root = WorkspaceRoot(workspaceRoot);
            if (root is null)
            {
                error = "Relative paths need an open workspace, and no workspace is open right now. " +
                        "Either ask the user to open a folder as the workspace, or pass a full path " +
                        $"(for example C:\\Users\\you\\notes.txt). The path was \"{raw}\".";
                return false;
            }

            fullPath = Normalize(Path.Combine(root, raw));
            return true;
        }
        catch (Exception ex) when (IsBadPath(ex))
        {
            error = $"\"{raw}\" is not a usable path: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 规范化：转成绝对路径并去掉尾部分隔符（<c>C:\a\b\</c> 与 <c>C:\a\b</c> 视为同一个）。
    /// 盘根（<c>C:\</c>）不受影响，<see cref="Path.TrimEndingDirectorySeparator"/> 不会把根削成 <c>C:</c>。
    /// </summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// 取当前工作区根目录（规范化后的绝对路径）；没有工作区、或回调本身出错，一律返回 null。
    /// </summary>
    /// <remarks>回调是界面层挂上来的，它抛异常不该把文件工具带崩 —— 当作"没有工作区"处理。</remarks>
    public static string? WorkspaceRoot(Func<string?>? workspaceRoot)
    {
        string? root;
        try
        {
            root = workspaceRoot?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return Normalize(root!);
        }
        catch (Exception ex) when (IsBadPath(ex))
        {
            return null;
        }
    }

    /// <summary>这个路径是不是盘根（<c>C:\</c> / <c>\\server\share\</c>）—— 删盘根是灾难，工具会拿它挡一道。</summary>
    public static bool IsDriveRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(root),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>路径相关的可预期异常（拼错了、太长、非法字符、没权限）。别的异常照旧上抛。</summary>
    internal static bool IsBadPath(Exception ex) =>
        ex is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException or SecurityException;
}
