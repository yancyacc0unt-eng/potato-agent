// ============================================================================
// 工作区系统（PotatoAgent.Workspaces）自测 —— 可复跑工程，退出码 0 = 全部通过。
//
// 覆盖八块：
//   一、空目录启动  ：Current 为 null、Recent 为空
//   二、设为当前    ：返回 true、Path/Name 正确、workspaces.json 已生成、Changed 触发、尾分隔符被规范化
//   三、重启保留    ：换一个 WorkspaceStore 重新 Load，当前工作区还在
//   四、最近列表    ：依次设 12 个 → 最多 10 条、最新在前、无重复、不含 Current、重启后顺序不变
//   五、失败不改状态：不存在的目录 / 文件不是目录 / 空路径 → false + 英文 error，Current 与 Recent 原样
//   六、坏文件降级  ：半截 JSON / 字段坏 / 结构坏 → Load 不抛、降级成空，且不改写坏文件
//   七、Find        ：向上找最近的 .potato\（最近优先，找不到返回 null）
//   八、EnsureMarker：幂等（调两次同一路径）、目录真的建出来
//
// ⚠ 安全前提：所有路径都在 %TEMP% 下的一次性沙箱目录里，跑完递归删干净；
//   绝不读、绝不写 %APPDATA%\PotatoAgent 下的任何真实文件（结尾还有一条护栏断言证明这点）。
//
// 跑法：
//   dotnet build build\WorkspacesSelfTest.csproj -c Debug
//   dotnet run   --project build\WorkspacesSelfTest.csproj -c Debug
// ============================================================================

using System.Text;
using PotatoAgent.Workspaces;

namespace PotatoAgent.Workspaces.SelfTest;

internal static class Program
{
    private static int _pass;
    private static int _fail;

    /// <summary>三、里用来验证时间戳能原样存回磁盘。</summary>
    private static DateTimeOffset _ws01OpenedAt;

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        var sandbox = Path.Combine(Path.GetTempPath(), "potato-workspaces-selftest-" + Guid.NewGuid().ToString("N"));
        var realFile = Path.Combine(DefaultRealDirectory(), "workspaces.json");
        var realExistedBefore = File.Exists(realFile);
        var realStampBefore = realExistedBefore ? File.GetLastWriteTimeUtc(realFile) : default;

        Console.WriteLine("==================== PotatoAgent.Workspaces 工作区系统自测 ====================");
        Console.WriteLine($"临时沙箱 : {sandbox}");
        Console.WriteLine($"落盘范围 : 只在沙箱目录里，绝不碰 {DefaultRealDirectory()}");
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(sandbox);

            var configDir = Path.Combine(sandbox, "config");
            var wsRoot = Path.Combine(sandbox, "ws");

            EmptyStart(configDir);
            SetCurrent(configDir, wsRoot);
            SurvivesRestart(configDir, wsRoot);
            RecentCapAndOrder(configDir, wsRoot);
            FailuresKeepState(configDir, sandbox);
            CorruptFileDegrades(sandbox);
            FindMarker(sandbox);
            EnsureMarkerIdempotent(sandbox);
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup(sandbox);

