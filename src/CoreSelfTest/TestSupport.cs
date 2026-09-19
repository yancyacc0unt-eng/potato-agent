// 自测用的公用小工具：把字节切成指定长度的碎片喂给 SseParser、把异常收进列表里、几条断言。
//
// 为什么非要自己写一个 Stream：TCP 分块和 SSE 的"行"没有任何关系，要复现"汉字被切成两半"这种事，
// 必须精确控制每次 ReadAsync 能拿到多少字节 —— MemoryStream 一次性全给你，测不出东西来。

using System.Text.Json;

namespace PotatoAgent.Core.SelfTest;

/// <summary>
/// 按固定块长把一段字节喂出去的只读流，用来复现任意位置的分块边界（1 = 每次只给一个字节）。
/// </summary>
internal sealed class ChunkedStream : Stream
{
    private readonly byte[] _data;
    private readonly int _chunkSize;
    private int _position;

    public ChunkedStream(byte[] data, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        _data = data;
        _chunkSize = chunkSize;
    }

    private ChunkedStream(string text, int chunkSize)
        : this(System.Text.Encoding.UTF8.GetBytes(text), chunkSize)
    {
    }

    /// <summary>按 UTF-8 编码 <paramref name="text"/>，再按 <paramref name="chunkSize"/> 字节一片喂出去。</summary>
    public static ChunkedStream Utf8(string text, int chunkSize) => new(text, chunkSize);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _data.Length - _position;
        if (remaining <= 0) return 0;

        var take = Math.Min(Math.Min(count, _chunkSize), remaining);
        Array.Copy(_data, _position, buffer, offset, take);
        _position += take;
        return take;
    }

    /// <summary>每次最多吐 <c>chunkSize</c> 个字节 —— 这就是被测代码看到的"网络分块"。</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = _data.Length - _position;
        if (remaining <= 0) return ValueTask.FromResult(0);

        var take = Math.Min(Math.Min(buffer.Length, _chunkSize), remaining);
        _data.AsMemory(_position, take).CopyTo(buffer);
        _position += take;
        return ValueTask.FromResult(take);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer, offset, count));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 把方法里抛出来的异常收进列表（不吞细节），返回 null 表示"居然没抛"。
/// 用 <c>Exception.GetType()</c> 精确匹配，方便断言"必须是这个子类，不能是基类"。
/// </summary>
internal static class Catch
{
    public static async Task<Exception?> Of(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>把一条异步序列整个拉完（异常会原样抛出来，交给 <see cref="Of"/> 收）。</summary>
    public static async Task<List<T>> DrainAsync<T>(IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            list.Add(item);
        }

        return list;
    }
}

/// <summary>断言失败时打印人话，不抛异常（自测要继续往下跑，把问题一次全报出来）。</summary>
internal static class Expect
{
    public static void Equal<T>(T expected, T actual, string what)
    {
        var ok = EqualityComparer<T>.Default.Equals(expected, actual);
        Program.Check(ok, $"{what}（期望 {Program.Trim(expected?.ToString())}，实际 {Program.Trim(actual?.ToString())}）");
    }

    public static void True(bool ok, string what) => Program.Check(ok, what);

    public static void Contains(string haystack, string needle, string what)
    {
        var ok = haystack.Contains(needle, StringComparison.Ordinal);
        Program.Check(ok, ok ? what : $"{what} —— 没找到 \"{Program.Trim(needle)}\"");
    }

    public static void DoesNotContain(string haystack, string needle, string what)
    {
        var ok = !haystack.Contains(needle, StringComparison.Ordinal);
        Program.Check(ok, ok ? what : $"{what} —— 居然找到了 \"{Program.Trim(needle)}\"");
    }

    /// <summary>断言异常正好是这个类型（子类也算过，但要把实际类型打出来）。</summary>
    public static void ExceptionType<T>(Exception? error, string what) where T : Exception
    {
        if (error is null)
        {
            Program.Fail($"{what} —— 居然没抛异常");
            return;
        }

        var ok = error is T;
        Program.Check(ok, $"{what}（实际 {error.GetType().Name}: {Program.Trim(error.Message)}）");
    }

    /// <summary>解析一段 JSON 文本；坏 JSON 直接判失败并返回 default。</summary>
    public static JsonElement Json(string text, string what)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Program.Fail($"{what} —— JSON 解析失败: {ex.Message}");
            return default;
        }
    }

    /// <summary>从 JSON 文本里取第一层的字符串字段（角色、id 这类协议字段就够用了）。</summary>
    public static string? Str(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
