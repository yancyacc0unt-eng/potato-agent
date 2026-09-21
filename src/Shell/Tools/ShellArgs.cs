// pc_shell 的取参小助手：从模型给的 JsonElement 里"宽松地"取参数。
//
// 宽松是刻意的（和 Win32 层的 ToolArgs 一个路子）：模型的参数永远可能缺字段、类型写错、给个 null。
// 这里一律退回默认值或返回一句人话错误，绝不因为一个坏字段抛 NullReference 把整次调用搞崩。

using System.Globalization;
using System.Text.Json;

namespace PotatoAgent.Shell.Tools;

/// <summary>从 <see cref="JsonElement"/> 里安全取参数。</summary>
internal static class ShellArgs
{
    /// <summary>取字符串；不是字符串或字段不存在返回 null。</summary>
    internal static string? Text(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>取整数；不是数字返回 null（小数会四舍五入，字符串 "30" 不认）。</summary>
    internal static int? Integer(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number))
        {
            return null;
        }

        // 模型偶尔会给 1e9 这种离谱数字，先夹到 int 范围再转换，避免 OverflowException。
        if (double.IsNaN(number) || number <= int.MinValue || number >= int.MaxValue)
        {
            return null;
        }

        return (int)Math.Round(number, MidpointRounding.AwayFromZero);
    }

    /// <summary>一句人话的错误：这个参数是必填的。</summary>
    internal static string Required(string name) => $"The '{name}' argument is required.";
}

/// <summary>把秒数写成给模型看的整数文本（不跟着系统区域跑，避免出现 "30,0"）。</summary>
internal static class ShellText
{
    /// <summary>不依赖当前区域文化的整数文本。</summary>
    internal static string Invariant(this int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>不依赖当前区域文化的长整数文本。</summary>
    internal static string Invariant(this long value) => value.ToString(CultureInfo.InvariantCulture);
}
