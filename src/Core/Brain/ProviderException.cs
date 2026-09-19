using System.Net;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 大脑（Provider）层所有异常的基类。上层界面统一 <c>catch (ProviderException ex)</c> 就能拿到人话提示。
/// </summary>
/// <remarks>
/// 刻意分细：HTTP 状态码错了、超时了、服务端吐的 JSON 坏了、网络断了，处理方式完全不同 ——
/// 400/401 要用户去改配置，429/5xx 可以重试，JSON 坏了是服务端或协议不兼容，网络断了重试即可。
/// <b>不裸崩、不漏原始 <c>HttpRequestException</c></b>。
/// </remarks>
public class ProviderException : Exception
{
    /// <summary>建一个 Provider 异常。</summary>
    public ProviderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>配置不完整或格式不对（没填 base URL、模型名为空）。这是用户该去设置页修的问题。</summary>
public sealed class ProviderConfigurationException : ProviderException
{
    /// <summary>建一个配置异常。</summary>
    public ProviderConfigurationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>服务端返回非 2xx。带上状态码和响应体，界面可以直接显示排查线索。</summary>
public sealed class ProviderHttpException : ProviderException
{
    /// <summary>建一个 HTTP 异常。</summary>
    public ProviderHttpException(HttpStatusCode statusCode, string responseBody, string requestUrl, Exception? innerException = null)
        : base(BuildMessage(statusCode, responseBody, requestUrl), innerException)
    {
        StatusCode = (int)statusCode;
        StatusCodeName = statusCode.ToString();
        ResponseBody = responseBody;
        RequestUrl = requestUrl;
    }

    /// <summary>HTTP 状态码，例如 400 / 401 / 429 / 500。</summary>
    public int StatusCode { get; }

    /// <summary>状态码的名字，例如 <c>BadRequest</c>。</summary>
    public string StatusCodeName { get; }

    /// <summary>响应体（已截断），通常含服务端的错误说明。</summary>
    public string ResponseBody { get; }

    /// <summary>请求地址（不含密钥）。</summary>
    public string RequestUrl { get; }

    /// <summary>重试有没有意义：429 和 5xx 有意义，其余（4xx）要先改配置/改请求。</summary>
    public bool IsRetryable => StatusCode == 429 || StatusCode >= 500;

    /// <summary>密钥/鉴权问题，界面该直接引导去设置页。</summary>
    public bool IsAuthFailure => StatusCode is 401 or 403;

    private static string BuildMessage(HttpStatusCode statusCode, string responseBody, string requestUrl)
    {
        var body = responseBody ?? string.Empty;
        if (body.Length > 500)
        {
            body = body[..500] + "…";
        }

        return $"HTTP {(int)statusCode} ({statusCode}) from {requestUrl}: {body}";
    }
}

/// <summary>连不上 / 连接中途断了（包括服务端流到一半关掉连接）。</summary>
public sealed class ProviderNetworkException : ProviderException
{
    /// <summary>建一个网络异常。</summary>
    public ProviderNetworkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>超时：HttpClient 自己的超时，或读流读到一半卡死。</summary>
public sealed class ProviderTimeoutException : ProviderException
{
    /// <summary>建一个超时异常。</summary>
    public ProviderTimeoutException(string message, TimeSpan? timeout = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Timeout = timeout;
    }

    /// <summary>超时阈值（拿得到的话）。</summary>
    public TimeSpan? Timeout { get; }
}

/// <summary>协议层坏了：SSE 的 payload 不是合法 JSON、结构不是预期的形状。</summary>
public sealed class ProviderProtocolException : ProviderException
{
    /// <summary>建一个协议异常。</summary>
    public ProviderProtocolException(string message, string? rawPayload = null, Exception? innerException = null)
        : base(message, innerException)
    {
        RawPayload = Truncate(rawPayload);
    }

    /// <summary>出问题的原始数据（已截断），排查协议不兼容时最关键的东西。</summary>
    public string? RawPayload { get; }

    private static string? Truncate(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return payload;
        }

        return payload.Length <= 500 ? payload : payload[..500] + "…";
    }
}

/// <summary>
/// 服务端在 HTTP 200 的流里塞了个错误对象（<c>{"error":{...}}</c>）—— 常见于限额、上下文超长。
/// 和 <see cref="ProviderHttpException"/> 分开，因为"HTTP 是成功的但内容失败了"排查思路不一样。
/// </summary>
public sealed class ProviderApiException : ProviderException
{
    /// <summary>建一个"流内错误"异常。</summary>
    public ProviderApiException(string message, string? code = null, string? type = null, string? rawPayload = null)
        : base(message)
    {
        Code = code;
        ErrorType = type;
        RawPayload = rawPayload;
    }

    /// <summary>服务端错误码（如 <c>insufficient_quota</c>）。</summary>
    public string? Code { get; }

    /// <summary>服务端错误类型。</summary>
    public string? ErrorType { get; }

    /// <summary>原始 payload。</summary>
    public string? RawPayload { get; }
}
