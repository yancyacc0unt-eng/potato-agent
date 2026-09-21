// ---------- 五、三个开关（模型 / 推理等级 / 权限档位） ----------
//
// 覆盖四件事：
//   1) 推理等级：设了才在请求体里发 reasoning_effort，没设就一个字段都不发；
//   2) 换当前档案（多 profile）：存盘 → 重启读回还是它 → 下一次请求的 model 字段真的换了；
//   3) 权限档位（basic / advanced）落盘 → 读回来还是它；
//   4) config.json 里的档位被手改成乱七八糟的词时【退回 basic】，而不是把整份配置判成损坏
//      —— 这正是不用枚举序号存盘的理由：改错一个词不该让用户连全部档案一起丢。
//
// ⚠ 与其它自测同一套安全前提：只打 127.0.0.1 上的假服务端，只写 %TEMP% 下的临时目录。

using System.Text;
using PotatoAgent.Core.Brain;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Core.SelfTest;

internal static class SwitchTests
{
    public static void Run()
    {
        Console.WriteLine("---- 五、三个开关（模型 / 推理等级 / 权限档位） ----");

        var root = Path.Combine(Path.GetTempPath(), "potato-agent-switch-" + Guid.NewGuid().ToString("N")[..8]);
        var protector = new FakeSecretProtector { Mode = FakeSecretProtector.Modes.Reversible };

        try
        {
            Directory.CreateDirectory(root);
            Console.WriteLine($"    临时目录: {root}");

            ReasoningEffortOnTheWire();
            ReasoningIsPersisted(root, protector);
            ModelSwitch(root, protector);
            ApprovalModeRoundTrip(root, protector);
            ApprovalModeSurvivesGarbage(root, protector);
            ApprovalModeNames();
        }
        catch (Exception error)
        {
            Program.Fail($"开关测试自身抛异常: {error.GetType().Name}: {error.Message}");
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

    // ==================== 5.1 推理等级：设了才发 ====================

    private static void ReasoningEffortOnTheWire()
    {
        Console.WriteLine("  5.1 reasoning_effort 只在设了的时候才出现在请求体里");

        using (var server = MockChatServer.Start(OkResponse()))
        {
            using var provider = ProviderFor(server, "high");
            SendOnce(provider);

            var body = server.Request(0)?.Body ?? string.Empty;
            Expect.Contains(body, "\"reasoning_effort\":\"high\"", "设成 high 时请求体里带着 reasoning_effort=high");
        }

        using (var server = MockChatServer.Start(OkResponse()))
        {
            using var provider = ProviderFor(server, null);
            SendOnce(provider);

            var body = server.Request(0)?.Body ?? string.Empty;
            Expect.DoesNotContain(body, "reasoning_effort", "没设时请求体里【没有】这个字段（服务端不认它就 400，默认不敢发）");
        }

        Console.WriteLine();
    }

    // ==================== 5.2 推理等级落盘 ====================

    private static void ReasoningIsPersisted(string root, ISecretProtector protector)
    {
        Console.WriteLine("  5.2 推理等级写进 config.json，读回来还是它；清空之后字段整个消失");

        var path = Path.Combine(root, "reasoning", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "r",
            BaseUrl = "https://example.com",
            Model = "m",
            ApiKey = Program.FakeKey,
            ReasoningEffort = "Medium",
        });
        store.Save();

        var onDisk = File.ReadAllText(path, Encoding.UTF8);
        Expect.Contains(onDisk, "\"reasoningEffort\": \"Medium\"", "文件里确实写下了 reasoningEffort");

        var reloaded = new ConfigStore(path, protector);
        var config = reloaded.Load();
        Expect.Equal(ConfigLoadStatus.Ok, reloaded.LastLoadStatus, "重新读盘成功");
        Expect.Equal("Medium", config.ActiveProfile?.ReasoningEffort, "推理等级逐字符一致");

        // 回到 Default（null）：文件里不该留下一个 null 字段，而是整个字段消失。
        store.ActiveProfile!.ReasoningEffort = null;
        store.Save();
        Expect.DoesNotContain(File.ReadAllText(path, Encoding.UTF8), "reasoningEffort", "清空之后这个字段从文件里整个消失（不写 null）");

        Console.WriteLine();
    }

    // ==================== 5.3 换当前档案 ====================

    private static void ModelSwitch(string root, ISecretProtector protector)
    {
        Console.WriteLine("  5.3 换当前档案（多 profile）：SetActiveProfile → 存盘 → 重启读回 → 下一次请求真的换了 model");

        var path = Path.Combine(root, "switch", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "a", BaseUrl = "https://a.example.com", Model = "model-a", ApiKey = Program.FakeKey,
        });
        store.UpsertProfile(new ProviderProfile
        {
            Name = "b", BaseUrl = "https://b.example.com", Model = "model-b", ApiKey = Program.FakeKey,
        });
        store.Save();

        Expect.Equal("b", store.Current.ActiveProfileName, "最后写入的那套成为当前档案");
        Expect.True(store.SetActiveProfile("a"), "切到 a 成功");
        Expect.True(!store.SetActiveProfile("no-such-profile"), "切到不存在的档案返回 false");
        Expect.Equal("a", store.Current.ActiveProfileName, "切失败时当前档案原地不动");
        store.Save();

        var reloaded = new ConfigStore(path, protector);
        var config = reloaded.Load();
        Expect.Equal(ConfigLoadStatus.Ok, reloaded.LastLoadStatus, "重新读盘成功");
        Expect.Equal("a", config.ActiveProfileName, "重启之后还是上次选的那一套（选择被持久化了）");
        Expect.Equal("model-a", config.ActiveProfile?.Model, "当前档案的模型名对得上");

        // 真正的证据：切完之后，请求体里发的是新模型。
        using var server = MockChatServer.Start(OkResponse());
        var active = reloaded.ActiveProfile!;
        active.BaseUrl = server.BaseUrl;
        using var provider = new OpenAiProvider(active);
        SendOnce(provider);

        var body = server.Request(0)?.Body ?? string.Empty;
        Expect.Contains(body, "\"model\":\"model-a\"", "请求体里的 model 就是切换后的那一个");

        Console.WriteLine();
    }

