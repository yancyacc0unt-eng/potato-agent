// ---------- 一、SSE 解析 ----------
//
// 核心命题：网络分块和 SSE 的行没有任何关系。
// 这里把一段完整响应真的切成 1 字节一片（于是【每个汉字的 3 字节序列都被切开】、
// 每个 data: 行都被从中间切开），要求拼出来的文本一字不差。
// 顺带覆盖 \r\n 变体、[DONE]、末尾没有换行的尾巴、空行分事件、注释心跳、多条 data 行。

using System.Text;
using PotatoAgent.Core.Brain;

namespace PotatoAgent.Core.SelfTest;

internal static class SseTests
{
    /// <summary>要逐字节复现的中文回复（含标点、空格）。</summary>
    private const string Reply = "你好，世界！土豆智能体在跑 SSE 解析自测。";

    /// <summary>工具调用参数：JSON 字符串里还套着中文和转义引号，最容易在分块解码上翻车。</summary>
    private const string ToolArgs = "{\"city\":\"上海\",\"unit\":\"celsius\",\"note\":\"带\\\"引号\\\"的中文\"}";

    public static void Run()
    {
        Console.WriteLine("---- 一、SSE 解析 ----");

        SplitEveryByte();
        ChunkSizes();
        CrlfAndEmptyLine();
        PaddingAndComments();
        TrailingTail();
        MultiLineData();
    }

    // ==================== 1.1 最狠的一种：每次只给 1 个字节 ====================

    private static void SplitEveryByte()
    {
        Console.WriteLine("  1.1 逐字节切碎（每个汉字 3 字节、每个 data: 行都被从中间切开）");

        var body = Events(Sse.Text(Reply), Sse.Comment, Sse.Done);
        Console.WriteLine($"    响应 {Encoding.UTF8.GetByteCount(body)} 字节 → 切成 {Encoding.UTF8.GetByteCount(body)} 片喂进去");
        Console.WriteLine($"    期望文本: {Reply}");

        var events = ReadBytes(body, 1).GetAwaiter().GetResult();
        var text = TextOf(events);

        Console.WriteLine($"    实际文本: {Program.Trim(text, 120)}");
        Console.WriteLine($"    事件数  : {events.Count}（注释心跳不该产出事件，所以是 2）");

        Expect.Equal(Reply, text, "1 字节一片时，正文一字不差");
        Expect.Equal(2, events.Count, "注释心跳被忽略，事件数正好 2 个");
        Expect.Equal("[DONE]", events.Count > 1 ? events[1].Data : "<缺>", "[DONE] 原样解析出来");
        Console.WriteLine();
    }

    // ==================== 1.2 各种块长都要一字不差 ====================

    private static void ChunkSizes()
    {
        Console.WriteLine("  1.2 遍历块长（含「切开汉字」的位置）");

        var body = Events(Sse.Text(Reply), Sse.Tool(0, "call_中文id", "get_weather", ToolArgs), Sse.Done);
        var total = Encoding.UTF8.GetByteCount(body);
        var bad = new List<int>();

        foreach (var size in new[] { 1, 2, 3, 4, 5, 7, 11, 13, 64, 4096, total })
        {
            var events = ReadBytes(body, size).GetAwaiter().GetResult();
            var text = TextOf(events);
            var arguments = events.Count > 1 ? ToolArgumentsIn(events[1].Data) : "<缺>";

            var ok = text == Reply && arguments == ToolArgs && events.Count == 3;
            if (!ok) bad.Add(size);
        }

        Program.Check(bad.Count == 0,
            bad.Count == 0
                ? $"块长 1/2/3/4/5/7/11/13/64/4096/整包 全部一字不差（响应共 {total} 字节）"
                : $"块长 {string.Join(",", bad)} 时结果不对");

        // 单独把工具调用那一帧拆开看，方便出错时定位。
        var sample = ReadBytes(body, 1).GetAwaiter().GetResult();
        if (sample.Count == 3)
        {
            Console.WriteLine($"    [1] data = {Program.Trim(sample[1].Data, 110)}");

            var arguments = ToolArgumentsIn(sample[1].Data);
            Expect.Equal(ToolArgs, arguments, "工具调用帧的 arguments 逐字符一致（含中文和转义引号）");
            Expect.Contains(sample[1].Data, "call_中文id", "帧里的 id 也是原文（没被转成 \\u 转义）");
        }
        else
        {
            Program.Fail($"期望 3 个事件，实际 {sample.Count} 个（工具调用帧丢了？）");
        }

        Console.WriteLine();
    }

