using System.Security;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PotatoAgent.Workspaces;

/// <summary>
/// 当前工作区 + 最近打开列表，落盘 <c>%APPDATA%\PotatoAgent\workspaces.json</c>。
/// </summary>
/// <remarks>
/// <para><b>什么时候用</b>：界面启动时 new 一个（可以用默认位置，也可以指到别的目录做测试），
/// 先 <see cref="Load"/> 拿回上次的状态；用户选完文件夹调 <see cref="TrySetCurrent"/>；
/// 订阅 <see cref="Changed"/> 在"当前工作区/最近列表"变化时刷新界面。</para>
/// <para><b>磁盘出问题绝不炸界面</b>：<see cref="Load"/> 遇到文件不存在 / JSON 坏 / 字段坏，一律降级成
/// "没有工作区"并正常返回；<see cref="Save"/> 失败只返回 <c>false</c>（内存里的状态照旧能用，只是重启后可能丢）。</para>
/// <para><b>线程安全</b>：界面线程和其他线程都可以随时读 <see cref="Current"/> / <see cref="Recent"/>，
/// 内部有锁；<see cref="Recent"/> 每次返回的是<b>快照副本</b>，外部改不动内部列表。</para>
/// <para><b>路径比较</b>一律按 Windows 的规矩走 <see cref="StringComparer.OrdinalIgnoreCase"/>（大小写不敏感），
/// 所以 <c>C:\Work</c> 和 <c>c:\work\</c> 会被认成同一个工作区，不会在最近列表里出现两条。</para>
/// </remarks>
public sealed class WorkspaceStore
{
    /// <summary>落盘文件名（放在配置目录下）。</summary>
    private const string FileName = "workspaces.json";

    /// <summary><see cref="Recent"/> 最多保留几条（<b>不含</b> <see cref="Current"/>）。</summary>
    public const int MaxRecentCount = 10;

    /// <summary>写盘用：camelCase + 缩进，让人能拿记事本直接看、直接改。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文路径别被转义成 \uXXXX，否则记事本里没法看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读盘用：宽容一点（大小写不敏感、允许注释和尾逗号），手改过的文件也能读回来。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private Workspace? _current;
    private List<Workspace> _recent = new();

    /// <summary>
    /// 建一个工作区仓库。构造时<b>不会</b>读盘，要拿上次的状态请自己再调一次 <see cref="Load"/>。
    /// </summary>
    /// <param name="configDirectory">
    /// 配置目录；传 <c>null</c>（默认）用 <see cref="DefaultDirectory"/> = <c>%APPDATA%\PotatoAgent</c>。
    /// 自测 / 临时隔离时传一个临时目录即可，那样就完全不会碰用户的真实工作区列表。
    /// </param>
    /// <remarks>
    /// 默认位置和 <c>ConfigStore</c> 一样走 <see cref="Environment.SpecialFolder.ApplicationData"/>，
    /// 所以 <c>workspaces.json</c> 和 <c>config.json</c> 始终住在同一个目录里。
    /// 注意它<b>不认</b> <c>APPDATA</c> 环境变量（Windows 的已知文件夹 API 行为），要隔离只能像参数说明那样显式传目录。
    /// </remarks>
    public WorkspaceStore(string? configDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(configDirectory) ? DefaultDirectory : configDirectory!.Trim();
        ConfigFilePath = Path.Combine(NormalizeDirectory(directory), FileName);
    }

    /// <summary>默认配置目录：<c>%APPDATA%\PotatoAgent</c>（和 <c>config.json</c> 同一个目录）。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PotatoAgent");

    /// <summary>本实例实际使用的 <c>workspaces.json</c> 完整路径（排查"东西写哪去了"时看它）。</summary>
    public string ConfigFilePath { get; }

    /// <summary>当前工作区；还没选过（或读盘降级成空）时为 <c>null</c>。</summary>
    public Workspace? Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>
    /// 最近打开的工作区列表：最近打开的在前，最多 <see cref="MaxRecentCount"/> 条，<b>不含</b> <see cref="Current"/>。
    /// </summary>
    /// <remarks>每次读都返回一份新的快照，调用方随便遍历；想改列表只能通过 <see cref="TrySetCurrent"/>。</remarks>
    public IReadOnlyList<Workspace> Recent
    {
        get { lock (_gate) { return _recent.ToArray(); } }
    }

