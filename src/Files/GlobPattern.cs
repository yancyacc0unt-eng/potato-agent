// file_list 的 pattern 参数：一个只匹配"名字"的小 glob（* 和 ?）。
//
// 为什么不用 Directory.EnumerateFiles 自带的 searchPattern：
//   Win32 的 FindFirstFile 有 8.3 短名匹配的老毛病（*.htm 会连 file.html 一起匹配），
//   而且它对目录和文件的处理不一致。自己匹配一次，行为可预期、也好自测。

using System.Text.RegularExpressions;

namespace PotatoAgent.Files;

/// <summary>把 <c>*.cs</c> 这种 glob 编译成"名字 -> 是不是匹配"的判断函数（文件工具内部用）。</summary>
internal static class GlobPattern
{
    /// <summary>
    /// 编译 glob。<paramref name="pattern"/> 为空 / <c>*</c> 时返回 null，表示"不过滤，全都要"。
    /// 只匹配名字（文件名或目录名），<c>*</c> 不跨路径分隔符。
    /// </summary>
    internal static Func<string, bool>? Create(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*")
        {
            return null;
        }

        var regex = new Regex(
            "^" + Translate(pattern.Trim()) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        return name => regex.IsMatch(name);
    }

    /// <summary>pattern 里带了路径分隔符（例如 <c>src\*.cs</c>）—— 名字匹配用不上，工具会直接报错说明。</summary>
    internal static bool HasSeparator(string? pattern) =>
        !string.IsNullOrEmpty(pattern) && (pattern.Contains('\\') || pattern.Contains('/'));

    /// <summary>把 glob 翻译成正则：先整体转义，再把转义后的 <c>\*</c> / <c>\?</c> 换回通配符。</summary>
    private static string Translate(string glob) =>
        Regex.Escape(glob)
            .Replace(@"\*", @"[^\\/]*", StringComparison.Ordinal)
            .Replace(@"\?", @"[^\\/]", StringComparison.Ordinal);
}
