using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PotatoAgent.Core.Brain;

/// <summary>配置读盘的结果，给界面判断要不要提示用户。</summary>
public enum ConfigLoadStatus
{
    /// <summary>正常读到了。</summary>
    Ok = 0,

    /// <summary>文件不存在（首次运行），按空配置处理。</summary>
    Missing = 1,

    /// <summary>文件存在但不是合法 JSON / 结构不对，按空配置处理（<b>不</b>覆盖原文件，留给用户自己修）。</summary>
    Corrupt = 2,

    /// <summary>配置读到了，但里面有密钥解不开（换了机器或换了用户）。</summary>
    KeyUndecryptable = 3,

    /// <summary>文件读不动（权限/占用），按空配置处理。</summary>
    Unreadable = 4,
}

/// <summary>
/// <c>%APPDATA%\PotatoAgent\config.json</c> 的读写：多套 profile、密钥 DPAPI 加密、坏文件安全降级。
/// </summary>
/// <remarks>
/// <para><b>永不抛异常</b>是这张表的核心承诺：<see cref="Load"/> 遇到任何问题都退化成空配置并记录
/// <see cref="LastLoadStatus"/>。<see cref="Save"/> 才可能抛（磁盘满了这种没办法装作没事），
/// 但调用方通常在设置页，可以弹框。</para>
/// <para><b>明文密钥绝不落盘</b>：<see cref="Save"/> 里除了靠 <see cref="ProviderProfile.ApiKey"/> 上的
/// <c>[JsonIgnore]</c>，还会在写盘前<b>再查一遍</b>序列化结果里有没有明文密钥，发现就拒绝写入（自检防线）。</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private readonly ISecretProtector _protector;

    /// <summary>
    /// 建一个配置仓库。<paramref name="filePath"/> 为 null 时用默认位置
    /// <c>%APPDATA%\PotatoAgent\config.json</c>（测试可以指到临时目录，绝不碰用户真实配置）。
    /// </summary>
    public ConfigStore(string? filePath = null, ISecretProtector? protector = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath!;
        _protector = protector ?? new DpapiSecretProtector();
        Current = AppConfig.CreateEmpty();
    }

    /// <summary>配置目录：<c>%APPDATA%\PotatoAgent</c>。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PotatoAgent");

    /// <summary>配置文件全路径：<c>%APPDATA%\PotatoAgent\config.json</c>。</summary>
    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "config.json");

    /// <summary>本实例用的配置文件路径。</summary>
    public string FilePath { get; }

    /// <summary>内存里的当前配置。构造后先调 <see cref="Load"/>。</summary>
    public AppConfig Current { get; private set; }

    /// <summary>上次读盘的结果。</summary>
    public ConfigLoadStatus LastLoadStatus { get; private set; } = ConfigLoadStatus.Missing;

    /// <summary>上次读盘失败的原因（失败才有值），可以进日志。</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>当前生效的档案（可能是 null：一套都没有）。</summary>
    public ProviderProfile? ActiveProfile => Current.ActiveProfile;

    /// <summary>
    /// 读配置。<b>任何异常都不往外抛</b>：文件缺失/损坏/读不动 → 空配置；密钥解不开 → 保留档案但密钥为空。
    /// </summary>
    public AppConfig Load()
    {
        lock (_gate)
        {
            LastLoadError = null;

            if (!File.Exists(FilePath))
            {
                Current = AppConfig.CreateEmpty();
                LastLoadStatus = ConfigLoadStatus.Missing;
                return Current;
            }

            string text;
            try
            {
                text = File.ReadAllText(FilePath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Current = AppConfig.CreateEmpty();
                LastLoadStatus = ConfigLoadStatus.Unreadable;
                LastLoadError = $"{ex.GetType().Name}: {ex.Message}";
                return Current;
            }

            AppConfig? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AppConfig>(text, ReadOptions);
            }
            catch (JsonException ex)
            {
                Current = AppConfig.CreateEmpty();
                LastLoadStatus = ConfigLoadStatus.Corrupt;
                LastLoadError = $"Invalid JSON: {ex.Message}";
                return Current;
            }

            if (parsed is null)
            {
                Current = AppConfig.CreateEmpty();
                LastLoadStatus = ConfigLoadStatus.Corrupt;
                LastLoadError = "Config file contains JSON null.";
                return Current;
            }

            Normalize(parsed);

            // 解密钥。解不开不算致命：档案还在，只是这次用不了。
            var undecryptable = 0;
            foreach (var profile in parsed.Profiles)
            {
                if (string.IsNullOrEmpty(profile.ApiKeyProtected))
                {
                    profile.ApiKey = null;
                    continue;
                }

                profile.ApiKey = _protector.TryUnprotect(profile.ApiKeyProtected);
                if (profile.ApiKey is null)
                {
                    undecryptable++;
                }
            }

            Current = parsed;
            LastLoadStatus = undecryptable > 0 ? ConfigLoadStatus.KeyUndecryptable : ConfigLoadStatus.Ok;
            if (undecryptable > 0)
            {
                LastLoadError = $"{undecryptable} profile(s) have an api key that cannot be decrypted on this machine/user.";
            }

            return Current;
        }
    }

    /// <summary>
    /// 写配置：先加密密钥（内存里的明文不会进 JSON），再原子替换文件（写 .tmp 后 move，中途崩不会留下半截文件）。
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            var json = SerializeForDisk(Current);

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
        }
    }

    /// <summary>切换当前 profile；名字不存在返回 false（不改动任何东西）。</summary>
    public bool SetActiveProfile(string name)
    {
        lock (_gate)
        {
            if (Current.Find(name) is null)
            {
                return false;
            }

            Current.ActiveProfileName = Current.Find(name)!.Name;
            return true;
        }
    }

    /// <summary>新增/覆盖一套档案并设为当前。密钥以明文传进来，落盘时自动加密。</summary>
    public ProviderProfile UpsertProfile(ProviderProfile profile, bool makeActive = true)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = "default";
            }

            var stored = Current.AddOrUpdate(profile);
            if (makeActive)
            {
                Current.ActiveProfileName = stored.Name;
            }

            return stored;
        }
    }

    /// <summary>删掉一套档案；被删的是当前那套时会自动切到剩下的第一套。</summary>
    public bool RemoveProfile(string name)
    {
        lock (_gate)
        {
            return Current.Remove(name);
        }
    }

    /// <summary>当前配置的脱敏 JSON（给"看一眼配置"用，不含明文密钥）。</summary>
    public string ToRedactedJson()
    {
        lock (_gate)
        {
            return Current.ToRedactedJson();
        }
    }

    /// <summary>序列化 + 加密 + 明文自检。返回真正要写盘的 JSON。</summary>
    private string SerializeForDisk(AppConfig config)
    {
        var secrets = new List<string>();
        foreach (var profile in config.Profiles)
        {
            if (!string.IsNullOrEmpty(profile.ApiKey))
            {
                profile.ApiKeyProtected = _protector.Protect(profile.ApiKey);
                if (profile.ApiKey.Length >= 6)
                {
                    secrets.Add(profile.ApiKey);
                }
            }
            else if (profile.ApiKey is { Length: 0 })
            {
                // 显式清空密钥。
                profile.ApiKeyProtected = null;
            }

            // ApiKey == null：这次不动密文（Clone/AddOrUpdate 传 null 的场景）。
        }

        var json = JsonSerializer.Serialize(config, WriteOptions);

        foreach (var secret in secrets)
        {
            if (json.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to write config: the serialized JSON still contains a plaintext API key.");
            }
        }

        return json;
    }

    /// <summary>规范化：名字补全去重、至少保证结构可用。</summary>
    private static void Normalize(AppConfig config)
    {
        config.Profiles ??= new List<ProviderProfile>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<ProviderProfile>(config.Profiles.Count);
        var index = 1;

        foreach (var profile in config.Profiles)
        {
            if (profile is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = $"profile{index}";
            }

            while (!seen.Add(profile.Name))
            {
                profile.Name = $"{profile.Name}_{++index}";
            }

            kept.Add(profile);
            index++;
        }

        config.Profiles = kept;
        config.Version = config.Version <= 0 ? 1 : config.Version;

        if (string.IsNullOrWhiteSpace(config.ActiveProfileName))
        {
            config.ActiveProfileName = kept.Count > 0 ? kept[0].Name : string.Empty;
        }
    }
}
