// ============================================================================
// 会话系统（PotatoAgent.Sessions）自测 —— 可复跑工程，退出码 0 = 全部通过，1 = 有失败。
//
// 覆盖十一块（编号对应任务书的验收点）：
//   1  Initialize() 建库 + 空库 List()                 2  Create() / Get() / List()
//   3  Rename() 改标题 + UpdatedAt 变新 + 列表重排      4  Append() 三种消息往返 + MessageCount
//   5  Delete() 连消息一起删（ON DELETE CASCADE）       6  Dispose 后重开，数据还在
//   7  SessionMessageSanitizer：带图消息 → 占位文本     8  SetWorkspace() 生效
//   9  SessionTitle：压平 / trim / 截 30 字 / 兜底      10 坏库 → InvalidDataException（不重建）
//   11 两个线程各 Append 50 条 → 100 条且 seq 不重复
//
// 除了黑盒调用被测代码，还会用【另一条独立连接】直接查库，
// 验证 seq / content 摘要 / payload 这些"落盘细节"真的按约定写了（第 4、5、6、7 节）。
//
// ⚠ 安全前提：只在 %TEMP% 下自建临时目录里建 data.db，跑完整目录删掉；
//   全程绝不读、绝不写 %APPDATA%\PotatoAgent（末尾有护栏断言）。
//
// 跑法：
//   dotnet build build\SessionsSelfTest.csproj -c Debug
//   dotnet run   --project build\SessionsSelfTest.csproj -c Debug
// ============================================================================

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PotatoAgent.Core.Brain;

namespace PotatoAgent.Sessions.SelfTest;

internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        Console.WriteLine("==================== PotatoAgent.Sessions 会话系统自测 ====================");
        Console.WriteLine($"运行时   : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}   进程 {(Environment.Is64BitProcess ? 64 : 32)} 位");
        Console.WriteLine($"临时目录 : {Path.GetTempPath()}");
        Console.WriteLine("数据库   : 只用 %TEMP% 下的临时 data.db，绝不碰 %APPDATA%\\PotatoAgent");
        Console.WriteLine();

        var realDatabase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PotatoAgent", "data.db");
        var realDatabaseBefore = Snapshot(realDatabase);

        var root = Path.Combine(Path.GetTempPath(), "potato-sessions-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        try
        {
            RunTests(root);
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup(root, realDatabase, realDatabaseBefore);
        }

        Console.WriteLine("==================================================================");
        Console.WriteLine($"PASS {_checks - _failures} / FAIL {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunTests(string root)
    {
        var dbPath = Path.Combine(root, "data.db");
        string sessionId;
        DateTimeOffset createdAt;

        // 带图消息在第 7 节（纯函数）和第 7b 节（落盘）都要用，先在作用域外造好。
        var imagePart = ChatContentPart.FromImageDataUri("data:image/png;base64," + new string('A', 20000));
        var imageMessage = ChatMessage.ToolWithParts(
            "call_shot_1",
            new[] { ChatContentPart.FromText("这是截图"), imagePart },
            "pc_screenshot");

        // ---------------- 1. Initialize() ----------------
        Section("1. Initialize() 建库");
        Check("没用过的实例直接读写会抛 InvalidOperationException（必须显式 Initialize）",
            Catch(() => new SqliteSessionStore(Path.Combine(root, "never-init.db")).List()) is InvalidOperationException);

        using (var store = new SqliteSessionStore(dbPath))
        {
            store.Initialize();
            Check("DatabasePath 就是传入的临时路径（自测不碰真实库）",
                string.Equals(store.DatabasePath, dbPath, StringComparison.OrdinalIgnoreCase));
            Check("Initialize() 后 db 文件已落盘", File.Exists(dbPath));
            Check("全新库 List() 为空", store.List().Count == 0);
            Check("库处于 WAL 模式（PRAGMA journal_mode 已落进文件头）", ReadJournalMode(dbPath) == "wal");

            // ---------------- 2. Create / Get / List ----------------
            Section("2. Create() / Get() / List()");
            var first = store.Create();
            sessionId = first.Id;
            createdAt = first.CreatedAt;
            Check("默认标题是 \"New chat\"", first.Title == "New chat");
            Check("Id 是 12 位小写十六进制短 id",
                first.Id.Length == 12 && first.Id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
            Check("新建会话 MessageCount = 0", first.MessageCount == 0);
            Check("新建时 CreatedAt == UpdatedAt", first.CreatedAt == first.UpdatedAt);
            Check("Get() 拿得到", store.Get(first.Id) is { Title: "New chat" } got && got.Id == first.Id);
            Check("Get() 不存在的 id 返回 null", store.Get("ffffffffffff") is null);
            Check("List() 里有它", store.List().Any(s => s.Id == first.Id));

            var second = store.Create("第二个会话");
            var secondId = second.Id;
            Check("显式标题生效", second.Title == "第二个会话");
            Check("List() 按 UpdatedAt 倒序：刚建的排最前", store.List().Count == 2 && store.List()[0].Id == secondId);

            // ---------------- 3. Rename ----------------
            Section("3. Rename()");
            Check("Rename() 返回 true", store.Rename(sessionId, "改过标题的会话"));
            var renamed = store.Get(sessionId);
            Check("标题已改", renamed?.Title == "改过标题的会话");
            Check("UpdatedAt 变新（严格大于创建时间）", renamed is not null && renamed.UpdatedAt > createdAt);
            Check("改名后重新排到 List() 最前", store.List()[0].Id == sessionId);
            Check("Rename() 不存在的会话返回 false", !store.Rename("ffffffffffff", "x"));
            Check("Rename() 空白标题被拒绝（标题不动）",
                !store.Rename(sessionId, "   ") && store.Get(sessionId)?.Title == "改过标题的会话");

            // ---------------- 4. Append / LoadMessages ----------------
            Section("4. Append() / LoadMessages()：三种消息往返");
            var toolCall = new ToolCall
            {
                Id = "call_launch_1",
                Function = new ToolCallFunction { Name = "pc_launch", Arguments = "{\"path\":\"notepad.exe\"}" },
            };
            var userMessage = ChatMessage.User("帮我打开记事本");
            var assistantMessage = ChatMessage.Assistant(null, new List<ToolCall> { toolCall });
            var toolMessage = ChatMessage.Tool("call_launch_1", "{\"ok\":true}", "pc_launch");
            store.Append(sessionId, new[] { userMessage, assistantMessage, toolMessage });

            var messages = store.LoadMessages(sessionId);
            Check("LoadMessages() 拿到 3 条", messages.Count == 3);
            Check("顺序与写入一致 user → assistant → tool",
                messages.Count == 3 && messages[0].Role == "user" && messages[1].Role == "assistant" && messages[2].Role == "tool");
            Check("user 文本往返一致", messages[0].Content == "帮我打开记事本");
            Check("assistant 无文本（null）但 tool_calls 还在", messages[1].Content is null && messages[1].ToolCalls is { Count: 1 });
            Check("tool_call 的 id / 工具名往返一致",
                messages[1].ToolCalls![0].Id == "call_launch_1" && messages[1].ToolCalls![0].Function.Name == "pc_launch");
            Check("arguments 原样往返（它本身就是 JSON 字符串）",
                messages[1].ToolCalls![0].Function.Arguments == "{\"path\":\"notepad.exe\"}");
            Check("tool 消息的 tool_call_id 往返一致", messages[2].ToolCallId == "call_launch_1");
            Check("tool 消息的 name 也保留", messages[2].Name == "pc_launch");
            Check("MessageCount 跟着涨到 3", store.Get(sessionId)?.MessageCount == 3);

            store.Append(sessionId, Array.Empty<ChatMessage>());
            Check("Append 空集合是 no-op（条数不变）", store.Get(sessionId)?.MessageCount == 3);
            Check("Append 到不存在的会话抛 InvalidOperationException（不静默丢消息）",
                Catch(() => store.Append("ffffffffffff", new[] { ChatMessage.User("x") })) is InvalidOperationException);
            Check("LoadMessages() 不存在的会话返回空表（不是 null）", store.LoadMessages("ffffffffffff").Count == 0);

            var rows = RawRows(dbPath, "SELECT seq, role, content, payload FROM messages WHERE session_id = $id ORDER BY seq;", sessionId);
            Check("落盘的 seq 从 1 连续递增",
                rows.Count == 3 && rows[0][0] == "1" && rows[1][0] == "2" && rows[2][0] == "3");
            Check("content 列只是纯文本摘要（assistant 没文本 → NULL）",
                rows[0][2] == "帮我打开记事本" && rows[1][2] is null && rows[2][2] == "{\"ok\":true}");
            Check("payload 列是完整 JSON（tool_calls 在里面）",
                rows[1][3] is not null
                && rows[1][3]!.Contains("\"tool_calls\"", StringComparison.Ordinal)
                && rows[1][3]!.Contains("call_launch_1", StringComparison.Ordinal));

            // ---------------- 5. Delete ----------------
            Section("5. Delete()：连消息一起删");
            store.Append(secondId, new[] { ChatMessage.User("待删除会话的消息") });
            Check("删除前 Get() 拿得到", store.Get(secondId) is not null);
            Check("删除前它也有消息", store.LoadMessages(secondId).Count == 1);
            Check("Delete() 返回 true", store.Delete(secondId));
            Check("删除后 Get() 为 null", store.Get(secondId) is null);
            Check("删除后 LoadMessages() 为空", store.LoadMessages(secondId).Count == 0);
            Check("删除后 List() 不含它", store.List().All(s => s.Id != secondId));
            Check("Delete() 同一个 id 再删返回 false", !store.Delete(secondId));
            Check("消息行被 ON DELETE CASCADE 真删掉了",
                RawRows(dbPath, "SELECT seq FROM messages WHERE session_id = $id;", secondId).Count == 0);

            // ---------------- 9. SessionTitle ----------------
            Section("9. SessionTitle.FromFirstMessage()");
            Check("null → New chat", SessionTitle.FromFirstMessage(null) == "New chat");
            Check("纯空白 → New chat", SessionTitle.FromFirstMessage("   \r\n\t  ") == "New chat");
            Check("首尾空白被 trim", SessionTitle.FromFirstMessage("   你好世界   ") == "你好世界");
            Check("换行 / 连续空白压成单个空格",
                SessionTitle.FromFirstMessage("第一行\n\n第二行\t\t第三行") == "第一行 第二行 第三行");
            var exactly30 = new string('字', 30);
            Check("正好 30 个字不截断、不加省略号", SessionTitle.FromFirstMessage(exactly30) == exactly30);
            Check("31 个字截到 30 个再补 …", SessionTitle.FromFirstMessage(new string('字', 31)) == exactly30 + "…");
            var longLine = "这是很长的一行内容";
            var flattened = string.Join(" ", Enumerable.Repeat(longLine, 6));
            Check("超长多行文本：先压平再截断（标题里没有换行，正文 30 字 + …）",
                SessionTitle.FromFirstMessage(string.Join("\n", Enumerable.Repeat(longLine, 6))) == flattened[..30] + "…");
            var emojiTitle = SessionTitle.FromFirstMessage("a" + string.Concat(Enumerable.Repeat("😀", 40)));
            Check("超长 emoji 标题不会留下半个字符（无孤立代理项）",
                !HasLoneSurrogate(emojiTitle) && emojiTitle.EndsWith("…", StringComparison.Ordinal));

            // ---------------- 7a. Sanitizer（纯函数） ----------------
            Section("7. SessionMessageSanitizer（纯函数部分）");
            var sanitized = SessionMessageSanitizer.Sanitize(imageMessage);
            Check("图片段没了（读回也不会有 image_url）",
                sanitized.Parts is { Count: > 0 } && sanitized.Parts!.All(p => p.Type != "image_url" && p.ImageUrl is null));
            Check("图片段换成了文字占位 [image omitted]",
                sanitized.Parts!.Any(p => p.Text == SessionMessageSanitizer.ImagePlaceholder));
            Check("原来的文本段保留", sanitized.Parts!.Any(p => p.Text == "这是截图"));
            Check("role 保留", sanitized.Role == "tool");
            Check("tool_call_id / name 保留", sanitized.ToolCallId == "call_shot_1" && sanitized.Name == "pc_screenshot");
            Check("没有就地改动入参（原消息还是带图的）",
                imageMessage.Parts!.Count == 2 && imageMessage.Parts[1].Type == "image_url");

            var plainMessage = ChatMessage.User("纯文本消息");
            Check("无图消息原样返回（同一个实例，不做无谓拷贝）",
                ReferenceEquals(SessionMessageSanitizer.Sanitize(plainMessage), plainMessage));
            var textOnlyParts = ChatMessage.UserWithParts(new[] { ChatContentPart.FromText("只有文字段") });
            Check("只有文本段的多模态消息也不动",
                ReferenceEquals(SessionMessageSanitizer.Sanitize(textOnlyParts), textOnlyParts));
            var assistantWithCalls = ChatMessage.Assistant("说话", new List<ToolCall> { toolCall });
            Check("带 tool_calls 的无图消息原样返回",
                ReferenceEquals(SessionMessageSanitizer.Sanitize(assistantWithCalls), assistantWithCalls));

            // ---------------- 8. SetWorkspace ----------------
            Section("8. SetWorkspace()");
            Check("SetWorkspace() 返回 true", store.SetWorkspace(sessionId, @"C:\work\potato"));
            Check("工作区往返一致", store.Get(sessionId)?.Workspace == @"C:\work\potato");
            Check("SetWorkspace(null) 解绑",
                store.SetWorkspace(sessionId, null) && store.Get(sessionId)?.Workspace is null);
            Check("SetWorkspace() 不存在的会话返回 false", !store.SetWorkspace("ffffffffffff", @"C:\work"));
            store.SetWorkspace(sessionId, root);
        }

        // ---------------- 6. 持久化 ----------------
        Section("6. 持久化：Dispose 后重开同一个库");
        using (var reopened = new SqliteSessionStore(dbPath))
        {
            reopened.Initialize();
            reopened.Initialize(); // 幂等：重复调用不清数据
            var record = reopened.Get(sessionId);
            Check("重开后会话还在", record is not null);
            Check("标题 / 工作区 / 消息数都还在",
                record is { Title: "改过标题的会话", MessageCount: 3 } && record.Workspace == root);
            var reloaded = reopened.LoadMessages(sessionId);
            Check("重开后消息顺序与内容一致",
                reloaded.Count == 3 && reloaded[0].Content == "帮我打开记事本" && reloaded[2].ToolCallId == "call_launch_1");
            Check("List() 数量正确（删掉的那个不会复活）", reopened.List().Count == 1);
            Check("Initialize() 幂等：重复调用不影响已存数据", reopened.List()[0].Id == sessionId);

            // ---------------- 7b. Sanitizer 端到端：带图消息真的没把图写进库 ----------------
            Section("7b. 带图消息落盘（端到端）");
            reopened.Append(sessionId, new[] { imageMessage });
            var stored = reopened.LoadMessages(sessionId);
            Check("读回 4 条", stored.Count == 4);
            Check("读回的那条图片段已经变成占位文本",
                stored[3].Parts is { Count: > 0 } && stored[3].Parts!.All(p => p.Type != "image_url" && p.ImageUrl is null));
            Check("读回后 role / tool_call_id / name 保留",
                stored[3].Role == "tool" && stored[3].ToolCallId == "call_shot_1" && stored[3].Name == "pc_screenshot");
            Check("读回后文本段保留", stored[3].Parts!.Any(p => p.Text == "这是截图"));

            var payloadRow = RawRows(dbPath,
                "SELECT length(payload), payload FROM messages WHERE session_id = $id ORDER BY seq DESC LIMIT 1;", sessionId);
            var payload = payloadRow[0][1] ?? string.Empty;
            Check("库里搜不到 data:image / base64（图片真的没落盘）",
                !payload.Contains("data:image", StringComparison.Ordinal)
                && !payload.Contains("base64", StringComparison.Ordinal));
            Check("payload 因此很小（< 2000 字节，原图 20000 字符）",
                int.Parse(payloadRow[0][0]!, CultureInfo.InvariantCulture) < 2000);
            Check("MessageCount 跟着变成 4", reopened.Get(sessionId)?.MessageCount == 4);
        }

        // ---------------- 10. 坏库 ----------------
        Section("10. 坏库：必须抛 InvalidDataException，不许静默重建");
        var badPath = Path.Combine(root, "broken.db");
        var garbage = Encoding.UTF8.GetBytes("这不是 SQLite 库，只是一段垃圾字节。" + new string('x', 600));
        File.WriteAllBytes(badPath, garbage);
        using (var broken = new SqliteSessionStore(badPath))
        {
            var error = Catch(() => broken.Initialize());
            Check("坏库 Initialize() 抛 InvalidDataException", error is InvalidDataException);
            if (error is not null)
            {
                Console.WriteLine($"       捕获 {error.GetType().Name}: {Shorten(error.Message)}");
            }

            var after = File.ReadAllBytes(badPath);
            Check("坏库没有被静默重建（字节数没变）", after.Length == garbage.Length);
            Check("坏库内容原样保留（没被覆盖成 SQLite 文件）", !after.AsSpan().StartsWith("SQLite format 3\0"u8));
            Check("坏库上别的操作也抛异常，而不是装作空库", Catch(() => broken.List()) is InvalidOperationException);
        }

        // ---------------- 11. 并发 ----------------
        Section("11. 并发：两个线程各 Append 50 条");
        var concurrentPath = Path.Combine(root, "concurrent.db");
        using (var store = new SqliteSessionStore(concurrentPath))
        {
            store.Initialize();
            var session = store.Create("并发测试");
            var errors = new List<Exception>();
            var threads = new List<Thread>();

            for (var worker = 0; worker < 2; worker++)
            {
                var tag = worker;
                var thread = new Thread(() =>
                {
                    try
                    {
                        for (var i = 0; i < 50; i++)
                        {
                            store.Append(session.Id, new[] { ChatMessage.User($"线程{tag}-第{i}条") });
                        }
                    }
                    catch (Exception error)
                    {
                        lock (errors) errors.Add(error);
                    }
                });
                threads.Add(thread);
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            if (errors.Count > 0)
            {
                Console.WriteLine($"       第一个异常：{errors[0].GetType().Name}: {Shorten(errors[0].Message)}");
            }

            Check("两个线程都没抛异常", errors.Count == 0);
            Check("总共 100 条", store.LoadMessages(session.Id).Count == 100);
            Check("MessageCount = 100", store.Get(session.Id)?.MessageCount == 100);

            var seqs = RawRows(concurrentPath, "SELECT seq FROM messages WHERE session_id = $id ORDER BY seq;", session.Id)
                .Select(row => int.Parse(row[0]!, CultureInfo.InvariantCulture))
                .ToList();
            Check("seq 无重复（100 个互不相同）", seqs.Count == 100 && seqs.Distinct().Count() == 100);
            Check("seq 连续 1..100（MAX(seq)+1 没漏号）", seqs.Count == 100 && seqs[0] == 1 && seqs[^1] == 100);
            Check("每条内容都读得回来（没有互相踩）",
                store.LoadMessages(session.Id).All(m => m.Content is not null && m.Content.StartsWith("线程", StringComparison.Ordinal)));
        }
    }

    // ==================== 收尾：删临时目录 + 护栏 ====================

    private static void Cleanup(string root, string realDatabase, string realDatabaseBefore)
    {
        // 自测自己开的连接用的是默认连接池，池里的连接会一直攥着 db 文件句柄，
        // 不把它们清掉，下面 Directory.Delete 会以"文件被占用"失败。
        // （被测的 SqliteSessionStore 连接串里已经关了池化，不受影响。）
        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }

                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150); // 文件句柄偶尔要晚一点才放开，重试几次
            }
        }

        Section("收尾");
        Check("跑完不留垃圾：临时目录已删干净", !Directory.Exists(root));
        Check($"护栏：没碰用户真实库 {realDatabase}", Snapshot(realDatabase) == realDatabaseBefore);
    }

    /// <summary>文件指纹（存在性 + 大小 + 最后写入时间），用来证明"全程没动过它"。</summary>
    private static string Snapshot(string path)
    {
        var info = new FileInfo(path);
        return !info.Exists ? "<不存在>" : $"{info.Length}@{info.LastWriteTimeUtc.Ticks}";
    }

    // ==================== 断言与汇总 ====================

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"---- {title} ----");
    }

    private static void Check(string what, bool ok)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {what}");
    }

    private static void Fail(string what)
    {
        _checks++;
        _failures++;
        Console.WriteLine($"    [FAIL] {what}");
    }

    /// <summary>跑一段动作，把抛出来的异常还给调用方（没抛就是 null），断言用。</summary>
    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static string Shorten(string text, int limit = 110) =>
        text.Length <= limit ? text : text[..limit] + "…";

    /// <summary>有没有被劈成半个的代理对（emoji 被截断的典型症状）。</summary>
    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    // ==================== 直接查库（绕开被测代码的读取路径） ====================

    /// <summary>用一条独立连接直接读库，验证"落盘细节"；$id 有值时绑定会话 id。</summary>
    private static List<string?[]> RawRows(string dbPath, string sql, string? sessionId = null)
    {
        var rows = new List<string?[]>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false, // 用完就真关，别让池里的连接攥着 db 文件不放（收尾要删临时目录）
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sessionId is not null)
        {
            command.Parameters.AddWithValue("$id", sessionId);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string ReadJournalMode(string dbPath)
    {
        var rows = RawRows(dbPath, "PRAGMA journal_mode;");
        return rows.Count == 0 ? "<空>" : rows[0][0] ?? "<null>";
    }
}