    // ==================== 1.3 \r\n 变体 + 空行分事件 ====================

    private static void CrlfAndEmptyLine()
    {
        Console.WriteLine("  1.3 \\r\\n 变体 + 空行分事件");

        const string A = "第一段";
        const string B = "第二段";

        CheckOne(Events(Sse.Text(A), Sse.Text(B), Sse.Done), "\n", A + B, "LF 分行、空行分事件");
        CheckOne(Events(Sse.Text(A), Sse.Text(B), Sse.Done), "\r\n", A + B, "CRLF 分行、空行分事件");
        CheckOne(Events(Sse.Text(A), Sse.Text(B), Sse.Done), "\r\n\n", A + B, "CRLF 与 LF 混用");
        CheckOne(Events(Sse.Text(A), Sse.Done), "\n\n", A, "连着的两个空行不产出空事件");
        CheckOne(Events(Sse.Text(A), Sse.Done), "\n\r\n", A, "行尾是 \\n\\r\\n 这种混搭");

        Console.WriteLine();
    }

    // ==================== 1.4 冒号后空格 / 无空格 / 注释心跳 ====================

    private static void PaddingAndComments()
    {
        Console.WriteLine("  1.4 冒号后空格 / 无空格 / 注释心跳");

        const string A = "无空格也认";
        CheckOne("data:" + Sse.Text(A).Substring("data:".Length) + "\n\ndata: [DONE]\n\n", "\n", A, "`data:` 之后没有空格也认");

        var withComments = Events(Sse.Comment, Sse.Text(A), ": 中途又来一次心跳", Sse.Done);
        CheckOne(withComments, "\n", A, "事件中间的注释行被忽略");

        var noSpace = "event: ping\ndata: {\"choices\":[]}\n\n" + Sse.Text(A) + "\n\ndata: [DONE]\n\n";
        var events = Read(noSpace, 3).GetAwaiter().GetResult();
        Program.Check(events.Count == 3, $"带 event: 字段的心跳事件照样解析（实际 {events.Count} 个事件）");
        Program.Check(events.Count == 3 && events[0].EventName == "ping", "event: ping 被记进 EventName");
        Expect.Equal(A, TextOf(events), "后面的正文不受影响");

        Console.WriteLine();
    }

    // ==================== 1.5 末尾没有换行的尾巴 ====================

    private static void TrailingTail()
    {
        Console.WriteLine("  1.5 流结束时的「尾巴」（服务端没补结尾换行）");

        var noNewlineAtAll = Sse.Text("没有换行的尾巴");
        Expect.Equal("没有换行的尾巴", TextOf(Read(noNewlineAtAll, 1).GetAwaiter().GetResult()), "整段没有 \\n 也能解析出来");

        var noBlankLine = Sse.Text("最后一行之后没有空行");
        Expect.Equal("最后一行之后没有空行", TextOf(Read(noBlankLine, 1).GetAwaiter().GetResult()), "最后一行后面没有空行也不丢事件");

        var crTail = Sse.Text("尾巴只剩一个回车") + "\r";
        Expect.Equal("尾巴只剩一个回车", TextOf(Read(crTail, 1).GetAwaiter().GetResult()), "尾巴上孤零零的 \\r 要剥掉");

        // 真实场景：data 块和 [DONE] 中间只有换行，没有空行 —— 两条 data 会被当成同一个事件拼起来。
        var glued = Sse.Text("甲") + "\n" + Sse.Text("乙");
        var gluedEvents = Read(glued, 1).GetAwaiter().GetResult();
        Program.Check(gluedEvents.Count == 1, $"没有空行分隔时按 SSE 规范合并成一个事件（实际 {gluedEvents.Count} 个）");
        Program.Check(gluedEvents.Count == 1 && gluedEvents[0].Data == Sse.Text("甲")[6..] + Sse.Text("乙")[6..],
            "合并后两条 data 首尾相接（各自去掉了 \"data: \" 前缀），谁都没丢");

        Console.WriteLine();
    }

