using System.Text;
using System.Text.Json;

namespace PotatoAgent.Core.Agent;

/// <summary>
/// 一次工具调用的"签名"：工具名 + <b>规范化</b>后的参数 JSON，用来判断两次调用是不是同一件事。
/// </summary>
/// <remarks>
/// <para>
/// 规范化 = 解析 → 对象属性按名字排序 → 去掉所有空白 → 重新序列化。
/// 于是下面三对在签名上完全相等（逐字节）：
/// </para>
/// <code>
/// {"path":"notepad.exe"}            vs  { "path" : "notepad.exe" }
/// {"a":1,"b":2}                     vs  {"b":2,"a":1}
/// {"path":"notepad.exe","x":null}   vs  {"x":null,"path":"notepad.exe"}
/// </code>
/// <para>数组顺序<b>不</b>参与排序（数组是有序的，顺序不同就是不同的调用）。</para>
/// <para>参数不是合法 JSON 时退化成"去掉首尾空白的原文" —— 反正它随后也会被参数解析拦下，
/// 这里不额外制造第二种错误。</para>
/// </remarks>
public static class ToolCallSignature
{
    /// <summary>工具名 + 规范化参数拼成的签名（两个字段之间用 <c>\n</c> 分隔，避免名字与参数串味）。</summary>
    /// <param name="name">工具名。</param>
    /// <param name="argumentsJson">模型给的参数原文。</param>
    public static string Of(string? name, string? argumentsJson) =>
        (name ?? string.Empty) + "\n" + Canonicalize(argumentsJson);

    /// <summary>把参数 JSON 规范化成唯一的字节串（见类型备注里的三条等价规则）。</summary>
    /// <param name="argumentsJson">参数原文；null / 空白按 <c>{}</c> 处理。</param>
    public static string Canonicalize(string? argumentsJson)
    {
        var text = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson!;

        try
        {
            using var document = JsonDocument.Parse(text);
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                Write(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return text.Trim();
        }
    }

    /// <summary>递归写：对象排序、数组保序、标量原样。</summary>
    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
