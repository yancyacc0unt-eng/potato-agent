// 从模型给的 JsonElement 里"宽松地"取参数 —— 和 Win32 层的 ToolArgs 同一套思路。
//
// 宽松是刻意的：模型的参数永远可能缺字段、类型写错、给个 null。
// 这里一律退回默认值或返回一句人话错误，绝不因为一个坏字段抛 NullReference 把整次调用搞崩。

using System.Globalization;
using System.Text.Json;

namespace PotatoAgent.Files;

/// <summary>从 <see cref="JsonElement"/> 里安全取参数（文件工具内部用）。</summary>
internal static class FileArgs
{
    /// <summary>取字符串（会去掉首尾空白）；不是字符串或字段不存在返回 null。</summary>
    internal static string? Text(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    /// <summary>取整数；不是数字返回 null。</summary>
    internal static int? Integer(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) ||
            double.IsNaN(number) ||
            number is > int.MaxValue or < int.MinValue)
        {
            return null;
        }

        return (int)Math.Round(number, MidpointRounding.AwayFromZero);
    }

    /// <summary>取布尔；不是布尔返回 <paramref name="fallback"/>。</summary>
    internal static bool Flag(this JsonElement args, string name, bool fallback) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    /// <summary>字段是否存在（哪怕是 null）。</summary>
    internal static bool Has(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);

    /// <summary>一句人话的错误：这个参数是必填的。</summary>
    internal static string Required(string name) => $"The '{name}' argument is required.";

    /// <summary>把整数夹到 [min, max]；null 用 <paramref name="fallback"/>（fallback 自己也要在范围内）。</summary>
    internal static int Clamped(this JsonElement args, string name, int fallback, int min, int max) =>
        Math.Clamp(args.Integer(name) ?? fallback, min, max);

    /// <summary>千分位数字，给模型和用户看的人类可读数字。</summary>
    internal static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
