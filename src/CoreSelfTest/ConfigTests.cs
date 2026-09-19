// ---------- 四、配置与密钥 ----------
//
// 三条硬承诺，逐条实测：
//   1) 写进去 → 读回来，逐字符一致（含中文、重音符号、emoji）；
//   2) 磁盘上【搜不到明文密钥】——原文、base64、URL-safe base64、前后片段全都搜一遍；
//   3) 文件损坏 / 缺失 / 密钥解不开时安全降级，绝不崩、绝不覆盖用户的坏文件。
//
// ⚠ 全程只用 %TEMP% 下的临时目录 + 注入的假加解密器，绝不读也不写 %APPDATA%\PotatoAgent。
//   真实 DPAPI 只在本机被支持时顺带验一下（不支持就如实报出来，不当失败）。

using System.Text;
using PotatoAgent.Core.Brain;

namespace PotatoAgent.Core.SelfTest;

internal static class ConfigTests
{
    /// <summary>假密钥：跟 Program.FakeKey 一样是现编的，理由见那边注释。</summary>
    private const string Secret = "s" + "k-selftest-" + "9F3aQ7xLm2VpR8tYw1ZbN4jH";

    /// <summary>带中文、重音符号和 emoji 的地址，专门用来验"逐字符一致"。</summary>
    private const string ZigzagUrl = "https://接口.example.com/v1?名字=土豆é😀&模式=测试";

    public static void Run()
    {
        Console.WriteLine("---- 四、配置与密钥 ----");

        var root = Path.Combine(Path.GetTempPath(), "potato-agent-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var protector = new FakeSecretProtector { Mode = FakeSecretProtector.Modes.Reversible };

        try
        {
            Directory.CreateDirectory(root);
            Console.WriteLine($"    临时目录: {root}");

            RoundTrip(root, protector);
            NoPlaintextOnDisk(root, protector);
            SecretIsEncrypted(root, protector);
            SaveSelfCheckRefusesPlaintext(root);
            GracefulDegradation(root, protector);
            DeletedAndMultiProfile(root, protector);
            RealDpapi();
        }
        catch (Exception error)
        {
            Program.Fail($"配置测试自身抛异常: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception error)
            {
                Console.WriteLine($"    ⚠ 临时目录没删干净（不影响结论）: {error.GetType().Name}");
            }
        }

        Console.WriteLine();
    }

    // ==================== 4.1 写 → 读回，逐字符一致 ====================

    private static void RoundTrip(string root, ISecretProtector protector)
    {
        Console.WriteLine("  4.1 写一条含密钥的配置 → 读回来逐字符一致");

        var path = Path.Combine(root, "roundtrip", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();

        var profile = new ProviderProfile
        {
            Name = "测试档案",
            BaseUrl = ZigzagUrl,
            Model = "deepseek-chat",
            ApiKey = Secret,
            Temperature = 0.375,
            MaxTokens = 4096,
        };

        store.UpsertProfile(profile);
        store.Save();

        var reloaded = new ConfigStore(path, protector);
        var config = reloaded.Load();

        Expect.Equal(ConfigLoadStatus.Ok, reloaded.LastLoadStatus, $"重新读盘成功（{reloaded.LastLoadStatus}）");
        Expect.Equal(1, config.Profiles.Count, "档案数量对得上");

        var read = config.ActiveProfile;
        Expect.True(read is not null, "能取到当前档案");
        if (read is null)
        {
            return;
        }

        Expect.Equal("测试档案", read.Name, "档案名逐字符一致");
        Expect.Equal("deepseek-chat", read.Model, "模型名一致");
        Expect.Equal(ZigzagUrl, read.BaseUrl, "baseUrl 逐字符一致（中文 / 重音 / emoji 都没坏）");
        Expect.Equal(0.375, read.Temperature ?? double.NaN, "temperature 一致");
        Expect.Equal(4096, read.MaxTokens ?? 0, "maxTokens 一致");
        Expect.Equal(Secret, read.ApiKey, "密钥解密回来逐字符一致");
        Expect.True(read.IsUsable, "读回来的档案是可用的（地址+模型+密钥齐全）");
        Expect.True(read.DescribeMissing() is null, "DescribeMissing() 返回 null（什么都不缺）");

        Console.WriteLine();
    }

    // ==================== 4.2 磁盘上搜不到明文密钥 ====================

    private static void NoPlaintextOnDisk(string root, ISecretProtector protector)
    {
        Console.WriteLine("  4.2 磁盘文件里搜不到明文密钥（原文 / base64 / 前后片段）");

        var path = Path.Combine(root, "noplaintext", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "leak-check",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = Secret,
        });
        store.Save();

        var raw = File.ReadAllText(path, Encoding.UTF8);

        Expect.DoesNotContain(raw, Secret, "搜不到密钥原文");
        Expect.DoesNotContain(raw, Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), "搜不到标准 base64");
        Expect.DoesNotContain(raw, Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)).Replace('+', '-').Replace('/', '_'), "搜不到 URL-safe base64");
        Expect.DoesNotContain(raw, Secret.Substring(0, 8), "搜不到密钥前 8 个字符");
        Expect.DoesNotContain(raw, Secret.Substring(Secret.Length - 8), "搜不到密钥后 8 个字符");
        Expect.DoesNotContain(raw, Secret.Substring(4, 12), "搜不到密钥中间一段");
        Expect.Contains(raw, FakeSecretProtector.Prefix, "磁盘上只留密文信封（带算法版本前缀）");
        Expect.Contains(raw, "\"apiKeyProtected\"", "落盘字段是 apiKeyProtected，不是 apiKey");
        Expect.DoesNotContain(raw, "\"apiKey\"", "文件里没有 apiKey 这个明文字段");

