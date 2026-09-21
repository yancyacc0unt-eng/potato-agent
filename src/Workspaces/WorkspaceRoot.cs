namespace PotatoAgent.Workspaces;

/// <summary>
/// 工作区根定位：从某个目录向上找最近的 <c>.potato\</c> 标记。
/// </summary>
/// <remarks>
/// <para><b>什么时候用</b>：需要在工具执行 / 会话启动时判断"我现在处在哪个工作区里"。
/// 向上找标记比让用户每次手选文件夹省事，也和很多开发工具的 <c>.git</c> 定位方式一致。</para>
/// <para><b>它不读任何文件内容</b>，只做路径运算 + "这个目录下面有没有 <c>.potato</c> 子目录"的判断，
/// 所以很快、也不需要权限。</para>
/// </remarks>
public static class WorkspaceRoot
{
    /// <summary>
    /// 标记目录名：<c>.potato</c>。某个目录下面存在这个子目录，它就是这个工作区的根。
    /// </summary>
    public const string MarkerDirectoryName = ".potato";

    /// <summary>
    /// 从 <paramref name="startDirectory"/>（含它自己）开始向上逐级查找最近的工作区根。
    /// </summary>
    /// <param name="startDirectory">起点目录；带不带尾部分隔符都行，相对路径会先转成绝对路径。</param>
    /// <returns>
    /// 第一个"下面有 <c>.potato\</c>"的目录（已规范化、无尾分隔符）；一路找到盘根都没有就返回 <c>null</c>。
    /// 起点是空串 / 非法路径时同样返回 <c>null</c>，<b>不抛异常</b>。
    /// </returns>
    /// <remarks>起点目录不存在也照样往上找（纯路径运算），方便"准备在还没建的目录里开工作区"这种场景。</remarks>
    public static string? Find(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        string current;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }

        while (true)
        {
            if (Directory.Exists(Path.Combine(current, MarkerDirectoryName)))
            {
                return current;
            }

            // GetDirectoryName 走到 "C:\" 时返回 null；再用相等判断兜一层，防止死循环。
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }
    }

    /// <summary>
    /// 在 <paramref name="directoryPath"/> 下面建好 <c>.potato\</c> 标记目录，并返回它的完整路径。
    /// </summary>
    /// <param name="directoryPath">要标记成工作区根的目录；不存在会被一起创建（含中间层）。</param>
    /// <returns><c>&lt;directoryPath&gt;\.potato</c> 的完整路径（已规范化、无尾分隔符）。</returns>
    /// <remarks>
    /// <b>幂等</b>：已经存在就原样不动、直接返回，调用两次结果一样，可以放心在"打开工作区"时无脑调。
    /// 真建不出来（无权限等）会抛 <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>，
    /// 由调用方决定怎么提示用户 —— 这里<b>不</b>吞掉，否则用户会以为标记建好了。
    /// </remarks>
    public static string EnsureMarker(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath.Trim()));
        var marker = Path.Combine(full, MarkerDirectoryName);

        // CreateDirectory 对已存在的目录是空操作，所以"已存在则不动"是它自带的语义。
        Directory.CreateDirectory(marker);
        return marker;
    }
}
