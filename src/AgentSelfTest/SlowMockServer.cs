// 慢速假服务端：故意把回答切成很多小块，每块之间隔一段时间，一块一块地 flush 出去。
//
// 和 MockServer 的区别只有一个，但正是流式诊断需要的那一个：
// MockServer 把整段 SSE 一次性写完再关连接（客户端只能看到"一下子全到了"），
// 这里每写完一帧就 FlushAsync —— 网络层上的分块是【真的】分块。
//
// 所以它能把"是模型/接口的首字延迟"和"是我们自己在攒"这两件事分开：
//   如果服务端 50ms 吐一块、我们却到最后一刻才把事件交给界面 —— 那是我们自己的缓冲。

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace PotatoAgent.Agent.SelfTest;

/// <summary>按固定间隔一帧一帧吐 SSE 的本地假服务端。用完必须 Dispose。</summary>
internal sealed class SlowMockServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<long> _sentAt = new();

    private SlowMockServer(int port, int frames, int gapMs)
    {
        Port = port;
        Frames = frames;
        GapMs = gapMs;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>起一个"共 <paramref name="frames"/> 帧、每帧之间隔 <paramref name="gapMs"/> 毫秒"的假服务端。</summary>
    public static SlowMockServer Start(int frames, int gapMs) => new(FindFreePort(), frames, gapMs);

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>正文帧数。</summary>
    public int Frames { get; }

    /// <summary>每帧之间的间隔（毫秒）。</summary>
    public int GapMs { get; }

    /// <summary>喂给 OpenAiProvider 的 base URL。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>每一帧真正写出去的时刻（相对本连接开始，毫秒）。</summary>
    public IReadOnlyList<long> SentAt
    {
        get { lock (_gate) return _sentAt.ToArray(); }
    }

    /// <summary>第 <paramref name="index"/> 帧的正文（从 0 起）。</summary>
    public static string TextOf(int index) => $"chunk{index:D2}|";

    /// <summary>一帧正文文本拼起来就是完整回答。</summary>
    public static string FullText(int frames)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < frames; i++)
        {
            builder.Append(TextOf(i));
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch { /* 已经停了 */ }
        try { _listener.Stop(); } catch { /* 已经停了 */ }
        try { _listener.Close(); } catch { /* 已经关了 */ }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";

            // 关键：不设 ContentLength64 + SendChunked = 每次 Flush 真的作为一块发出去，
            // 而不是"先在服务端攒满缓冲区再一次性发出"。
            context.Response.SendChunked = true;

            var output = context.Response.OutputStream;
            var clock = Stopwatch.StartNew();

            for (var index = 0; index < Frames; index++)
            {
                await WriteAsync(output, Sse.Text(TextOf(index)) + "\n\n").ConfigureAwait(false);
                lock (_gate)
                {
                    _sentAt.Add(clock.ElapsedMilliseconds);
                }

                if (index + 1 < Frames)
                {
                    await Task.Delay(GapMs).ConfigureAwait(false);
                }
            }

            await WriteAsync(output, Sse.Finish("stop") + "\n\n" + Sse.Done + "\n\n").ConfigureAwait(false);
            output.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { /* 客户端提前跑了 */ }
        }
    }

    private static async Task WriteAsync(Stream output, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
