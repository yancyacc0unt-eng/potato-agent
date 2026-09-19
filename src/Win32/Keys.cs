// SendKeys 的按键表达式解析（不是 System.Windows.Forms.SendKeys —— 那个走的是
// keybd_event 老路径且对中文无能为力，这里全部自己算成 SendInput 事件）。
//
// 支持的写法：
//   ^a            Ctrl+A
//   +{TAB}        Shift+Tab
//   ^+{ESC}       Ctrl+Shift+Esc
//   CTRL+SHIFT+A  文本形式的修饰键（等价于 ^+a）
//   %{F4}         Alt+F4
//   #d            Win+D
//   {ENTER} {TAB} {ESC} {BACKSPACE} {DELETE} {HOME} {END} {PGUP} {PGDN}
//   {UP} {DOWN} {LEFT} {RIGHT} {F1}..{F24} {SPACE} {INS} {CAPSLOCK} {PRINTSCREEN} {VOLUMEUP} ...
//   ~             Enter
//   {+} {^} {%} {~} {(} {)} {{} {}}   转义出字面量
//   其余单字符     按当前键盘布局用 VkKeyScan 算出 VK 与需要的 Shift/Ctrl/Alt 状态
//   布局里没有的字符（例如中文）→ 自动降级成 KEYEVENTF_UNICODE 直接投递码元

using System.Runtime.InteropServices;

namespace PotatoAgent.Win32;

/// <summary>一个按键动作：可选修饰键 + 一个 VK（或一个直接用 Unicode 投递的字符）。</summary>
internal readonly record struct KeyStroke(ushort Vk, bool Ctrl, bool Shift, bool Alt, bool Win, char Literal)
{
    /// <summary>true 表示这个字符在当前键盘布局里没有对应按键，只能按 Unicode 码元打出去。</summary>
    internal bool IsLiteral => Literal != '\0';
}

