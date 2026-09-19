// JSON 辅助：把 Win32 结构转成输出对象，给命令参数提供带默认值的取值器。
// 使用共享框架自带的 System.Text.Json，因此工程不需要任何 NuGet 依赖。
// 搬运自 dsh_plugins/ctrl-computer/daemon/Json.cs，命名空间改为 PotatoAgent.Win32。

using System.Text.Json;
using System.Text.Json.Nodes;

namespace PotatoAgent.Win32;

internal static class J
{
    internal static JsonObject Rect(RECT rect) => new()
    {
        ["x"] = rect.Left,
        ["y"] = rect.Top,
        ["width"] = rect.Width,
        ["height"] = rect.Height,
    };

    internal static JsonObject Rect(System.Drawing.Rectangle rect) => new()
    {
        ["x"] = rect.X,
        ["y"] = rect.Y,
        ["width"] = rect.Width,
        ["height"] = rect.Height,
    };

    internal static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }
}

/// <summary>命令参数的宽松读取：类型不符时退回默认值，绝不因为一个坏字段崩掉整次调用。</summary>
internal static class Args
{
    internal static string? Text(this JsonObject args, string key) =>
        args[key] is { } node && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    internal static int Int(this JsonObject args, string key, int fallback) =>
        args[key] is { } node && node.GetValueKind() == JsonValueKind.Number ? (int)Math.Round(node.GetValue<double>()) : fallback;

    internal static double Num(this JsonObject args, string key, double fallback) =>
        args[key] is { } node && node.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : fallback;

    internal static bool Flag(this JsonObject args, string key, bool fallback) =>
        args[key] is { } node && node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? node.GetValue<bool>() : fallback;
}
