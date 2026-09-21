using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PotatoAgent.Core.Brain;

namespace PotatoAgent.Sessions;

/// <summary>SQLite 实现：单库 %APPDATA%\PotatoAgent\data.db（SPEC 4.4）。</summary>
/// <remarks>
/// <para>
/// <b>用法</b>：<c>new SqliteSessionStore(path)</c> → <see cref="Initialize"/>（必须显式调一次）→ 其余读写。
/// 构造完不调 <see cref="Initialize"/> 就用别的成员会抛 <see cref="InvalidOperationException"/>，
/// 免得界面拿到一个"看起来能用的空库"。
/// </para>
/// <para>
/// <b>线程安全</b>：所有公开操作都在内部 <c>lock</c> 里串行化，并且<b>每次操作单开一条连接、用完即关</b>
/// （连接串里关掉了池化），不跨线程共用一个 <see cref="SqliteConnection"/>。同一个实例可以被多线程直接用。
/// </para>
/// <para>
/// <b>异常</b>：库文件不是 SQLite（坏库 / 被替换）时 <see cref="Initialize"/> 抛
/// <see cref="InvalidDataException"/>，<b>绝不静默重建</b> —— 自动重建等于把用户历史抹了。
/// 其余读写失败按原样抛（<see cref="SqliteException"/> 等），由界面层兜底提示。
/// </para>
/// <para>
/// <b>还没有 FTS5</b>：会话搜索功能未定，本轮不建 <c>messages_fts</c> 虚表（避免留一张没人用的死表）。
/// 以后要加全文检索，就在 <see cref="Initialize"/> 的建表语句后面补一段
/// <c>CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(content, content='messages', content_rowid='id');</c>
/// 加同步触发器即可，老库会自动补上。
/// </para>
/// </remarks>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    /// <summary>SQLite 文件头（16 字节），用来在打开之前认出"这文件根本不是 SQLite 库"。</summary>
    private static readonly byte[] SqliteFileHeader = "SQLite format 3\0"u8.ToArray();

    /// <summary>建表语句。幂等：每次 <see cref="Initialize"/> 都能整段重跑。</summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions(
          id TEXT PRIMARY KEY, title TEXT NOT NULL, workspace TEXT NULL,
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS messages(
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
          seq INTEGER NOT NULL,
          role TEXT NOT NULL,
          content TEXT NULL,
          payload TEXT NOT NULL,
          created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_messages_session ON messages(session_id, seq);
        """;

    private readonly object _gate = new();
    private readonly string _connectionString;

    /// <summary>最近一次发出去的时间戳，保证同一实例内时间严格递增（见 <see cref="NextTimestamp"/>）。</summary>
    private DateTimeOffset _lastTimestamp = DateTimeOffset.MinValue;

    private bool _initialized;
    private bool _disposed;

    /// <summary>建一个仓库实例（只记路径，不碰磁盘）。</summary>
    /// <param name="databasePath">
    /// 库文件路径；null / 空白时用 <c>%APPDATA%\PotatoAgent\data.db</c>。
    /// 自测 / 多环境要传自己的临时路径，别写用户的真实库。
    /// </param>
    public SqliteSessionStore(string? databasePath = null)
    {
        DatabasePath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath) ? DefaultDatabasePath() : databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 关掉连接池：池化会让进程一直攥着文件句柄，删库 / 换库 / 备份时非常讨厌，
            // 而会话读写的频率低到完全不需要池化。
            Pooling = false,

            // 万一有别的进程（例如另一个实例）正在写，等一会儿再报 BUSY，而不是立刻失败。
            DefaultTimeout = 15,
        }.ToString();
    }

    /// <summary>默认库位置：<c>%APPDATA%\PotatoAgent\data.db</c>。</summary>
    private static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PotatoAgent", "data.db");

    /// <summary>这个实例实际使用的库文件绝对路径（自测用它确认没写错地方）。</summary>
    public string DatabasePath { get; }

    /// <summary>建表。幂等，重复调用无副作用；<b>必须显式调用一次</b>，之后才能读写。</summary>
    /// <exception cref="InvalidDataException">库文件存在但不是 SQLite（坏库 / 被别的程序覆写）时抛出，不会被自动重建。</exception>
    public void Initialize()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            EnsureExistingFileIsSqlite();

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = SchemaSql;
                command.ExecuteNonQuery();
            }
            catch (SqliteException error)
            {
                throw new InvalidDataException(
                    $"会话库无法使用：{DatabasePath}。它可能不是 SQLite 文件、已损坏、或被别的程序独占" +
                    $"（SQLite 错误 {error.SqliteErrorCode}：{error.Message}）。" +
                    "为免抹掉已有数据，这里不做自动重建 —— 请先手工备份，再删除该文件重试。",
                    error);
            }

            _initialized = true;
        }
    }

    /// <summary>列出所有会话元数据，按 UpdatedAt 倒序。</summary>
    public IReadOnlyList<SessionRecord> List()
    {
        lock (_gate)
        {
            EnsureUsable();

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = RecordSelectSql + " ORDER BY s.updated_at DESC, s.id ASC;";

            using var reader = command.ExecuteReader();
            var sessions = new List<SessionRecord>();
            while (reader.Read())
            {
                sessions.Add(ReadRecord(reader));
            }

            return sessions;
        }
    }

    /// <summary>新建一个空会话并落库。</summary>
    public SessionRecord Create(string? title = null, string? workspace = null)
    {
        lock (_gate)
        {
            EnsureUsable();

            var now = NextTimestamp();
            var record = new SessionRecord
            {
                Id = NewId(),
                Title = string.IsNullOrWhiteSpace(title) ? SessionTitle.DefaultTitle : title.Trim(),
                Workspace = NormalizeWorkspace(workspace),
                CreatedAt = now,
                UpdatedAt = now,
                MessageCount = 0,
            };

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO sessions(id, title, workspace, created_at, updated_at) " +
                "VALUES($id, $title, $workspace, $created, $updated);";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$workspace", (object?)record.Workspace ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", ToStorage(record.CreatedAt));
            command.Parameters.AddWithValue("$updated", ToStorage(record.UpdatedAt));
            command.ExecuteNonQuery();

            return record;
        }
    }

    /// <summary>按 id 取会话元数据；不存在返回 null。</summary>
    public SessionRecord? Get(string id)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = RecordSelectSql + " WHERE s.id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRecord(reader) : null;
        }
    }

    /// <summary>读出一个会话的全部消息，按 seq 升序；会话不存在返回空表。</summary>
    public IReadOnlyList<ChatMessage> LoadMessages(string id)
    {
        lock (_gate)
        {
            EnsureUsable();

            var messages = new List<ChatMessage>();
            if (string.IsNullOrEmpty(id))
            {
                return messages;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM messages WHERE session_id = $id ORDER BY seq ASC;";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ChatMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<ChatMessage>(reader.GetString(0));
                }
                catch (JsonException)
                {
                    // 单行 payload 烂掉不该让整段历史打不开，跳过它继续读后面的。
                    message = null;
                }

                if (message is not null)
                {
                    messages.Add(message);
                }
            }

            return messages;
        }
    }

    /// <summary>追加一批消息并落盘，同时刷新会话的 UpdatedAt。</summary>
    /// <exception cref="InvalidOperationException">目标会话不存在时抛出，不会隐式建会话。</exception>
    public void Append(string id, IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_gate)
        {
            EnsureUsable();

            // 清洗（去掉图片）在这里做：调用方手里那条带图的消息不受影响，照旧能继续喂模型。
            var batch = new List<ChatMessage>();
            foreach (var message in messages)
            {
                if (message is not null)
                {
                    batch.Add(SessionMessageSanitizer.Sanitize(message));
                }
            }

            if (batch.Count == 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException("Append 收到空的会话 id。");
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (!SessionExists(connection, transaction, id))
            {
                throw new InvalidOperationException($"会话不存在：{id}。Append 不会隐式建会话，请先 Create()。");
            }

            var seq = LastSeq(connection, transaction, id);
            var now = ToStorage(NextTimestamp());

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO messages(session_id, seq, role, content, payload, created_at) " +
                    "VALUES($sid, $seq, $role, $content, $payload, $created);";

                var pSession = insert.Parameters.Add("$sid", SqliteType.Text);
                var pSeq = insert.Parameters.Add("$seq", SqliteType.Integer);
                var pRole = insert.Parameters.Add("$role", SqliteType.Text);
                var pContent = insert.Parameters.Add("$content", SqliteType.Text);
                var pPayload = insert.Parameters.Add("$payload", SqliteType.Text);
                var pCreated = insert.Parameters.Add("$created", SqliteType.Text);

                foreach (var message in batch)
                {
                    pSession.Value = id;
                    pSeq.Value = ++seq;
                    pRole.Value = message.Role ?? "user";
                    pContent.Value = (object?)Summarize(message) ?? DBNull.Value;
                    pPayload.Value = JsonSerializer.Serialize(message);
                    pCreated.Value = now;
                    insert.ExecuteNonQuery();
                }
            }

            using (var touch = connection.CreateCommand())
            {
                touch.Transaction = transaction;
                touch.CommandText = "UPDATE sessions SET updated_at = $updated WHERE id = $id;";
                touch.Parameters.AddWithValue("$updated", now);
                touch.Parameters.AddWithValue("$id", id);
                touch.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>改标题并刷新 UpdatedAt；标题空白或会话不存在返回 false。</summary>
    public bool Rename(string id, string title)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET title = $title, updated_at = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$title", title.Trim());
            command.Parameters.AddWithValue("$updated", ToStorage(NextTimestamp()));
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>删除会话，消息靠 ON DELETE CASCADE 一起删。</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>绑定 / 解绑工作区；只动元数据，不刷新 UpdatedAt（换目录不算"会话有新内容"）。</summary>
    public bool SetWorkspace(string id, string? workspace)
    {
        lock (_gate)
        {
            EnsureUsable();
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET workspace = $workspace WHERE id = $id;";
            command.Parameters.AddWithValue("$workspace", (object?)NormalizeWorkspace(workspace) ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>释放实例。之后再用这个实例会抛 <see cref="ObjectDisposedException"/>（数据已经落盘，不受影响）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _initialized = false;
        }
    }

    // ==================== 内部实现 ====================

    /// <summary>列表 / 单条共用的 SELECT 前缀。</summary>
    private const string RecordSelectSql =
        "SELECT s.id, s.title, s.workspace, s.created_at, s.updated_at, " +
        "       (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.id) AS message_count " +
        "FROM sessions s";

    /// <summary>开一条新连接并设好逐连接的 PRAGMA。</summary>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        // 逐连接设置（不是建库时一次性）：外键 ON 是级联删除的前提，WAL 让读不挡写。
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>打开之前先认文件头：不是 SQLite 就当场抛异常，别让 SQLite 把它当空库用。</summary>
    private void EnsureExistingFileIsSqlite()
    {
        var info = new FileInfo(DatabasePath);
        if (!info.Exists || info.Length == 0)
        {
            // 不存在 / 0 字节 = 全新库，SQLite 自己会写头部。
            return;
        }

        var header = new byte[SqliteFileHeader.Length];
        int read;
        using (var stream = new FileStream(
                   DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        // 真有内容的 SQLite 库至少一个 512 字节的页，所以"短于 100 字节"一定是垃圾。
        if (info.Length < 100 || read < header.Length || !header.AsSpan().SequenceEqual(SqliteFileHeader))
        {
            throw new InvalidDataException(
                $"会话库不是有效的 SQLite 文件：{DatabasePath}（{info.Length} 字节，文件头对不上）。" +
                "为免抹掉已有数据，这里不做自动重建 —— 请先手工备份，再删除该文件重试。");
        }
    }

    /// <summary>把一行读成 <see cref="SessionRecord"/>。</summary>
    private static SessionRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Workspace = reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedAt = FromStorage(reader.GetString(3)),
        UpdatedAt = FromStorage(reader.GetString(4)),
        MessageCount = (int)reader.GetInt64(5),
    };

    /// <summary>12 位小写短 id。</summary>
    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>空白的 workspace 一律归一成 null，免得库里同时存在 null 和 ""。</summary>
    private static string? NormalizeWorkspace(string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim();

    /// <summary>落盘用的时间格式：ISO 8601 "o"，UTC 偏移，字典序 == 时间序。</summary>
    private static string ToStorage(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>读回落盘的时间。</summary>
    private static DateTimeOffset FromStorage(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// 下一个时间戳：取当前 UTC 时间，但保证严格大于上一次发出去的值。
    /// </summary>
    /// <remarks>
    /// Windows 的时钟精度只有毫秒级（甚至 15.6ms），"创建完立刻改名"很容易落在同一毫秒里，
    /// 那样 <c>UpdatedAt</c> 就不再"变新"、列表排序也会不稳。这里用单调递增兜住。
    /// </remarks>
    private DateTimeOffset NextTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        if (now <= _lastTimestamp)
        {
            now = _lastTimestamp.AddTicks(1);
        }

        _lastTimestamp = now;
        return now;
    }

    /// <summary>messages.content 只是给人看的纯文本摘要（图片占位也算文本），真正的数据在 payload。</summary>
    private static string? Summarize(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
        {
            return message.Content;
        }

        if (message.Parts is not { Count: > 0 })
        {
            return null;
        }

        var texts = new List<string>();
        foreach (var part in message.Parts)
        {
            if (!string.IsNullOrEmpty(part?.Text))
            {
                texts.Add(part!.Text!);
            }
        }

        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    /// <summary>会话在不在（Append 的前置检查）。</summary>
    private static bool SessionExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sessions WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>当前会话内最大的 seq（没有消息就是 0）；写入时从它开始 +1。</summary>
    private static long LastSeq(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM messages WHERE session_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>确认本实例可用（已 Initialize 且没被 Dispose）。</summary>
    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("会话库还没初始化：请先调用一次 Initialize() 再读写。");
        }
    }
}
