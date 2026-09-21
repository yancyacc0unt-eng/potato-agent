using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// OpenAI 兼容协议的流式客户端：<c>POST {baseUrl}/v1/chat/completions</c>（<c>stream: true</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只用 <see cref="HttpClient"/>，不引任何第三方 SDK</b> —— 用户自填接口地址，换厂家只要改配置，
/// 不用等哪家出了新包。请求体、SSE 解析、tool_calls 拼接全是手写的。
/// </para>
/// <para>这一层只负责"发一次请求、把流读成人能理解的事件"；要调工具请用 <see cref="ChatSession"/>（它管工具循环）。</para>
/// <para>错误一律包装成 <see cref="ProviderException"/> 的子类：HTTP 非 2xx → <see cref="ProviderHttpException"/>，
/// 超时 → <see cref="ProviderTimeoutException"/>，分块不是合法 JSON → <see cref="ProviderProtocolException"/>，
/// 连不上/中途断流 → <see cref="ProviderNetworkException"/>。取消（<see cref="OperationCanceledException"/>）原样上抛。</para>
/// </remarks>
public sealed class OpenAiProvider : IDisposable
{
    /// <summary>自己 new HttpClient 时的默认超时。注意 .NET 的超时也管读流，所以别设太短。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions MessageJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// 用一套档案建 Provider。<paramref name="httpClient"/> 传 null 时自己 new 一个（超时见 <see cref="DefaultTimeout"/>，
    /// 由本实例负责 Dispose）；外面传进来时（例如全应用共用一个连接池）本实例不负责它的生命周期。
    /// </summary>
    public OpenAiProvider(ProviderProfile profile, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // 拷一份：调用方（设置页）随后改 profile 不该影响正在跑的这一轮。
        Profile = profile.Clone();

        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = DefaultTimeout };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>本实例使用的档案（构造时的快照，含明文密钥，别往日志里打）。</summary>
    public ProviderProfile Profile { get; }

    /// <summary>
    /// 请求里要不要带 <c>stream_options.include_usage</c> 去要 token 用量。
    /// 默认 <b>false</b>：这是 OpenAI 后加的字段，有些第三方兼容服务端见到不认识的字段会直接 400，
    /// 而"用户自填地址"意味着要尽量兼容野路子服务端。确定自家服务端支持再打开。
    /// </summary>
    public bool IncludeUsage { get; set; }

    /// <summary>这次请求真正会打的地址，设置页可以显示出来给用户核对。</summary>
    public Uri Endpoint => BuildChatCompletionsUri(Profile.BaseUrl);

    /// <summary>
    /// 最近一次 <see cref="StreamCompletionAsync"/> 的实测时序（首字节 / 首个文本增量 / 增量个数 / 增量间隔）。
    /// 还没有跑过任何一次请求时为 null。诊断"回答像一次性蹦出来"时先看它，别猜。
    /// </summary>
    public StreamTiming? LastStreamTiming { get; private set; }

    /// <summary>每跑完一次流式请求（正常或出错收尾）回调一次实测时序；想立刻记一笔日志就挂它。</summary>
    public Action<StreamTiming>? TimingReported { get; set; }

    /// <summary>从配置仓库里取当前档案建一个 Provider；没有可用档案时抛 <see cref="ProviderConfigurationException"/>。</summary>
    [SupportedOSPlatform("windows")]   // 因为 ConfigStore 用 DPAPI
    public static OpenAiProvider FromActiveProfile(ConfigStore store, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var profile = store.ActiveProfile
            ?? throw new ProviderConfigurationException("No provider profile is configured yet. Fill in Base URL / API key / model first.");

        var missing = profile.DescribeMissing();
        if (missing is not null)
        {
            throw new ProviderConfigurationException($"Provider profile '{profile.Name}' is incomplete: {missing}");
        }

        return new OpenAiProvider(profile, httpClient);
    }

    /// <summary>
    /// 把用户填的 base URL 补成 chat/completions 地址。三种写法都认：
    /// <c>https://api.x.com</c> → <c>/v1/chat/completions</c>；<c>https://api.x.com/v1</c> → 同上；
    /// 已经是完整端点 <c>.../v1/chat/completions</c> 就原样用。
    /// </summary>
    public static Uri BuildChatCompletionsUri(string? baseUrl)
    {
        var uri = ParseBase(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/');

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            // 用户直接粘了完整端点，尊重它。
        }
        else if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            path += "/chat/completions";
        }
        else
        {
            path += "/v1/chat/completions";
        }

