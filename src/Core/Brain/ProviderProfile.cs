using System.Text.Json.Serialization;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// 一套模型接入档案（profile）：用户自己填的 base URL + 密钥 + 模型名 + 采样参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>序列化规则（重要）</b>：<see cref="ApiKey"/> 标了 <see cref="JsonIgnoreAttribute"/>，
/// 永远不会落盘；落盘的是 <see cref="ApiKeyProtected"/>（DPAPI 密文，只在本机本用户可解）。
/// 序列化/反序列化请一律走 <see cref="ConfigStore"/>，不要自己 <c>JsonSerializer.Serialize(profile)</c> 到磁盘。
/// </para>
/// </remarks>
public sealed class ProviderProfile
{
    /// <summary>档案名，界面下拉框显示这个。默认 <c>default</c>。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    /// <summary>
    /// 接口根地址，例如 <c>https://api.deepseek.com</c> 或 <c>https://api.openai.com</c>。
    /// 末尾带不带 <c>/</c>、带不带 <c>/v1</c> 都行，<see cref="OpenAiProvider"/> 会补成
    /// <c>{base}/v1/chat/completions</c>。
    /// </summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>模型名，例如 <c>deepseek-chat</c> / <c>gpt-4o-mini</c>。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>采样温度，0~2。null 表示不发给服务端（用服务端默认值）。</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; } = 0.7;

    /// <summary>单次回复的 token 上限。null 或 &lt;= 0 表示不发给服务端。</summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; set; } = 2048;

    /// <summary>
    /// 推理强度，原样发给服务端的 <c>reasoning_effort</c> 字段（<c>low</c> / <c>medium</c> / <c>high</c> / <c>max</c> …）。
    /// <b>null 或空 = 这个字段一个都不发</b>（默认：服务端不认它就 400，所以不敢默认发）。
    /// </summary>
    /// <remarks>
    /// 刻意不做取值校验：各家服务端认的词不一样（有的只认 low/medium/high，有的另有 max / minimal），
    /// 写错了由服务端报错、我们原样转达，比在本地瞎猜一个白名单更诚实。
    /// </remarks>
    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// 上一次从服务端拉到的模型名列表（<c>GET /v1/models</c> 的缓存，给界面上的模型下拉框用）。
    /// null 或空 = 还没拉过 —— 这时候下拉框只显示 <see cref="Model"/> 当前填的那一个。
    /// </summary>
    /// <remarks>缓存的意义是"不联网也能看到列表"：拉取要密钥 + 网络，启动时不该卡在这上面。</remarks>
    [JsonPropertyName("knownModels")]
    public List<string>? KnownModels { get; set; }

    /// <summary>
    /// 密钥明文，<b>只存在于内存</b>，永远不写进配置文件。
    /// 读盘时由 <see cref="ConfigStore"/> 从 <see cref="ApiKeyProtected"/> 解出来。
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>DPAPI 加密后的密钥（base64，带 <c>dpapi:v1:</c> 前缀）。磁盘上只有这个字段。</summary>
    [JsonPropertyName("apiKeyProtected")]
    public string? ApiKeyProtected { get; set; }

    /// <summary>是否已经有一把钥匙（不管解没解开）。</summary>
    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    /// <summary>是否有密文但没能解开（换了机器/用户，或者文件被手改坏了）。</summary>
    [JsonIgnore]
    public bool HasUndecryptableKey => string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(ApiKeyProtected);

    /// <summary>配置是否够跑一次请求（地址和模型都填了）。密钥缺失单独报，好让界面提示得准。</summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);

    /// <summary>缺什么，返回人话；齐全返回 null。给设置页做提示用。</summary>
    public string? DescribeMissing()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            return "Base URL is not set.";
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            return "Model name is not set.";
        }

        if (string.IsNullOrEmpty(ApiKey))
        {
            return HasUndecryptableKey
                ? "API key could not be decrypted on this machine/user. Please re-enter it."
                : "API key is not set.";
        }

        return null;
    }

    /// <summary>深拷贝。拷贝里带着明文密钥，注意别写到日志里。</summary>
    public ProviderProfile Clone() => new()
    {
        Name = Name,
        BaseUrl = BaseUrl,
        Model = Model,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        ReasoningEffort = ReasoningEffort,
        KnownModels = KnownModels is null ? null : new List<string>(KnownModels),
        ApiKey = ApiKey,
        ApiKeyProtected = ApiKeyProtected,
    };

    /// <summary>脱敏描述，可以安全进日志和界面。</summary>
    public override string ToString()
    {
        var keyState = HasApiKey ? "key=" + Mask(ApiKey) : (HasUndecryptableKey ? "key=<undecryptable>" : "key=<none>");
        return $"{Name}: {BaseUrl} / {Model} / temp={Temperature?.ToString() ?? "-"} / maxTokens={MaxTokens?.ToString() ?? "-"} / " +
               $"reasoning={ReasoningEffort ?? "-"} / {keyState}";
    }

    /// <summary>只留头尾，中间打码。</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "<none>";
        }

        if (secret.Length <= 8)
        {
            return new string('*', secret.Length);
        }

        return secret[..4] + new string('*', Math.Min(8, secret.Length - 8)) + secret[^4..];
    }
}