    /// <summary>
    /// 当前工作区或最近列表发生任何变化之后触发（<see cref="Load"/> 不算），在调用方的线程上同步触发。
    /// </summary>
    /// <remarks>
    /// 界面订阅它来刷新列表 —— 不要在这里面做重活（它在 <see cref="TrySetCurrent"/> / <see cref="ClearCurrent"/>
    /// 的调用线程上跑，而且是写盘之后才触发）。
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>
    /// 读盘，把 <see cref="Current"/> / <see cref="Recent"/> 换成文件里的状态。<b>永不抛异常</b>。
    /// </summary>
    /// <remarks>
    /// <para>文件不存在 / 不是合法 JSON / 结构或字段坏掉 / 读不动（权限、路径太长）→ 一律降级成
    /// "没有工作区"（<see cref="Current"/> 为 <c>null</c>、<see cref="Recent"/> 为空）。</para>
    /// <para>读坏文件时<b>不会</b>去覆盖或删除原文件，留给用户自己修。</para>
    /// <para>单个条目坏掉（路径是空串之类）只丢那一条，不影响其它条目；
    /// 另外会顺手去重、按打开时间从新到旧排序、并做一次 <see cref="MaxRecentCount"/> 截断，
    /// 所以手改过的文件读进来也一定是规整的。</para>
    /// <para>本方法<b>不</b>触发 <see cref="Changed"/>：它属于"启动时初始化"，订阅者此时通常还没挂上。</para>
    /// </remarks>
    public void Load()
    {
        Workspace? current = null;
        var recent = new List<Workspace>();

        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var text = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                var file = JsonSerializer.Deserialize<WorkspaceFileDto>(text, ReadOptions);

                if (file is not null)
                {
                    current = ToWorkspace(file.Current);

                    if (file.Recent is not null)
                    {
                        foreach (var entry in file.Recent)
                        {
                            var workspace = ToWorkspace(entry);
                            if (workspace is null)
                            {
                                continue;
                            }

                            // 去重（含"跟 Current 重复"），手改出来的重复条目在这里被清掉
                            if (current is not null && PathEquals(current.Path, workspace.Path))
                            {
                                continue;
                            }

                            if (recent.Exists(w => PathEquals(w.Path, workspace.Path)))
                            {
                                continue;
                            }

                            recent.Add(workspace);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            // 文件不存在 / JSON 坏 / 字段坏 / 读不动 → 降级成"没有工作区"，界面照常起得来
            current = null;
            recent.Clear();
        }

        // OrderByDescending 是稳定排序：时间一样时保持文件里的顺序（写盘时就是最新在前）
        recent = recent.OrderByDescending(w => w.LastOpenedAt).ToList();
        if (recent.Count > MaxRecentCount)
        {
            recent.RemoveRange(MaxRecentCount, recent.Count - MaxRecentCount);
        }

        lock (_gate)
        {
            _current = current;
            _recent = recent;
        }
    }

    /// <summary>
    /// 把某个文件夹设为当前工作区，并把它记进最近列表（原来的当前工作区会被推到最近列表最前面）。
    /// </summary>
    /// <param name="directoryPath">文件夹路径；相对路径、带尾分隔符、大小写不同都行，内部会规范化。</param>
    /// <param name="error">
    /// 成功时为 <c>null</c>；失败时是一句<b>英文</b>说明（界面直接显示，例如 <c>That folder does not exist.</c>）。
    /// </param>
    /// <returns>成功 <c>true</c>；路径不存在或不是文件夹时 <c>false</c>，此时<b>什么都不改</b>（Current / Recent / 磁盘原样）。</returns>
    /// <remarks>
    /// <para>成功的动作顺序：设 <see cref="Current"/> → 旧的当前工作区进 <see cref="Recent"/> → 写盘 → 触发 <see cref="Changed"/>。</para>
    /// <para>同一个文件夹重复设置<b>不会</b>在 <see cref="Recent"/> 里出现两条（会被去重），
    /// 且 <see cref="Recent"/> 里永远不含 <see cref="Current"/>。</para>
    /// <para>写盘失败（无权限等）也算成功：内存里的切换已经生效、本次会话照常能用，只是重启后可能回到旧状态。</para>
    /// </remarks>
    public bool TrySetCurrent(string directoryPath, out string? error)
    {
        error = null;

        var path = NormalizePath(directoryPath);
        if (path is null)
        {
            error = "That folder path is empty or not valid.";
            return false;
        }

        // 文件要排在"目录不存在"前面报，否则用户会拿到一句看不懂的"目录不存在"
        if (File.Exists(path))
        {
            error = "That path is a file, not a folder.";
            return false;
        }

        if (!Directory.Exists(path))
        {
            error = "That folder does not exist.";
            return false;
        }

        var workspace = new Workspace
        {
            Path = path,
            Name = DirectoryNameOf(path),
            LastOpenedAt = DateTimeOffset.Now,
        };

        lock (_gate)
        {
            var previous = _current;

            // 同一个文件夹只留一条：先把这个路径从最近列表里拿掉（重复设置时不产生重复）
            _recent.RemoveAll(w => PathEquals(w.Path, path));

            if (previous is not null && !PathEquals(previous.Path, path))
            {
                _recent.Insert(0, previous);
                if (_recent.Count > MaxRecentCount)
                {
                    _recent.RemoveRange(MaxRecentCount, _recent.Count - MaxRecentCount);
                }
            }

            _current = workspace;
        }

        Save();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 清掉当前工作区（回到"还没选文件夹"的状态），写盘并触发 <see cref="Changed"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="Recent"/> <b>保留</b> —— 用户只是想换个工作区时，最近列表还在，方便再点回去。
    /// 本来就没有当前工作区时也会照常写盘 + 触发一次（对订阅者来说"状态刷新一下"没有坏处）。
    /// </remarks>
    public void ClearCurrent()
    {
        lock (_gate)
        {
            _current = null;
        }

        Save();
        RaiseChanged();
    }

    /// <summary>
    /// 把内存里的状态原子写到 <see cref="ConfigFilePath"/>：先写同目录的 <c>.tmp</c>，再覆盖正式文件。
    /// </summary>
    /// <returns>成功 <c>true</c>；失败（无权限、路径太长、磁盘满…）<c>false</c>，<b>不抛异常</b>。</returns>
    /// <remarks>
    /// <para>写 <c>.tmp</c> 再替换是为了中途断电 / 崩溃时不会留下半截 JSON —— 要么是旧内容，要么是新内容。</para>
    /// <para>配置目录不存在会自动创建（首次运行时 <c>%APPDATA%\PotatoAgent</c> 可能还没有）。</para>
    /// <para>失败时会把残留的 <c>.tmp</c> 尽量清掉；清不掉也不影响返回值。本方法<b>不</b>触发 <see cref="Changed"/>。</para>
    /// </remarks>
    public bool Save()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(
                new WorkspaceFileDto
                {
                    Current = _current is null ? null : ToEntry(_current),
                    Recent = _recent.Select(ToEntry).ToList(),
                },
                WriteOptions);
        }

        var tempPath = ConfigFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(ConfigFilePath))
            {
                File.Replace(tempPath, ConfigFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, ConfigFilePath);
            }

            return true;
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    // ==================== 内部工具 ====================

    /// <summary>写盘用的 JSON 结构；读回来时字段全可空，坏条目单独丢掉而不是整份作废。</summary>
    private sealed class WorkspaceFileDto
    {
        /// <summary>当前工作区，没选过就是 null。</summary>
        public WorkspaceEntryDto? Current { get; set; }

        /// <summary>最近打开列表（最新在前）。</summary>
        public List<WorkspaceEntryDto>? Recent { get; set; }
    }

    /// <summary>单个工作区条目。</summary>
    private sealed class WorkspaceEntryDto
    {
        /// <summary>文件夹绝对路径。</summary>
        public string? Path { get; set; }

        /// <summary>目录名（纯粹为了文件好读，读回来时会按路径重算）。</summary>
        public string? Name { get; set; }

        /// <summary>最后打开时间。</summary>
        public DateTimeOffset? LastOpenedAt { get; set; }
    }

    /// <summary>触发 <see cref="Changed"/>（在调用方线程上同步触发）。</summary>
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>落盘条目 → 内存对象。</summary>
    private static WorkspaceEntryDto ToEntry(Workspace workspace) => new()
    {
        Path = workspace.Path,
        Name = workspace.Name,
        LastOpenedAt = workspace.LastOpenedAt,
    };

    /// <summary>
    /// 磁盘条目 → 内存对象；路径为空 / 非法时返回 <c>null</c>（调用方丢掉这一条）。
    /// 名字一律按路径重算，保证 <see cref="Workspace.Name"/> 永远是"最后一段目录名"这个不变量。
    /// </summary>
    private static Workspace? ToWorkspace(WorkspaceEntryDto? entry)
    {
        if (entry is null)
        {
            return null;
        }

        var path = NormalizePath(entry.Path);
        if (path is null)
        {
            return null;
        }

        return new Workspace
        {
            Path = path,
            Name = DirectoryNameOf(path),
            LastOpenedAt = entry.LastOpenedAt ?? default,
        };
    }

    /// <summary>配置目录规范化（去尾分隔符）；转不成绝对路径就原样用，反正后面还有兜底。</summary>
    private static string NormalizeDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            return directory;
        }
    }

    /// <summary>
    /// 路径规范化：绝对化 + 去尾部分隔符（<c>C:\</c> 这种盘根保留尾分隔符）；空串 / 非法路径返回 <c>null</c>。
    /// </summary>
    private static string? NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
            return full.Length == 0 ? null : full;
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            return null;
        }
    }

    /// <summary>取目录名（最后一段）；盘根这种取不出来的就用路径本身顶替，界面至少不显示空白。</summary>
    private static string DirectoryNameOf(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Windows 上路径一律大小写不敏感地比较。</summary>
    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断是不是"磁盘 / 文件内容"这一类可以安全吞掉的异常。
    /// 只吞这一类，别的问题（比如自己代码里的 <c>NullReferenceException</c>）照样抛出来。
    /// </summary>
    private static bool IsFileProblem(Exception ex) =>
        ex is IOException                  // 含 DirectoryNotFound / PathTooLong / FileNotFound
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException       // 含 PlatformNotSupported
            or ArgumentException
            or SecurityException
            or FormatException
            or InvalidOperationException;

    /// <summary>尽量删掉写了一半的 <c>.tmp</c>；删不掉也不吭声（返回值已经说明失败了）。</summary>
    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            // 用户目录里留一个 .tmp 比"因为清理失败而炸掉界面"好得多
        }
    }
}
