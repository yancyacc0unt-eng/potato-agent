// ---------- 七、模型列表（GET /v1/models） ----------
//
// 界面上那个"模型"下拉框靠它：拉到的名字写进档案当缓存，选中哪个就把 profile.model 改成哪个。
// 覆盖：端点补全、正常列表（排序去重 + 请求形状）、401、服务端没实现这个端点、空列表、
//       以及"缓存落盘 / 设置页保存不会把缓存擦掉"。
//
// ⚠ 与其它自测同一套安全前提：只打 127.0.0.1 上的假服务端，只写 %TEMP% 下的临时目录。

using PotatoAgent.Core.Brain;

namespace PotatoAgent.Core.SelfTest;

internal static class ModelListTests
{
    public static void Run()
    {
        Console.WriteLine("---- 七、模型列表（GET /v1/models） ----");

        var root = Path.Combine(Path.GetTempPath(), "potato-agent-models-" + Guid.NewGuid().ToString("N")[..8]);
        var protector = new FakeSecretProtector { Mode = FakeSecretProtector.Modes.Reversible };

        try
        {
            Directory.CreateDirectory(root);
            Console.WriteLine($"    临时目录: {root}");

            UriShapes();
            HappyPath();
            AuthFailure();
            NotAModelList();
            EmptyList();
            CacheRoundTrip(root, protector);
        }
        catch (Exception error)
        {
            Program.Fail($"模型列表测试自身抛异常: {error.GetType().Name}: {error.Message}");
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

    // ==================== 7.1 端点补全 ====================

    private static void UriShapes()
    {
        Console.WriteLine("  7.1 端点补全：三种写法都补成 /v1/models");

        Expect.Equal("https://api.example.com/v1/models",
            OpenAiProvider.BuildModelsUri("https://api.example.com").ToString(), "裸 base URL 补 /v1/models");
        Expect.Equal("https://api.example.com/v1/models",
            OpenAiProvider.BuildModelsUri("https://api.example.com/v1").ToString(), "已经带 /v1 就不重复补");
        Expect.Equal("https://api.example.com/v1/models",
            OpenAiProvider.BuildModelsUri("https://api.example.com/v1/models").ToString(), "完整端点原样使用");

        var missing = Catch.Of(() =>
        {
            OpenAiProvider.BuildModelsUri("");
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
        Expect.ExceptionType<ProviderConfigurationException>(missing, "base URL 为空时抛 ProviderConfigurationException");

        Console.WriteLine();
    }

    // ==================== 7.2 正常列表 ====================

    private static void HappyPath()
    {
        Console.WriteLine("  7.2 正常列表：排序去重，请求是 GET /v1/models 且带 Bearer");

        const string body = """
            {"object":"list","data":[
              {"id":"deepseek-reasoner","object":"model"},
              {"id":"deepseek-chat","object":"model"},
              {"id":"deepseek-chat","object":"model"},
              {"id":"","object":"model"}
            ]}
            """;

        using var server = MockChatServer.Start(MockResponse.Text(body));
        using var provider = ProviderFor(server);

        var names = Wait(provider.ListModelsAsync());

        Expect.Equal(2, names.Count, "去重之后 2 个（重复的名字和空名字都被丢掉）");
        Expect.Equal("deepseek-chat", names[0], "按名字排序：第一个是 chat");
        Expect.Equal("deepseek-reasoner", names[1], "第二个是 reasoner");

        var request = server.Request(0);
        Expect.Equal("GET", request?.Method, "用的是 GET");
        Expect.Equal("/v1/models", request?.Path, "路径是 /v1/models");
        Expect.Equal("Bearer " + Program.FakeKey, request?.Authorization, "带上了 Bearer 密钥");

        Console.WriteLine();
    }

    // ==================== 7.3 密钥不对 ====================

    private static void AuthFailure()
    {
        Console.WriteLine("  7.3 密钥不对：401 → ProviderHttpException，并标成鉴权失败");

        using var server = MockChatServer.Start(
            MockResponse.HttpError(401, """{"error":{"message":"invalid api key"}}"""));
        using var provider = ProviderFor(server);

        var error = Catch.Of(() => provider.ListModelsAsync()).GetAwaiter().GetResult();

        Expect.ExceptionType<ProviderHttpException>(error, "401 → ProviderHttpException");
        if (error is ProviderHttpException http)
        {
            Expect.Equal(401, http.StatusCode, "状态码如实带出来");
            Expect.True(http.IsAuthFailure, "标记成鉴权失败（界面该引导去设置页）");
            Expect.Contains(http.ResponseBody, "invalid api key", "服务端原话带出来，方便排查");
            Expect.DoesNotContain(http.RequestUrl, "sk-", "请求地址里没有密钥");
        }

        Console.WriteLine();
    }

    // ==================== 7.4 不是模型列表 ====================

    private static void NotAModelList()
    {
        Console.WriteLine("  7.4 服务端没实现这个端点 / 回的不是模型列表 → ProviderProtocolException");

        using (var server = MockChatServer.Start(MockResponse.Text("<!doctype html><html>not found</html>", "text/html")))
        {
            using var provider = ProviderFor(server);
            var error = Catch.Of(() => provider.ListModelsAsync()).GetAwaiter().GetResult();
            Expect.ExceptionType<ProviderProtocolException>(error, "回 HTML 时 → ProviderProtocolException（不是裸 JsonException）");
        }

        using (var server = MockChatServer.Start(MockResponse.Text("""{"models":["a","b"]}""")))
        {
            using var provider = ProviderFor(server);
            var error = Catch.Of(() => provider.ListModelsAsync()).GetAwaiter().GetResult();
            Expect.ExceptionType<ProviderProtocolException>(error, "合法 JSON 但没有 data 数组时也认得出");
            if (error is ProviderProtocolException protocol)
            {
                Expect.Contains(protocol.RawPayload ?? string.Empty, "models", "原始响应留在 RawPayload 里（排查协议不兼容靠它）");
            }
        }

        Console.WriteLine();
    }

    // ==================== 7.5 空列表 ====================

    private static void EmptyList()
    {
        Console.WriteLine("  7.5 空列表 → 如实报错，不假装成功");

        using var server = MockChatServer.Start(MockResponse.Text("""{"object":"list","data":[]}"""));
        using var provider = ProviderFor(server);

        var error = Catch.Of(() => provider.ListModelsAsync()).GetAwaiter().GetResult();
        Expect.ExceptionType<ProviderProtocolException>(error, "data 是空数组时 → ProviderProtocolException");

        Console.WriteLine();
    }

    // ==================== 7.6 缓存 ====================

    private static void CacheRoundTrip(string root, ISecretProtector protector)
    {
        Console.WriteLine("  7.6 模型名缓存：落盘 → 读回一致；设置页保存时不会被擦掉");

        var path = Path.Combine(root, "cache", "config.json");
        var store = new ConfigStore(path, protector);
        store.Load();
        store.UpsertProfile(new ProviderProfile
        {
            Name = "p",
            BaseUrl = "https://example.com",
            Model = "deepseek-chat",
            ApiKey = Program.FakeKey,
            KnownModels = new List<string> { "deepseek-chat", "deepseek-reasoner" },
        });
        store.Save();

        var reloaded = new ConfigStore(path, protector);
        var config = reloaded.Load();
        Expect.Equal(ConfigLoadStatus.Ok, reloaded.LastLoadStatus, "重新读盘成功");
        Expect.Equal(2, config.ActiveProfile?.KnownModels?.Count ?? 0, "缓存里 2 个模型名");
        Expect.Equal("deepseek-reasoner", config.ActiveProfile?.KnownModels?[1], "顺序原样保留");

        // 设置页保存时构造的 profile 不带这个字段（null）—— 那表示"这次别动"，不是"清空"。
        store.UpsertProfile(new ProviderProfile
        {
            Name = "p",
            BaseUrl = "https://example.com",
            Model = "deepseek-chat",
            ApiKey = Program.FakeKey,
        });
        store.Save();

        var afterSave = new ConfigStore(path, protector);
        Expect.Equal(2, afterSave.Load().ActiveProfile?.KnownModels?.Count ?? 0,
            "设置页保存（KnownModels 为 null）不会把缓存擦掉");

        Console.WriteLine();
    }

    // ==================== 内部工具 ====================

    private static OpenAiProvider ProviderFor(MockChatServer server) =>
        new(new ProviderProfile
        {
            Name = "selftest",
            BaseUrl = server.BaseUrl,
            Model = "switch-model",
            ApiKey = Program.FakeKey,
            Temperature = 0,
            MaxTokens = 16,
        });

    /// <summary>同步等一个模型列表请求跑完。</summary>
    private static IReadOnlyList<string> Wait(Task<IReadOnlyList<string>> task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            Program.Fail("模型列表请求没有在 20 秒内返回");
            return Array.Empty<string>();
        }

        return task.Result;
    }
}