            Section("护栏");
            Check("全程没碰用户真实配置（%APPDATA%\\PotatoAgent\\workspaces.json 状态不变）",
                File.Exists(realFile) == realExistedBefore &&
                (!realExistedBefore || File.GetLastWriteTimeUtc(realFile) == realStampBefore));
            Check("沙箱目录跑完已删干净", !Directory.Exists(sandbox));
            Console.WriteLine();
        }

        return Report();
    }

    // ==================== 一、空目录启动 ====================

    private static void EmptyStart(string configDir)
    {
        Section("一、空目录启动");

        var store = new WorkspaceStore(configDir);
        Check("ConfigFilePath = <配置目录>\\workspaces.json",
            string.Equals(store.ConfigFilePath, Path.Combine(Norm(configDir), "workspaces.json"), StringComparison.OrdinalIgnoreCase));

        store.Load();
        Check("没有工作区时 Current 为 null", store.Current is null);
        Check("没有工作区时 Recent 为空", store.Recent.Count == 0);
        Check("此时磁盘上还没有 workspaces.json", !File.Exists(store.ConfigFilePath));
    }

    // ==================== 二、设为当前工作区 ====================

    private static void SetCurrent(string configDir, string wsRoot)
    {
        Section("二、设为当前工作区");

        var folder = Path.Combine(wsRoot, "ws01");
        Directory.CreateDirectory(folder);

        var store = new WorkspaceStore(configDir);
        store.Load();

        var changedCount = 0;
        store.Changed += (_, _) => changedCount++;

        var ok = store.TrySetCurrent(folder, out var error);
        Check("TrySetCurrent 返回 true", ok);
        Check("成功时 error 为 null", ok && error is null);
        Check("Current.Path 是规范化后的绝对路径",
            store.Current is not null && string.Equals(store.Current.Path, Norm(folder), StringComparison.OrdinalIgnoreCase));
        Check("Current.Name 是目录名 ws01", store.Current?.Name == "ws01");
        Check("LastOpenedAt 是刚刚（不是默认值）",
            store.Current is not null && (DateTimeOffset.Now - store.Current.LastOpenedAt).Duration() < TimeSpan.FromMinutes(5));
        Check("workspaces.json 已生成", File.Exists(store.ConfigFilePath));
        Check("Changed 触发了 1 次", changedCount == 1);

        var text = File.ReadAllText(store.ConfigFilePath, Encoding.UTF8);
        Check("JSON 是 camelCase + 缩进（记事本能看）",
            text.Contains("\"current\"", StringComparison.Ordinal) &&
            text.Contains("\"recent\"", StringComparison.Ordinal) &&
            text.Contains('\n'));

        // 同一个目录、带尾分隔符再设一次：应当规范化成同一个工作区
        var again = store.TrySetCurrent(folder + Path.DirectorySeparatorChar, out var error2);
        Check("同一目录带尾分隔符再设一次也成功", again && error2 is null);
        Check("尾分隔符被规范化掉（路径里没有尾部反斜杠）",
            store.Current is not null && string.Equals(store.Current.Path, Norm(folder), StringComparison.OrdinalIgnoreCase));
        Check("同一路径重复设置不产生 Recent 重复", store.Recent.Count == 0);
        Check("重复设置也算一次变化（Changed 又触发）", changedCount == 2);
        Check("重复设置会刷新打开时间（时间戳 >= 第一次）",
            store.Current is not null && store.Current.LastOpenedAt >= _ws01OpenedAt);

        // 记下最终落盘的那个时间戳，第三块用它验证 ISO 往返没丢精度
        _ws01OpenedAt = store.Current?.LastOpenedAt ?? default;
    }

    // ==================== 三、重新 Load（模拟重启） ====================

    private static void SurvivesRestart(string configDir, string wsRoot)
    {
        Section("三、重新 Load（模拟重启）");

        var folder = Norm(Path.Combine(wsRoot, "ws01"));
        var reopened = new WorkspaceStore(configDir);
        reopened.Load();

        Check("重启后 Current 还是 ws01",
            reopened.Current is not null && string.Equals(reopened.Current.Path, folder, StringComparison.OrdinalIgnoreCase));
        Check("重启后 Name 也对", reopened.Current?.Name == "ws01");
        Check("重启后 LastOpenedAt 原样读回来（ISO 时间戳没丢精度）",
            reopened.Current is not null && reopened.Current.LastOpenedAt == _ws01OpenedAt);
        Check("重启后 Recent 仍为空", reopened.Recent.Count == 0);
    }

    // ==================== 四、最近列表 ====================

    private static void RecentCapAndOrder(string configDir, string wsRoot)
    {
        Section("四、最近列表（上限 10、最新在前、无重复）");

        var folders = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            var path = Path.Combine(wsRoot, "ws" + i.ToString("00"));
            Directory.CreateDirectory(path);
            folders.Add(Norm(path));
        }

        var store = new WorkspaceStore(configDir);
        store.Load();

        var allSet = true;
        foreach (var folder in folders)
        {
            allSet &= store.TrySetCurrent(folder, out _);
        }

        Check("依次设置 12 个不同工作区全部成功", allSet);
        Check("Current 是最后设的那个（ws12）", store.Current?.Name == "ws12");

        // 最新的 10 个：ws11 → ws02（ws12 是 Current，不进 Recent；ws01 被挤出去）
        var expected = Enumerable.Range(2, 10).Reverse().Select(i => "ws" + i.ToString("00")).ToArray();
        var recent = store.Recent;

        Check("Recent 最多 10 条", recent.Count == 10);
        Check("最新在前、旧的后推", recent.Select(w => w.Name).SequenceEqual(expected));
        Check("Recent 无重复（同一个文件夹只留一条）",
            recent.Select(w => w.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == recent.Count);
        Check("Recent 不含 Current", recent.All(w => !string.Equals(w.Path, store.Current?.Path, StringComparison.OrdinalIgnoreCase)));
        Check("最早的 ws01 已被挤出列表", recent.All(w => w.Name != "ws01"));

        var reloaded = new WorkspaceStore(configDir);
        reloaded.Load();
        Check("重启后 Recent 还是 10 条、顺序不变",
            reloaded.Recent.Count == 10 && reloaded.Recent.Select(w => w.Name).SequenceEqual(expected));
        Check("重启后 Current 也还是 ws12", reloaded.Current?.Name == "ws12");

        // 把正好在 Current 里的 ws11 再设一次：它自己不该进 Recent，ws12 也不该出现两条
        store.TrySetCurrent(folders[10], out _);
        Check("重复设置 Current 里的路径：它自己不进 Recent", store.Recent.All(w => w.Name != "ws11"));
        Check("重复设置后 ws12 只有一条", store.Recent.Count(w => w.Name == "ws12") == 1);
        Check("重复设置后 Recent 仍是 10 条", store.Recent.Count == 10);
    }

    // ==================== 五、设置失败不改状态 ====================

    private static void FailuresKeepState(string configDir, string sandbox)
    {
        Section("五、设置失败不改状态");

        var store = new WorkspaceStore(configDir);
        store.Load();

        var beforePath = store.Current?.Path;
        var beforeRecent = store.Recent.Count;

        var missing = Path.Combine(sandbox, "no-such-folder");
        var ok = store.TrySetCurrent(missing, out var error);
        Check("不存在的目录 → false", !ok);
        Check("error 是一句非空英文说明", !string.IsNullOrWhiteSpace(error) && error!.Length > 8);
        Check("Current 没变", string.Equals(store.Current?.Path, beforePath, StringComparison.OrdinalIgnoreCase));
        Check("Recent 没变", store.Recent.Count == beforeRecent);

        var file = Path.Combine(sandbox, "not-a-folder.txt");
        File.WriteAllText(file, "x", Encoding.UTF8);
        var okFile = store.TrySetCurrent(file, out var errorFile);
        Check("路径是文件不是目录 → false", !okFile);
        Check("这条 error 也非空", !string.IsNullOrWhiteSpace(errorFile));
        Check("Current 依然没变", string.Equals(store.Current?.Path, beforePath, StringComparison.OrdinalIgnoreCase));

        var okEmpty = store.TrySetCurrent("   ", out var errorEmpty);
        Check("空路径 → false 且有说明", !okEmpty && !string.IsNullOrWhiteSpace(errorEmpty));
        Check("Current 还是没变", string.Equals(store.Current?.Path, beforePath, StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 六、坏文件降级 ====================

    private static void CorruptFileDegrades(string sandbox)
    {
        Section("六、坏文件降级（Load 永不抛）");

        var configDir = Path.Combine(sandbox, "config-bad");
        Directory.CreateDirectory(configDir);
        var file = Path.Combine(configDir, "workspaces.json");

        File.WriteAllText(file, "{ \"current\": { \"path\": \"C:\\\\x\" ", Encoding.UTF8); // 手写半截 JSON
        var truncated = new WorkspaceStore(configDir);
        truncated.Load();
        Check("半截 JSON：Load 不抛、Current 降级为 null", truncated.Current is null);
        Check("半截 JSON：Recent 降级为空", truncated.Recent.Count == 0);

        File.WriteAllText(file,
            "{\"current\":{\"path\":\"\",\"name\":\"\"},\"recent\":[{\"name\":\"只有名字没路径\"},{\"path\":\"   \"}]}",
            Encoding.UTF8);
        var badFields = new WorkspaceStore(configDir);
        badFields.Load();
        Check("字段坏：Current 为 null", badFields.Current is null);
        Check("字段坏：坏条目被丢掉，Recent 为空", badFields.Recent.Count == 0);

        File.WriteAllText(file, "[1,2,3]", Encoding.UTF8);
        var badShape = new WorkspaceStore(configDir);
        badShape.Load();
        Check("结构坏：降级成没有工作区", badShape.Current is null && badShape.Recent.Count == 0);
        Check("Load 不改写坏文件（留给用户自己修）", File.ReadAllText(file, Encoding.UTF8) == "[1,2,3]");

        // 坏文件之后照常能用：设置成功会写出一份好的
        var folder = Path.Combine(sandbox, "ws", "ws01");
        var ok = badShape.TrySetCurrent(folder, out var error);
        var reopened = new WorkspaceStore(configDir);
        reopened.Load();
        Check("坏文件之后照常能设工作区并写好文件",
            ok && error is null && reopened.Current?.Name == "ws01");
    }

    // ==================== 七、Find 向上找 .potato ====================

    private static void FindMarker(string sandbox)
    {
        Section("七、Find 向上找 .potato");

        var a = Path.Combine(sandbox, "root-a");
        var b = Path.Combine(a, "b");
        var c = Path.Combine(b, "c");
        Directory.CreateDirectory(c);
        Directory.CreateDirectory(Path.Combine(c, WorkspaceRoot.MarkerDirectoryName));

        Check("Find(c) == c（自己下面就有标记）", Same(WorkspaceRoot.Find(c), c));
        Check("Find(b) == null（上面还没有标记）", WorkspaceRoot.Find(b) is null);

        Directory.CreateDirectory(Path.Combine(a, WorkspaceRoot.MarkerDirectoryName));
        Check("a 建标记后 Find(a) == a", Same(WorkspaceRoot.Find(a), a));
        Check("a 建标记后 Find(b) == a（向上找到）", Same(WorkspaceRoot.Find(b), a));
        Check("最近的优先：Find(c) 仍然是 c", Same(WorkspaceRoot.Find(c), c));
        Check("起点带尾分隔符也能找", Same(WorkspaceRoot.Find(c + Path.DirectorySeparatorChar), c));
        Check("起点目录还不存在也照样往上找", Same(WorkspaceRoot.Find(Path.Combine(c, "deep", "deeper")), c));

        Directory.Delete(Path.Combine(c, WorkspaceRoot.MarkerDirectoryName));
        Check("标记删掉后 Find(b) 继续往上 == a", Same(WorkspaceRoot.Find(b), a));
    }

    // ==================== 八、EnsureMarker 幂等 ====================

    private static void EnsureMarkerIdempotent(string sandbox)
    {
        Section("八、EnsureMarker 幂等");

        var dir = Path.Combine(sandbox, "marker-d");
        Directory.CreateDirectory(dir);

        var first = WorkspaceRoot.EnsureMarker(dir);
        var second = WorkspaceRoot.EnsureMarker(dir);

        Check("两次调用返回同一路径", string.Equals(first, second, StringComparison.OrdinalIgnoreCase));
        Check("返回的就是 <目录>\\.potato", Same(first, Path.Combine(dir, WorkspaceRoot.MarkerDirectoryName)));
        Check(".potato 目录真的存在", Directory.Exists(first));
        Check("常量 MarkerDirectoryName == \".potato\"", WorkspaceRoot.MarkerDirectoryName == ".potato");
    }

    // ==================== 断言、清理与汇总 ====================

    private static void Check(string name, bool condition)
    {
        if (condition) { _pass++; } else { _fail++; }
        Console.WriteLine($"    [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    private static void Fail(string name)
    {
        _fail++;
        Console.WriteLine($"    [FAIL] {name}");
    }

    private static void Section(string title) => Console.WriteLine($"---- {title} ----");

    /// <summary>规范化成"绝对路径 + 无尾分隔符"，和被测代码里的规则保持一致。</summary>
    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>两个路径是不是同一个（Windows：大小写不敏感）。</summary>
    private static bool Same(string? actual, string expected) =>
        actual is not null && string.Equals(actual, Norm(expected), StringComparison.OrdinalIgnoreCase);

    /// <summary>用户真实配置目录（只拿来断言"我们没碰它"，绝不写）。</summary>
    private static string DefaultRealDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PotatoAgent");

    private static void Cleanup(string sandbox)
    {
        try
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"    （沙箱没删干净，请手工删 {sandbox}: {error.GetType().Name}: {error.Message}）");
        }
    }

    private static int Report()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine($"PASS {_pass} / FAIL {_fail}");
        Console.WriteLine("==================================================================");
        return _fail == 0 ? 0 : 1;
    }
}