        Console.WriteLine($"    文件大小 {raw.Length} 字符，密文前缀 {FakeSecretProtector.Prefix}，明文 0 处命中");
        Console.WriteLine($"    密钥掩码（可以进日志的样子）: {ProviderProfile.Mask(Secret)}");
        Console.WriteLine();
    }

    // ==================== 4.3 密钥真的被加密过 ====================

    private static void SecretIsEncrypted(string root, ISecretProtector protector)
    {
        Console.WriteLine("  4.3 落盘的是密文而不是绕了一圈的明文");

        var path = Path.Combine(root, "cipher", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "cipher",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = Secret,
        });
        store.Save();

        var raw = File.ReadAllText(path, Encoding.UTF8);
        var envelope = ExtractEnvelope(raw);

        Expect.True(envelope is not null, "文件里找到了 apiKeyProtected 的值");
        Expect.DoesNotContain(envelope ?? string.Empty, Secret, "密文里不含明文");
        Expect.Equal(Secret, protector.TryUnprotect(envelope), "拿密文能解回原文（说明确实经过了加解密这一环）");

        Console.WriteLine();
    }

    // ==================== 4.4 写盘前的明文自检 ====================

    private static void SaveSelfCheckRefusesPlaintext(string root)
    {
        Console.WriteLine("  4.4 写盘前的明文自检（加密器坏成「原样返回」时必须拒绝写盘）");

        var path = Path.Combine(root, "guard", "config.json");
        var store = new ConfigStore(path, new FakeSecretProtector { Mode = FakeSecretProtector.Modes.Identity });
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "guard",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = Secret,
        });

        var error = Catch.Of(() =>
        {
            store.Save();
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();

        Expect.ExceptionType<InvalidOperationException>(error, "序列化结果里还有明文密钥 → 拒绝写盘并抛异常");
        Expect.True(!File.Exists(path), "拒绝之后就【没有】留下任何文件");

        Console.WriteLine();
    }

    // ==================== 4.5 坏文件安全降级 ====================

    private static void GracefulDegradation(string root, ISecretProtector protector)
    {
        Console.WriteLine("  4.5 文件损坏 / 结构不对 / 密钥解不开 → 安全降级不崩");

        // (a) 文件不存在
        var missing = new ConfigStore(Path.Combine(root, "missing", "config.json"), protector);
        var missingConfig = missing.Load();
        Expect.Equal(ConfigLoadStatus.Missing, missing.LastLoadStatus, "文件不存在 → Missing");
        Expect.Equal(0, missingConfig.Profiles.Count, "文件不存在 → 空配置，不崩");

        // (b) 各种坏内容
        CheckCorrupt(root, protector, "全是乱码.json", "这不是 JSON {{{", "纯乱码");
        CheckCorrupt(root, protector, "半截.json", "{\"version\": 1, \"profiles\": [", "截断的 JSON");
        CheckCorrupt(root, protector, "null.json", "null", "内容是 JSON null");
        CheckCorrupt(root, protector, "数组.json", "[1, 2, 3]", "顶层是数组不是对象");
        CheckCorrupt(root, protector, "空文件.json", string.Empty, "空文件");

        // (c) 密钥解不开（换了机器 / 换了用户 / 密文被手改坏）
        var dllPath = Path.Combine(root, "badkey", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        File.WriteAllText(dllPath,
            "{\"version\":1,\"activeProfile\":\"bad\",\"profiles\":[{\"name\":\"bad\"," +
            "\"baseUrl\":\"https://api.example.com\",\"model\":\"fake-model\"," +
            "\"apiKeyProtected\":\"" + FakeSecretProtector.Prefix + "bm90LWEtcmVhbC1jaXBoZXI=\"}]}",
            new UTF8Encoding(false));

        var badKey = new ConfigStore(dllPath, new FakeSecretProtector { Mode = FakeSecretProtector.Modes.Null });
        var badConfig = badKey.Load();
        Expect.Equal(ConfigLoadStatus.KeyUndecryptable, badKey.LastLoadStatus, "密钥解不开 → KeyUndecryptable");
        Expect.True(badKey.LastLoadError is not null, "给出一句人可以看的 LastLoadError");
        Expect.Equal(1, badConfig.Profiles.Count, "档案本身没丢（只是这次用不了）");
        Expect.True(badConfig.ActiveProfile?.ApiKey is null, "解不开时 ApiKey 降级成 null，而不是乱码");
        Expect.True(badConfig.ActiveProfile?.HasUndecryptableKey == true, "档案自己知道「有密文但解不开」");
        Expect.Contains(badConfig.ActiveProfile?.DescribeMissing() ?? string.Empty, "could not be decrypted",
            "DescribeMissing() 给的是「重新填一次密钥」，不是「没填」");
        Expect.True(badConfig.ActiveProfile?.IsUsable == true, "地址和模型还在，所以档案本身仍算可用");

        // (d) 有人把明文密钥手写进 config.json：绝不能当成有效密钥使用
        var handEdited = Path.Combine(root, "handwritten", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(handEdited)!);
        File.WriteAllText(handEdited,
            "{\"version\":1,\"activeProfile\":\"hw\",\"profiles\":[{\"name\":\"hw\"," +
            "\"baseUrl\":\"https://api.example.com\",\"model\":\"fake-model\"," +
            "\"apiKeyProtected\":\"" + Secret + "\"}]}",
            new UTF8Encoding(false));

        var handwritten = new ConfigStore(handEdited, new DpapiSecretProtector());
        var handwrittenConfig = handwritten.Load();
        Expect.True(handwrittenConfig.ActiveProfile?.ApiKey is null,
            "手写进文件的明文密钥被拒绝（没有 dpapi:v1: 前缀就当解不开）");
        Expect.Equal(ConfigLoadStatus.KeyUndecryptable, handwritten.LastLoadStatus, "这种手改也归到 KeyUndecryptable");

        Console.WriteLine();
    }

    private static void CheckCorrupt(string root, ISecretProtector protector, string fileName, string content, string what)
    {
        var directory = Path.Combine(root, "corrupt");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));

        var store = new ConfigStore(path, protector);
        var config = store.Load();

        var ok = store.LastLoadStatus == ConfigLoadStatus.Corrupt && config.Profiles.Count == 0 && store.LastLoadError is not null;
        Program.Check(ok, ok
            ? $"{what} → Corrupt + 空配置 + 有 LastLoadError"
            : $"{what} → 期望 Corrupt/空配置，实际 {store.LastLoadStatus}/{config.Profiles.Count} 个档案");

        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) != content)
        {
            Program.Fail($"{what} —— 读盘把用户的坏文件覆盖了（绝不能动它）");
        }
    }

    // ==================== 4.6 删文件 / 多档案混合 ====================

    private static void DeletedAndMultiProfile(string root, ISecretProtector good)
    {
        Console.WriteLine("  4.6 密钥被删掉 / 多档案里只有一套解不开");

        // (a) 删掉密钥：内存里的明文一定没了，落盘的密文字段也清掉
        var path = Path.Combine(root, "deleted", "config.json");
        var store = new ConfigStore(path, good);
        store.Load();
        store.Save();
        Expect.Equal(ConfigLoadStatus.Missing, store.LastLoadStatus, "还没保存过时是 Missing");

        store.UpsertProfile(new ProviderProfile
        {
            Name = "gone",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = Secret,
        });
        store.Save();

        var cleared = new ProviderProfile
        {
            Name = "gone",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = string.Empty,   // 空串 = 明确清掉密钥
        };
        store.UpsertProfile(cleared, makeActive: false);
        store.Save();

        var afterDelete = new ConfigStore(path, good);
        afterDelete.Load();
        Expect.True(afterDelete.ActiveProfile?.ApiKey is null, "删掉密钥后再读回来就是没有密钥（不会把旧密文留着）");
        Expect.True(afterDelete.ActiveProfile?.ApiKeyProtected is null, "磁盘上的密文字段也被清掉了");

        // (b) 两套档案，其中一套解不开
        var mixedPath = Path.Combine(root, "mixed", "config.json");
        var goodOne = new ConfigStore(mixedPath, good);
        goodOne.Load();
        goodOne.UpsertProfile(new ProviderProfile
        {
            Name = "good",
            BaseUrl = "https://api.example.com",
            Model = "fake-model",
            ApiKey = Secret,
        });
        goodOne.UpsertProfile(new ProviderProfile
        {
            Name = "broken",
            BaseUrl = "https://api2.example.com",
            Model = "fake-model-2",
            ApiKey = Secret + "-broken",
        }, makeActive: false);
        goodOne.Save();

        // 先老实读一遍，把 "broken" 那套真正落到磁盘上的密文抠出来 —— 待会儿专门让它解不开。
        var plain = new ConfigStore(mixedPath, good);
        var plainConfig = plain.Load();
        var brokenEnvelope = plainConfig.Find("broken")?.ApiKeyProtected;
        Expect.True(brokenEnvelope is not null, "先取到 broken 那套的密文（好精确模拟「换了机器解不开」）");

        var mixedProtector = new FakeSecretProtector { Mode = FakeSecretProtector.Modes.FailForBroken };
        if (brokenEnvelope is not null)
        {
            mixedProtector.BreakEnvelope(brokenEnvelope);
        }

        var mixed = new ConfigStore(mixedPath, mixedProtector);
        var mixedConfig = mixed.Load();
        Expect.Equal(ConfigLoadStatus.KeyUndecryptable, mixed.LastLoadStatus, "有一套解不开 → 整体状态标成 KeyUndecryptable");
        Expect.Equal(2, mixedConfig.Profiles.Count, "两套档案都还在");
        Expect.True(mixedConfig.Find("good")?.ApiKey == Secret, "能解开的那套照常可用");
        Expect.True(mixedConfig.Find("broken")?.ApiKey is null, "解不开的那套单独降级，不影响另一套");

        Console.WriteLine();
    }

    // ==================== 4.7 真实 DPAPI ====================

    private static void RealDpapi()
    {
        Console.WriteLine("  4.7 真实 DPAPI（本机 Windows 才跑，不支持就如实报出来）");

        ISecretProtector? protector = null;
        try
        {
            protector = new DpapiSecretProtector();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.WriteLine($"    ⚠ 本机不支持 DPAPI，跳过: {ex.Message}");
            return;
        }

        try
        {
            var envelope = protector.Protect(Secret);
            Expect.True(envelope.StartsWith(DpapiSecretProtector.Prefix, StringComparison.Ordinal),
                "密文带 dpapi:v1: 前缀（算法版本，将来换算法好迁移）");
            Expect.DoesNotContain(envelope, Secret, "真实 DPAPI 密文里也不含明文");
            Expect.Equal(Secret, protector.TryUnprotect(envelope), "真实 DPAPI 解得回来");

            Expect.True(protector.TryUnprotect(DpapiSecretProtector.Prefix + "bm90LWEtcmVhbC1jaXBoZXI=") is null,
                "假密文解不开时返回 null（不抛异常）");
            Expect.True(protector.TryUnprotect("明文塞进来") is null, "没有前缀的值一律拒绝解析");
            Expect.True(protector.TryUnprotect(null) is null, "null 进去 null 出来");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or TypeInitializationException)
        {
            Console.WriteLine($"    ⚠ 本机 DPAPI 不可用，跳过: {ex.GetType().Name}");
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>从配置 JSON 里把 apiKeyProtected 的值抠出来。</summary>
    private static string? ExtractEnvelope(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("profiles", out var profiles) &&
            profiles.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                if (profile.TryGetProperty("apiKeyProtected", out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }

        return null;
    }
}

/// <summary>
/// 假加解密器：让配置测试不必依赖 DPAPI，同时能人为制造"解不开""加密器坏掉"这些场景。
/// 变换本身是可逆的 XOR（不是"base64 一下"就完事），所以"磁盘上搜不到明文 / base64"这条断言才立得住。
/// </summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:v1:";

    private static readonly byte[] Mask = Encoding.UTF8.GetBytes("PotatoAgent.self-test.mask.v1");

    public enum Modes
    {
        /// <summary>可逆的假加密（base64 打底），正常路径用。</summary>
        Reversible,

        /// <summary>原样返回 —— 用来验"写盘前的明文自检"必须拦住它。</summary>
        Identity,

        /// <summary>一律解不开 —— 模拟换了机器 / 换了用户。</summary>
        Null,

        /// <summary>只有名字里带 broken 的那套解不开。</summary>
        FailForBroken,
    }

    /// <summary>只有点名的那份密文解不开（模拟"换机器 / 换用户"）。</summary>
    public Modes Mode { get; init; } = Modes.Reversible;

    private string? _brokenEnvelope;

    /// <summary>指定一份密文，让它解不开。</summary>
    public void BreakEnvelope(string envelope) => _brokenEnvelope = envelope;

    public string Protect(string plaintext)
        => Mode == Modes.Identity
            ? plaintext
            : Prefix + Convert.ToBase64String(Xor(Encoding.UTF8.GetBytes(plaintext)));

    public string? TryUnprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue) || Mode == Modes.Null)
        {
            return null;
        }

        if (Mode == Modes.FailForBroken && protectedValue == _brokenEnvelope)
        {
            return null;
        }

        // Identity 模式下密文就是明文，照原样还回去（这样"写盘自检"才是唯一拦住它的那道防线）。
        if (Mode == Modes.Identity)
        {
            return protectedValue;
        }

        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            return Encoding.UTF8.GetString(Xor(bytes));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>XOR 是可逆的，所以加解密共用一个函数。</summary>
    private static byte[] Xor(byte[] data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ Mask[i % Mask.Length]);
        }

        return result;
    }
}