        return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    /// <summary>预留：<c>GET {baseUrl}/v1/models</c> 的地址。</summary>
    public static Uri BuildModelsUri(string? baseUrl)
    {
        var uri = ParseBase(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/');

        if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? path + "/models"
                : path + "/v1/models";
        }

        return new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty }.Uri;
    }

    /// <summary>
    /// 拉服务端当前可用的模型列表（<c>GET {base}/v1/models</c>），给界面上的"模型"下拉框用。
    /// </summary>
    /// <remarks>
    /// <para>返回的是响应里 <c>data[].id</c> 那一串名字，已排序去重（大小写不敏感）。</para>
    /// <para>失败一律是 <see cref="ProviderException"/> 的子类：超时 → <see cref="ProviderTimeoutException"/>、
    /// 连不上 → <see cref="ProviderNetworkException"/>、非 2xx → <see cref="ProviderHttpException"/>、
    /// 回的不是模型列表 → <see cref="ProviderProtocolException"/>。<b>不抛裸异常</b>，界面照旧翻译成人话。</para>
    /// </remarks>
    /// <param name="ct">取消令牌。</param>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var uri = BuildModelsUri(Profile.BaseUrl);   // 地址没填/格式不对，这里就抛 ProviderConfigurationException

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(Profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Profile.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderTimeoutException(
                $"Request to {uri} timed out (HttpClient.Timeout = {_http.Timeout}).", _http.Timeout, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderNetworkException($"Cannot reach {uri}: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderHttpException(response.StatusCode, body, uri.ToString());
            }

            return ParseModelList(body, uri);
        }
    }

    /// <summary>解析 <c>GET /v1/models</c> 的响应体：取 <c>data[].id</c>，排序去重。</summary>
    /// <remarks>
    /// 只认 OpenAI 那套 <c>{"data":[{"id":"..."}]}</c>。服务端没实现这个端点、或者回了个别的形状时，
    /// 抛 <see cref="ProviderProtocolException"/> 并把原文带上 —— 用户看得到原始响应，才知道该怎么改。
    /// </remarks>
    private static IReadOnlyList<string> ParseModelList(string body, Uri uri)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ProviderProtocolException("The model list is not valid JSON (protocol mismatch?).", body, ex);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new ProviderProtocolException(
                    $"The response from {uri} has no 'data' array, so it is not an OpenAI-style model list.", body);
            }

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    names.Add(id.GetString()!);
                }
            }

            if (names.Count == 0)
            {
                throw new ProviderProtocolException($"The server returned an empty model list from {uri}.", body);
            }

            return names.ToList();
        }
    }

    /// <summary>
    /// 发一次流式请求，把响应逐块吐成 <see cref="ChatStreamEvent"/>。
    /// 消费方 <c>await foreach</c> 到结束会额外收到一条 <see cref="ChatFinished"/>。
    /// </summary>
    /// <remarks>
    /// 这一层同时负责<b>实测</b>：每次请求收尾（正常结束、出错、被提前放弃都算）都会刷新
    /// <see cref="LastStreamTiming"/>，并回调 <see cref="TimingReported"/>。
    /// 计时点都在"事件刚被解析出来"的那一刻，与消费方拉得多快无关。
    /// </remarks>
    /// <param name="messages">完整上下文（含 system / 历史 / 上一轮 tool 结果）。</param>
    /// <param name="tools">可给模型调用的工具；null 或空 = 不带 tools 字段。</param>
    /// <param name="ct">取消令牌。取消时抛 <see cref="OperationCanceledException"/>，不会被包装成超时。</param>
    public async IAsyncEnumerable<ChatStreamEvent> StreamCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timing = new StreamTimingCollector();

        // yield return 不能出现在带 catch 的 try 里，但可以出现在只带 finally 的 try 里 ——
        // 于是"每个事件立刻往上抛"和"无论怎么收场都记下时序"两件事可以同时成立。
        try
        {
            await foreach (var evt in StreamCoreAsync(messages, tools, timing, ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            PublishTiming(timing.Build());
        }
    }

    /// <summary>真正干活的流式实现；时序埋点靠 <paramref name="timing"/> 带出去。</summary>
    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool>? tools,
        StreamTimingCollector timing,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var uri = Endpoint;   // 地址没填/格式不对，这里就抛 ProviderConfigurationException
        if (string.IsNullOrWhiteSpace(Profile.Model))
        {
            throw new ProviderConfigurationException("Model name is empty. Set it in the provider profile.");
        }

        var payload = BuildRequestBody(messages, tools, stream: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrEmpty(Profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Profile.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 自己的超时也表现为取消 —— 用 ct 区分是"用户按了停止"还是"等太久了"。
            throw new ProviderTimeoutException(
                $"Request to {uri} timed out (HttpClient.Timeout = {_http.Timeout}).", _http.Timeout, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderNetworkException($"Cannot reach {uri}: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodySafelyAsync(response, ct).ConfigureAwait(false);
                throw new ProviderHttpException(response.StatusCode, body, uri.ToString());
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new ProviderNetworkException($"Failed to open the response stream from {uri}: {ex.Message}", ex);
            }

            // 套一层只干一件事的壳：第一次真读到字节时打时间戳 —— 那就是"首字节时间"。
            await using (var measured = new FirstByteStream(stream, timing))
            {
                string? finishReason = null;
                TokenUsage? usage = null;

                await foreach (var sse in ReadEventsAsync(measured, ct).ConfigureAwait(false))
                {
                    if (sse.Data.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(sse.Data, "[DONE]", StringComparison.Ordinal))
                    {
                        break;
                    }

                    foreach (var evt in ParseChunk(sse.Data, ref finishReason, ref usage))
                    {
                        if (evt is ChatTextDelta)
                        {
                            timing.ObserveTextDelta();
                        }

                        yield return evt;
                    }
                }

                yield return new ChatFinished(finishReason, usage);
            }
        }
    }

    /// <summary>把一轮回复的文本收齐（不要流式界面时用，例如 Tasks 页跑一段提示词）。</summary>
    public async Task<string> CompleteTextAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool>? tools = null,
        CancellationToken ct = default)
    {
        var text = new StringBuilder();

        await foreach (var evt in StreamCompletionAsync(messages, tools, ct).ConfigureAwait(false))
        {
            if (evt is ChatTextDelta delta)
            {
                text.Append(delta.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>流式请求 + 每个文本增量回调一次（界面直接刷气泡用）。返回拼好的全文。</summary>
    public async Task<string> StreamTextAsync(
        IReadOnlyList<ChatMessage> messages,
        Action<string> onTextDelta,
        IReadOnlyList<ITool>? tools = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onTextDelta);

        var text = new StringBuilder();

        await foreach (var evt in StreamCompletionAsync(messages, tools, ct).ConfigureAwait(false))
        {
            if (evt is ChatTextDelta delta)
            {
                text.Append(delta.Text);
                onTextDelta(delta.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>只 Dispose 自己 new 的那个 HttpClient。</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>把一次请求的实测时序公开出去（顺手回调观察者；观察者自己炸了不影响这次请求）。</summary>
    private void PublishTiming(StreamTiming timing)
    {
        LastStreamTiming = timing;

        var callback = TimingReported;
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(timing);
        }
        catch
        {
            // 观察者是外部代码，它抛异常不该把一次成功的请求变成失败。
        }
    }

    /// <summary>一次请求的时序账本：秒表 + 首字节 + 首增量 + 增量个数 + 增量间隔。</summary>
    private sealed class StreamTimingCollector
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan? _lastTextDelta;

        /// <summary>响应体第一个字节到达的时刻（由 <see cref="FirstByteStream"/> 打点）。</summary>
        internal TimeSpan? FirstByte { get; private set; }

        /// <summary>记下"第一个字节到了"（只记第一次）。</summary>
        internal void MarkFirstByte() => FirstByte ??= _clock.Elapsed;

        private TimeSpan? FirstTextDelta { get; set; }

        private int TextDeltaCount { get; set; }

        private List<TimeSpan> TextDeltaGaps { get; } = new();

        /// <summary>刚解析出一个文本增量：记首增量、记它与上一个之间的间隔。</summary>
        internal void ObserveTextDelta()
        {
            var now = _clock.Elapsed;

            FirstTextDelta ??= now;
            if (_lastTextDelta is { } previous)
            {
                TextDeltaGaps.Add(now - previous);
            }

            _lastTextDelta = now;
            TextDeltaCount++;
        }

        /// <summary>收尾：结账。</summary>
        internal StreamTiming Build() =>
            new(_clock.Elapsed, FirstByte, FirstTextDelta, TextDeltaCount, TextDeltaGaps);
    }

    /// <summary>
    /// 只做一件事的只读壳：<b>第一次真的读到字节</b>时打一个时间戳，然后原样转发。
    /// 首字节时间是"服务端第一次吐东西"的硬证据，不能靠猜。
    /// </summary>
    private sealed class FirstByteStream : Stream
    {
        private readonly Stream _inner;
        private readonly StreamTimingCollector _timing;
        private bool _stamped;

        internal FirstByteStream(Stream inner, StreamTimingCollector timing)
        {
            _inner = inner;
            _timing = timing;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Stamp(_inner.Read(buffer, offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            StampAsync(_inner.ReadAsync(buffer, offset, count, cancellationToken));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            StampAsync(_inner.ReadAsync(buffer, cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Stamp(int read)
        {
            Mark(read);
            return read;
        }

        private async Task<int> StampAsync(Task<int> pending)
        {
            var read = await pending.ConfigureAwait(false);
            Mark(read);
            return read;
        }

        private async ValueTask<int> StampAsync(ValueTask<int> pending)
        {
            var read = await pending.ConfigureAwait(false);
            Mark(read);
            return read;
        }

        /// <summary>只认"第一次读到内容"那一下；读到 0（流结束）不算首字节。</summary>
        private void Mark(int read)
        {
            if (_stamped || read <= 0)
            {
                return;
            }

            _stamped = true;
            _timing.MarkFirstByte();
        }
    }

    /// <summary>自己 new 的 HttpClient 时套一层错误翻译，避免裸 IOException 冒到界面。</summary>
    private static async IAsyncEnumerable<SseEvent> ReadEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = SseParser.ReadAsync(stream, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // 服务端流到一半把连接掐了（HttpIOException 也是 IOException）。
                    throw new ProviderNetworkException(
                        "The server closed the connection in the middle of the stream.", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new ProviderNetworkException("The stream failed while reading the response body.", ex);
                }

                if (!moved)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private byte[] BuildRequestBody(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ITool>? tools, bool stream)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", Profile.Model);

            writer.WritePropertyName("messages");
            JsonSerializer.Serialize(writer, messages, MessageJsonOptions);

            writer.WriteBoolean("stream", stream);

            if (Profile.Temperature is { } temperature && temperature >= 0)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (Profile.MaxTokens is { } maxTokens && maxTokens > 0)
            {
                writer.WriteNumber("max_tokens", maxTokens);
            }

            // 推理强度：只有用户明确设了才发 —— 各家服务端认的词和认不认这个字段都不一样，
            // 默认不发（null）才不会把本来能跑的请求打成 400。
            if (!string.IsNullOrWhiteSpace(Profile.ReasoningEffort))
            {
                writer.WriteString("reasoning_effort", Profile.ReasoningEffort);
            }

            if (tools is { Count: > 0 })
            {
                writer.WritePropertyName("tools");
                ToolRegistry.WriteTools(writer, tools);
                writer.WriteString("tool_choice", "auto");
            }

            if (stream && IncludeUsage)
            {
                writer.WritePropertyName("stream_options");
                writer.WriteStartObject();
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>解析一个 SSE data 块；顺手更新 finish_reason / usage 到调用方传进来的局部变量。</summary>
    private static List<ChatStreamEvent> ParseChunk(string payload, ref string? finishReason, ref TokenUsage? usage)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ProviderProtocolException(
                "The server sent a stream chunk that is not valid JSON (protocol mismatch?).", payload, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProviderProtocolException(
                    $"Unexpected stream chunk: JSON {root.ValueKind}, expected an object.", payload);
            }

            // 有些服务端 HTTP 200 但流里塞 error 对象（限额、上下文超长）。
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new ProviderApiException(
                    "The server reported an error inside the stream: " + DescribeError(error),
                    GetString(error, "code"),
                    GetString(error, "type"),
                    payload);
            }

            var events = new List<ChatStreamEvent>(2);

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = ReadUsage(usageElement);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return events;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                {
                    finishReason = reason.GetString();
                }

                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        events.Add(new ChatTextDelta(text!));
                    }
                }

                // 思考模式的思维链：单独成一种事件往上走，绝不能混进正文（混了界面会把思考过程当回答显示）。
                // 空串当成"这一片没有"（有的服务端会先发一个空的占位帧）。
                if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                {
                    var thought = reasoning.GetString();
                    if (!string.IsNullOrEmpty(thought))
                    {
                        events.Add(new ChatReasoningDelta(thought!));
                    }
                }

                if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var call in toolCalls.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    int? index = null;
                    if (call.TryGetProperty("index", out var indexElement) &&
                        indexElement.ValueKind == JsonValueKind.Number &&
                        indexElement.TryGetInt32(out var parsedIndex))
                    {
                        index = parsedIndex;
                    }

                    string? name = null;
                    var arguments = string.Empty;
                    if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
                    {
                        name = GetString(function, "name");
                        arguments = GetString(function, "arguments") ?? string.Empty;
                    }

                    events.Add(new ChatToolCallDelta(index, GetString(call, "id"), name, arguments));
                }
            }

            return events;
        }
    }

    private static TokenUsage ReadUsage(JsonElement element)
    {
        return new TokenUsage(
            ReadInt(element, "prompt_tokens"),
            ReadInt(element, "completion_tokens"),
            ReadInt(element, "total_tokens"));
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string DescribeError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? "unknown";
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            return GetString(error, "message")
                ?? GetString(error, "code")
                ?? error.GetRawText();
        }

        return error.GetRawText();
    }

    private static async Task<string> ReadBodySafelyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= 4000 ? body : body[..4000] + "…";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return $"<failed to read the error body: {ex.Message}>";
        }
    }

    private static Uri ParseBase(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ProviderConfigurationException(
                "Base URL is empty. Fill in the API address in Settings first (e.g. https://api.openai.com).");
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ProviderConfigurationException(
                $"Base URL '{baseUrl}' is not a valid http(s) URL.");
        }

        return uri;
    }
}
