using System.Text.RegularExpressions;

namespace PotatoAgent.Sessions;

/// <summary>会话标题的小工具（纯函数，好测）。</summary>
/// <remarks>
/// 新建会话时还没有标题，用首条用户消息现推一个，让左侧列表一眼能认出来。
/// 它只做字符串整理，不碰存储、不依赖界面。
/// </remarks>
public static class SessionTitle
{
    /// <summary>没有可用文本时的兜底标题（也是 <see cref="ISessionStore.Create"/> 的默认标题）。</summary>
    public const string DefaultTitle = "New chat";

    /// <summary>标题正文最多保留的字符数；超出部分截掉并在末尾补一个省略号。</summary>
    public const int MaxLength = 30;

    /// <summary>连续空白（含换行 / 制表 / 全角空格）压缩成一个普通空格。</summary>
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>从首条用户消息的正文推一个会话标题。</summary>
    /// <remarks>
    /// 规则：换行与连续空白压成单个空格 → 去首尾空白 → 超过 <see cref="MaxLength"/> 个字符就截断并补 "…"。
    /// </remarks>
    /// <param name="text">首条用户消息的文本；null / 空白 / 压平后为空都返回 <see cref="DefaultTitle"/>。</param>
    /// <returns>可以直接显示的标题，绝不为 null、绝不为空、绝不含换行。</returns>
    public static string FromFirstMessage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultTitle;
        }

        var flat = WhitespaceRun.Replace(text, " ").Trim();
        if (flat.Length == 0)
        {
            return DefaultTitle;
        }

        if (flat.Length <= MaxLength)
        {
            return flat;
        }

        var cut = MaxLength;
        if (char.IsHighSurrogate(flat[cut - 1]))
        {
            // 别把代理对（emoji / 少数字）劈成半个字符，那种字符串会污染列表和日志。
            cut--;
        }

        return flat[..cut] + "…";
    }
}