    // ==================== 5.4 权限档位落盘 ====================

    private static void ApprovalModeRoundTrip(string root, ISecretProtector protector)
    {
        Console.WriteLine("  5.4 权限档位（basic / advanced）写进 config.json，读回来还是它");

        var path = Path.Combine(root, "mode", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        Expect.Equal(ToolApprovalMode.Basic, store.Current.ApprovalMode, "全新配置默认 basic（默认从严）");

        store.Current.ApprovalMode = ToolApprovalMode.Advanced;
        store.Save();

        var reloaded = new ConfigStore(path, protector);
        var config = reloaded.Load();
        Expect.Equal(ConfigLoadStatus.Ok, reloaded.LastLoadStatus, "重新读盘成功");
        Expect.Equal(ToolApprovalMode.Advanced, config.ApprovalMode, "重启之后还是 advanced");
        Expect.Contains(
            File.ReadAllText(path, Encoding.UTF8),
            "\"toolApprovalMode\": \"advanced\"",
            "文件里存的是人看得懂的字符串，不是枚举序号");

        Console.WriteLine();
    }

    // ==================== 5.5 档位被手改坏 ====================

    private static void ApprovalModeSurvivesGarbage(string root, ISecretProtector protector)
    {
        Console.WriteLine("  5.5 toolApprovalMode 写错 → 退回 basic，档案一个都不许丢");

        var path = Path.Combine(root, "garbage", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "keepme", BaseUrl = "https://example.com", Model = "m", ApiKey = Program.FakeKey,
        });
        store.Save();

        // 大小写乱写：仍然认。
        RewriteMode(path, "basic", "ADVANCED");
        var shouted = new ConfigStore(path, protector);
        Expect.Equal(ToolApprovalMode.Advanced, shouted.Load().ApprovalMode, "大小写乱写也认（ADVANCED → advanced）");

        // 写成一个不存在的词：退回 basic，但配置本身必须还是"读得动"的。
        RewriteMode(path, "ADVANCED", "banana");
        var broken = new ConfigStore(path, protector);
        var config = broken.Load();
        Expect.Equal(ConfigLoadStatus.Ok, broken.LastLoadStatus, "写错一个词【不算】配置损坏（否则用户会连档案一起丢）");
        Expect.Equal(ToolApprovalMode.Basic, config.ApprovalMode, "认不出来的档位退回 basic");
        Expect.Equal(1, config.Profiles.Count, "档案还在");
        Expect.Equal("keepme", config.ActiveProfile?.Name, "当前档案也没丢");

        Console.WriteLine();
    }

    // ==================== 5.6 档位名 ↔ 枚举 ====================

    private static void ApprovalModeNames()
    {
        Console.WriteLine("  5.6 档位名 ↔ 枚举（界面下拉框靠它）");

        Expect.Equal("basic", ToolApprovalModeNames.ToName(ToolApprovalMode.Basic), "Basic → basic");
        Expect.Equal("advanced", ToolApprovalModeNames.ToName(ToolApprovalMode.Advanced), "Advanced → advanced");
        Expect.Equal(ToolApprovalMode.Advanced, ToolApprovalModeNames.Parse("advanced"), "advanced → Advanced");
        Expect.Equal(ToolApprovalMode.Advanced, ToolApprovalModeNames.Parse("  Advanced  "), "界面上的显示名（带空格 / 大小写不一）也认");
        Expect.Equal(ToolApprovalMode.Basic, ToolApprovalModeNames.Parse(null), "null → Basic");
        Expect.Equal(ToolApprovalMode.Basic, ToolApprovalModeNames.Parse("banana"), "认不出来 → Basic（绝不默默变成『几乎不警告』）");
        Expect.Equal(2, ToolApprovalModeNames.All.Length, "下拉框拿到的是 2 档");

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    /// <summary>把 config.json 里 toolApprovalMode 的值换一个词（只改那一处）。</summary>
    private static void RewriteMode(string path, string from, string to)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        File.WriteAllText(path, text.Replace($"\"{from}\"", $"\"{to}\"", StringComparison.Ordinal), Encoding.UTF8);
    }

    private static MockResponse OkResponse() =>
        MockResponse.Sse(Sse.Text("ok"), Sse.Finish("stop"), Sse.Done);

    private static OpenAiProvider ProviderFor(MockChatServer server, string? reasoning) =>
        new(new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = "switch-model",
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 16,
            ReasoningEffort = reasoning,
        });

    /// <summary>发一句话并等它跑完。内容不重要 —— 只要请求真的发到了假服务端。</summary>
    private static void SendOnce(OpenAiProvider provider)
    {
        var messages = new[] { ChatMessage.User("hello") };
        var task = Task.Run(async () =>
        {
            await foreach (var _ in provider.StreamCompletionAsync(messages).ConfigureAwait(false))
            {
                // 事件本身不关心。
            }
        });

        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail("请求没有在 20 秒内跑完");
        }
    }
}
