// 工具层的公共小助手：从模型给的 JsonElement 里"宽松地"取参数，以及把 hwnd / 窗口标题解析成真实句柄。
//
// 宽松是刻意的：模型的参数永远可能缺字段、类型写错、给个 null。
// 这里一律退回默认值或返回一句人话错误，绝不因为一个坏字段抛 NullReference 把整次调用搞崩。

using System.Globalization;
using System.Text.Json;
using PotatoAgent.Win32;

namespace PotatoAgent.Win32.Tools;

/// <summary>从 <see cref="JsonElement"/> 里安全取参数。</summary>
internal static class ToolArgs
{
    /// <summary>取字符串；不是字符串或字段不存在返回 null。</summary>
    internal static string? Text(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>取数字（整数或小数都认）；不是数字返回 null。</summary>
    internal static double? Number(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;

    /// <summary>取整数；不是数字返回 null。</summary>
    internal static int? Integer(this JsonElement args, string name) =>
        args.Number(name) is { } number ? (int)Math.Round(number) : null;

    /// <summary>取布尔；不是布尔返回 <paramref name="fallback"/>。</summary>
    internal static bool Flag(this JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    /// <summary>取子对象；不是对象返回 null。</summary>
    internal static JsonElement? Object(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>一句人话的错误：这个参数是必填的。</summary>
    internal static string Required(string name) => $"The '{name}' argument is required.";
}

/// <summary>把模型给的窗口标识解析成真实 HWND。</summary>
internal static class ToolWindow
{
    /// <summary>
    /// 按 <c>hwnd</c>（"0x00012345" 或十进制）或 <c>window</c>（标题片段，大小写不敏感）找窗口；
    /// 两个都没给就用当前前台窗口。找不到时 <paramref name="error"/> 是一句人话。
    /// </summary>
    internal static bool TryResolve(JsonElement args, out IntPtr hwnd, out string? error)
    {
        error = null;

        var raw = args.Text("hwnd");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!TryParseHandle(raw!, out hwnd))
            {
                error = $"'{raw}' is not a valid window handle (use the hwnd field from pc_windows, e.g. \"0x00123456\").";
                return false;
            }

            if (!Native.IsWindow(hwnd))
            {
                error = $"the window {raw} no longer exists.";
                return false;
            }

            return true;
        }

        var title = args.Text("window");
        if (!string.IsNullOrWhiteSpace(title))
        {
            hwnd = Desktop.FindByTitle(title!);
            if (hwnd == IntPtr.Zero)
            {
                error = $"no visible top-level window has a title containing \"{title}\". Call pc_windows to list them.";
                return false;
            }

            return true;
        }

        hwnd = Desktop.Foreground();
        if (hwnd == IntPtr.Zero)
        {
            error = "there is no foreground window right now (the desktop may be locked). Nothing was done.";
            return false;
        }

        return true;
    }

    /// <summary>允许 "0x1A2B" 与十进制两种写法。</summary>
    internal static bool TryParseHandle(string text, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        var trimmed = text.Trim();

        long value;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
        }
        else if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        hwnd = new IntPtr(value);
        return hwnd != IntPtr.Zero;
    }
}
