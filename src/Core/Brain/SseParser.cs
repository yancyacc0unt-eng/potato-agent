using System.Runtime.CompilerServices;
using System.Text;

namespace PotatoAgent.Core.Brain;

/// <summary>一个解析好的 SSE 事件（OpenAI 流里就是一条 <c>data: {...}</c>）。</summary>
/// <param name="EventName">服务端给的 <c>event:</c> 名，没有则 null（OpenAI 不用这个字段）。</param>
/// <param name="Data">data 内容，可能等于 <c>[DONE]</c>。</param>
/// <param name="Id">服务端给的 <c>id:</c>，没有则 null。</param>
public sealed record SseEvent(string? EventName, string Data, string? Id);

/// <summary>
/// 手写 SSE 解析器（<c>text/event-stream</c>）。不引任何第三方库。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里最容易踩的坑</b>：TCP 分块和 SSE 的"行"没有任何关系。一个 <c>data:</c> 行会被切成两半发过来，
/// 半截 JSON 直接 <c>JsonDocument.Parse</c> 必炸；更阴的是<b>一个 UTF-8 汉字（3 字节）也会被切成两半</b>，
/// 用 <c>Encoding.UTF8.GetString</c> 逐块解码会把中文变成 <c>�</c>。
/// </para>
/// <para>所以这里做两件事：</para>
/// <list type="number">
/// <item>用 <see cref="Decoder"/> 增量解码：多字节字符不全就先压在解码器里，等下一块字节到齐再吐字符。</item>
/// <item>字符层再攒一个行缓冲 <see cref="StringBuilder"/>：只有看到 <c>\n</c> 才算一行到了；
/// 攒不够就等下一块。<c>\r\n</c> 和 <c>\n</c> 都认。</item>
/// </list>
/// <para>事件边界按 SSE 规范：空行结束一个事件，多条 <c>data:</c> 行属于同一个事件（这里直接首尾相接 ——
/// OpenAI 兼容服务端每个事件只有一行 data，而拼接不会破坏任何合法 JSON）。</para>
/// <para>流结束时如果还留着没换行的尾巴，也当成完整一行处理，并且最后一个事件不会因为缺少结尾空行而丢掉。</para>
/// </remarks>
public static class SseParser
{
    private const int ReadBufferSize = 4096;

    /// <summary>
    /// 从 <paramref name="stream"/> 里一条条读出 SSE 事件。
    /// 取消、网络断开、流被中途掐掉都会把异常原样抛给调用方（由 Provider 包装成人话）。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[ReadBufferSize];
        var chars = new char[ReadBufferSize];

        var lineBuffer = new StringBuilder();   // 已解码但还没见到 '\n' 的字符
        var scanFrom = 0;                       // lineBuffer 里已经确认没有 '\n' 的前缀长度
        var data = new StringBuilder();
        string? eventName = null;
        string? lastId = null;

        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            // 只解码"完整"的 UTF-8 序列，半个汉字留在 decoder 里等下一块。
            var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (charCount > 0)
            {
                lineBuffer.Append(chars, 0, charCount);
            }

            while (TryTakeLine(lineBuffer, ref scanFrom, out var line))
            {
                if (TryBuildEvent(line, data, ref eventName, ref lastId, out var evt))
                {
                    yield return evt;
                }
            }
        }

        // 收尾 1：把 decoder 里可能压着的残余字符吐出来（正常情况下是空的）。
        var tailChars = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        if (tailChars > 0)
        {
            lineBuffer.Append(chars, 0, tailChars);
        }

        // 收尾 2：流最后一行常常没有换行符，也当完整一行处理。
        if (lineBuffer.Length > 0)
        {
            var tail = lineBuffer.ToString();
            if (tail.EndsWith('\r'))
            {
                tail = tail[..^1];
            }

            lineBuffer.Clear();
            if (TryBuildEvent(tail, data, ref eventName, ref lastId, out var tailEvent))
            {
                yield return tailEvent;
            }
        }

        // 收尾 3：服务端没补结尾空行时，最后攒的那个事件不能丢。
        if (data.Length > 0 || eventName is not null)
        {
            var last = new SseEvent(eventName, data.ToString(), lastId);
            data.Clear();
            yield return last;
        }
    }

    /// <summary>从行缓冲里取出一整行（不含行尾）。取不到说明这一块还没凑齐一行，返回 false 等下一块。</summary>
    private static bool TryTakeLine(StringBuilder buffer, ref int scanFrom, out string line)
    {
        for (var i = scanFrom; i < buffer.Length; i++)
        {
            if (buffer[i] != '\n')
            {
                continue;
            }

            var length = i;
            if (length > 0 && buffer[length - 1] == '\r')
            {
                length--;
            }

            line = buffer.ToString(0, length);
            buffer.Remove(0, i + 1);
            scanFrom = 0;
            return true;
        }

        // 这一块里确定没有换行；下次只从新追加的部分往后扫。
        scanFrom = buffer.Length;
        line = string.Empty;
        return false;
    }

    /// <summary>处理一行；凑成一个完整事件时返回 true。空行 = 事件结束。</summary>
    private static bool TryBuildEvent(
        string line,
        StringBuilder data,
        ref string? eventName,
        ref string? lastId,
        out SseEvent evt)
    {
        if (line.Length == 0)
        {
            if (data.Length == 0 && eventName is null)
            {
                evt = null!;
                return false;
            }

            evt = new SseEvent(eventName, data.ToString(), lastId);
            data.Clear();
            eventName = null;
            return true;
        }

        if (line[0] == ':')
        {
            // 注释行，服务端拿它当心跳（": keep-alive"）。
            evt = null!;
            return false;
        }

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        switch (field)
        {
            case "data":
                data.Append(value);
                break;
            case "event":
                eventName = value;
                break;
            case "id":
                lastId = value;
                break;
            case "retry":
                break;
            default:
                break;
        }

        evt = null!;
        return false;
    }
}
