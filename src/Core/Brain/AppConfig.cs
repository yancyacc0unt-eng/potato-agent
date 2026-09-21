using System.Text.Json.Serialization;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.Brain;

/// <summary>
/// <c>%APPDATA%\PotatoAgent\config.json</c> 的整体结构：多套 profile + 当前用哪套。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置文件格式版本，将来迁移用。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>当前选中的 profile 名。</summary>
    [JsonPropertyName("activeProfile")]
    public string ActiveProfileName { get; set; } = "default";

    /// <summary>
    /// 工具权限档位在配置里的写法（<c>"basic"</c> / <c>"advanced"</c>，见 <see cref="ToolApprovalModeNames"/>）。
    /// 刻意存字符串而不是枚举：这份文件是给人看的、也可能被人手改，拼错一个词只该退回默认档，
    /// 不该因为一个 JsonException 把整份配置判成损坏。
    /// </summary>
    [JsonPropertyName("toolApprovalMode")]
    public string ToolApprovalModeName { get; set; } = ToolApprovalModeNames.Basic;

    /// <summary>
    /// 工具权限档位（解析 <see cref="ToolApprovalModeName"/> 得来；认不出来一律当 <see cref="ToolApprovalMode.Basic"/>）。
    /// </summary>
    [JsonIgnore]
    public ToolApprovalMode ApprovalMode
    {
        get => ToolApprovalModeNames.Parse(ToolApprovalModeName);
        set => ToolApprovalModeName = ToolApprovalModeNames.ToName(value);
    }

    /// <summary>全部档案。</summary>
    [JsonPropertyName("profiles")]
    public List<ProviderProfile> Profiles { get; set; } = new();

    /// <summary>当前生效的档案：找不到同名就退到第一套；一套都没有则 null。</summary>
    [JsonIgnore]
    public ProviderProfile? ActiveProfile
    {
        get
        {
            var byName = Find(ActiveProfileName);
            return byName ?? (Profiles.Count > 0 ? Profiles[0] : null);
        }
    }

    /// <summary>按名找档案，找不到返回 null。</summary>
    public ProviderProfile? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var profile in Profiles)
        {
            if (string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>新增或覆盖（按名字匹配，忽略大小写）。返回被加入/更新的那一套。</summary>
    public ProviderProfile AddOrUpdate(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var existing = Find(profile.Name);
        if (existing is null)
        {
            Profiles.Add(profile);
            return profile;
        }

        existing.BaseUrl = profile.BaseUrl;
        existing.Model = profile.Model;
        existing.Temperature = profile.Temperature;
        existing.MaxTokens = profile.MaxTokens;
        existing.ReasoningEffort = profile.ReasoningEffort;

        // 模型名缓存同理：设置页不编辑它，别把用户拉到的列表擦掉。
        if (profile.KnownModels is not null)
        {
            existing.KnownModels = profile.KnownModels;
        }

        // 明文为 null = "这次不动密钥"，交给调用方明确地用空串表示"清掉"。
        if (profile.ApiKey is not null)
        {
            existing.ApiKey = profile.ApiKey;
            existing.ApiKeyProtected = profile.ApiKeyProtected;
        }

        return existing;
    }

    /// <summary>删掉一套档案；删的是当前那套时，自动把当前指针挪到剩下的第一套。</summary>
    public bool Remove(string name)
    {
        var existing = Find(name);
        if (existing is null)
        {
            return false;
        }

        Profiles.Remove(existing);

        if (string.Equals(ActiveProfileName, existing.Name, StringComparison.OrdinalIgnoreCase))
        {
            ActiveProfileName = Profiles.Count > 0 ? Profiles[0].Name : string.Empty;
        }

        return true;
    }

    /// <summary>空配置（文件缺失或损坏时的降级结果）。</summary>
    public static AppConfig CreateEmpty() => new()
    {
        Version = 1,
        ActiveProfileName = string.Empty,
        Profiles = new List<ProviderProfile>(),
    };

    /// <summary>脱敏 JSON，给"看一眼当前配置"用，不含任何明文密钥。</summary>
    public string ToRedactedJson()
    {
        var lines = new List<string>
        {
            "{",
            $"  \"version\": {Version},",
            $"  \"activeProfile\": \"{ActiveProfileName}\",",
            $"  \"toolApprovalMode\": \"{ToolApprovalModeName}\",",
            "  \"profiles\": [",
        };

        for (var i = 0; i < Profiles.Count; i++)
        {
            var p = Profiles[i];
            var comma = i == Profiles.Count - 1 ? string.Empty : ",";
            lines.Add($"    {{ \"name\": \"{p.Name}\", \"baseUrl\": \"{p.BaseUrl}\", \"model\": \"{p.Model}\", " +
                      $"\"temperature\": {p.Temperature?.ToString() ?? "null"}, \"maxTokens\": {p.MaxTokens?.ToString() ?? "null"}, " +
                      $"\"reasoningEffort\": \"{p.ReasoningEffort ?? string.Empty}\", " +
                      $"\"apiKey\": \"{ProviderProfile.Mask(p.ApiKey)}\" }}{comma}");
        }

        lines.Add("  ]");
        lines.Add("}");
        return string.Join(Environment.NewLine, lines);
    }
}