internal static class KeyParser
{
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12;
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_DELETE = 0x2E;
    internal const ushort VK_SPACE = 0x20;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BACKSPACE"] = 0x08, ["BKSP"] = 0x08, ["BS"] = 0x08,
        ["TAB"] = 0x09,
        ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
        ["SHIFT"] = 0x10, ["CTRL"] = 0x11, ["CONTROL"] = 0x11, ["ALT"] = 0x12, ["MENU"] = 0x12,
        ["PAUSE"] = 0x13, ["BREAK"] = 0x13,
        ["CAPSLOCK"] = 0x14,
        ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20,
        ["PGUP"] = 0x21, ["PAGEUP"] = 0x21, ["PRIOR"] = 0x21,
        ["PGDN"] = 0x22, ["PAGEDOWN"] = 0x22, ["NEXT"] = 0x22,
        ["END"] = 0x23, ["HOME"] = 0x24,
        ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
        ["PRINTSCREEN"] = 0x2C, ["SNAPSHOT"] = 0x2C,
        ["INS"] = 0x2D, ["INSERT"] = 0x2D,
        ["DEL"] = 0x2E, ["DELETE"] = 0x2E,
        ["LWIN"] = 0x5B, ["RWIN"] = 0x5C, ["WIN"] = 0x5B, ["APPS"] = 0x5D,
        ["NUMLOCK"] = 0x90, ["SCROLLLOCK"] = 0x91,
        ["VOLUMEMUTE"] = 0xAD, ["VOLUMEDOWN"] = 0xAE, ["VOLUMEUP"] = 0xAF,
        ["MEDIANEXTTRACK"] = 0xB0, ["MEDIAPREVTRACK"] = 0xB1, ["MEDIAPLAYPAUSE"] = 0xB3,
    };

    /// <summary>这些 VK 是"扩展键"，不带上 EXTENDEDKEY 标志会被当成小键盘上的那颗。</summary>
    internal static bool IsExtended(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp PgDn End Home 方向键
        0x2C or 0x2D or 0x2E => true,                                          // PrintScreen Insert Delete
        0x5B or 0x5C or 0x5D => true,                                          // 左Win 右Win 菜单键
        0x6F or 0x90 or 0xA3 or 0xA5 => true,                                  // 小键盘/ NumLock / 右Ctrl / 右Alt
        _ => false,
    };

    /// <summary>把表达式解析成一串按键动作。写法不认识时抛 ArgumentException（由调用方转成失败结果）。</summary>
    internal static List<KeyStroke> Parse(string keys)
    {
        if (string.IsNullOrEmpty(keys)) throw new ArgumentException("keys is empty; nothing to send");

        var strokes = new List<KeyStroke>();
        int index = 0;

        while (index < keys.Length)
        {
            bool ctrl = false, shift = false, alt = false, win = false;

            // ---- 前缀修饰键，可以叠加任意多个 ----
            while (index < keys.Length)
            {
                char current = keys[index];
                if (current == '^') { ctrl = true; index++; continue; }
                if (current == '+') { shift = true; index++; continue; }
                if (current == '%') { alt = true; index++; continue; }
                if (current == '#') { win = true; index++; continue; }
                if (TryWordModifier(keys, ref index, ref ctrl, ref shift, ref alt, ref win)) continue;
                break;
            }

            if (index >= keys.Length)
                throw new ArgumentException($"\"{keys}\" ends with a modifier prefix but no key to press");

            // ---- 真正的那颗键 ----
            if (keys[index] == '{')
            {
                int close = keys.IndexOf('}', index);
                if (close < 0) throw new ArgumentException($"\"{keys}\" has an unclosed '{{'");

                string name = keys.Substring(index + 1, close - index - 1);
                index = close + 1;

                if (name.Length == 0) throw new ArgumentException($"\"{keys}\" contains an empty {{}}");

                if (name.Length == 1 && !char.IsLetterOrDigit(name[0]))
                {
                    // {+} {(} 这类转义：当成字面量字符
                    strokes.Add(Literal(name[0], ctrl, shift, alt, win));
                }
                else if (Named.TryGetValue(name, out ushort named))
                {
                    // {CTRL} 这种"修饰键本身作为按键"的写法，直接按下并抬起该键
                    strokes.Add(new KeyStroke(named, ctrl, shift, alt, win, '\0'));
                }
                else if (name.Length is 2 or 3 && name[0] is 'F' or 'f' && int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 24)
                {
                    strokes.Add(new KeyStroke((ushort)(0x70 + f - 1), ctrl, shift, alt, win, '\0'));
                }
                else
                {
                    throw new ArgumentException($"unknown key name \"{{{name}}}\" in \"{keys}\"");
                }
            }
            else
            {
                char current = keys[index];
                index++;
                if (current == '~') strokes.Add(new KeyStroke(VK_RETURN, ctrl, shift, alt, win, '\0'));
                else strokes.Add(Literal(current, ctrl, shift, alt, win));
            }
        }

        if (strokes.Count == 0) throw new ArgumentException($"\"{keys}\" produced no keystrokes");
        return strokes;
    }

    /// <summary>VkKeyScan 拿不到映射的字符 → 用 Unicode 码元直接投递（中文走这条路）。</summary>
    private static KeyStroke Literal(char character, bool ctrl, bool shift, bool alt, bool win)
    {
        short mapped = NativeAction.VkKeyScan(character);
        if (mapped == -1) return new KeyStroke(0, ctrl, shift, alt, win, character);

        byte state = (byte)((mapped >> 8) & 0xFF);
        return new KeyStroke(
            (ushort)(mapped & 0xFF),
            ctrl || (state & 0x02) != 0,
            shift || (state & 0x01) != 0,
            alt || (state & 0x04) != 0,
            win,
            '\0');
    }

    /// <summary>识别 "CTRL+" "SHIFT+" 这种文本修饰前缀（注意 SHIFT 的缩写就是 '+'，已在上面处理）。</summary>
    private static bool TryWordModifier(string keys, ref int index, ref bool ctrl, ref bool shift, ref bool alt, ref bool win)
    {
        int plus = keys.IndexOf('+', index);
        if (plus < 0) return false;

        string word = keys.Substring(index, plus - index).Trim();
        switch (word.ToUpperInvariant())
        {
            case "CTRL" or "CONTROL": ctrl = true; break;
            case "SHIFT": shift = true; break;
            case "ALT": alt = true; break;
            case "WIN" or "LWIN" or "RWIN": win = true; break;
            default: return false;
        }

        index = plus + 1;
        return true;
    }
}