    // ==================== 1.6 一个事件里多条 data 行 ====================

    private static void MultiLineData()
    {
        Console.WriteLine("  1.6 一个事件里多条 data 行");

        var body = "data: 第一行\ndata: 第二行\n\n" + Sse.Done + "\n\n";
        var events = Read(body, 2).GetAwaiter().GetResult();

        Program.Check(events.Count == 2, $"两个事件（实际 {events.Count} 个）");
        Expect.Equal("第一行第二行", events.Count > 0 ? events[0].Data : "<缺>", "多条 data 首尾相接");

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    /// <summary>把若干 <c>data:</c> 行各自当成一个【事件】拼起来：事件之间必须有空行，否则按 SSE 规范它们属于同一个事件。</summary>
    private static string Events(params string[] events) => string.Join("\n\n", events) + "\n\n";

    private static void CheckOne(string body, string separator, string expected, string what)
    {
        var events = Read(separator == "\n" ? body : Normalize(body, separator), 1).GetAwaiter().GetResult();
        var text = TextOf(events);
        Program.Check(text == expected, text == expected ? what : $"{what} —— 实际 \"{Program.Trim(text)}\"");
    }

    /// <summary>
    /// 把 LF 换成别的行尾。空行（<c>\n\n</c>）要原样保留成空行 ——
    /// 直接替换会把两个事件的间隔吃掉，测试数据就错了（这个坑已经踩过一次）。
    /// </summary>
    private static string Normalize(string body, string separator)
    {
        const string Blank = "\u0001";   // 占位符，正文里不可能出现
        var lf = body.Replace("\r\n", "\n");
        var marker = lf.Replace("\n\n", Blank);
        return marker.Replace("\n", separator).Replace(Blank, separator + separator);
    }

    /// <summary>从一个 SSE 事件的 data 里取出 tool_calls[0].function.arguments 的值。</summary>
    private static string ToolArgumentsIn(string data)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(data);
        var choices = doc.RootElement.GetProperty("choices");
        var delta = choices[0].GetProperty("delta");
        var calls = delta.GetProperty("tool_calls");
        return calls[0].GetProperty("function").GetProperty("arguments").GetString() ?? "<null>";
    }

    /// <summary>把正文增量全部拼起来。不是合法 JSON 的帧直接跳过（那属于别的用例该报的问题）。</summary>
    private static string TextOf(IReadOnlyList<SseEvent> events)
    {
        var text = new StringBuilder();
        foreach (var evt in events)
        {
            if (evt.Data.Length == 0 || evt.Data == "[DONE]")
            {
                continue;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(evt.Data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content) &&
                            content.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            text.Append(content.GetString());
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                Program.Fail($"事件 data 不是合法 JSON: {Program.Trim(evt.Data)}");
            }
        }

        return text.ToString();
    }

    private static Task<List<SseEvent>> Read(string text, int chunkSize)
        => Catch.DrainAsync(SseParser.ReadAsync(ChunkedStream.Utf8(text, chunkSize)));

    private static Task<List<SseEvent>> ReadBytes(string text, int chunkSize)
        => Catch.DrainAsync(SseParser.ReadAsync(new ChunkedStream(Encoding.UTF8.GetBytes(text), chunkSize)));
}
