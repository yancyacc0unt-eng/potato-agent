namespace PotatoAgent.Sessions;

/// <summary>一个会话的元数据（不含消息本体）。</summary>
/// <remarks>
/// <para>
/// 列表页 / 会话切换只读这个记录，所以它刻意不带 <see cref="PotatoAgent.Core.Brain.ChatMessage"/>：
/// 否则刷一次列表就要把所有会话的全部历史读进内存。
/// </para>
/// <para>消息本体用 <see cref="ISessionStore.LoadMessages"/> 单独取。</para>
/// </remarks>
public sealed class SessionRecord
{
    /// <summary>12 位小写短 id（<c>Guid.NewGuid().ToString("N")</c> 前 12 位），也是数据库主键。</summary>
    public required string Id { get; init; }

    /// <summary>标题，非空。新建时是 <c>"New chat"</c>，之后由首条用户消息生成或用户改名决定。</summary>
    public required string Title { get; init; }

    /// <summary>会话绑定的工作区根路径；没有绑定工作区时为 null。</summary>
    public string? Workspace { get; init; }

    /// <summary>创建时间（UTC 时刻；落库用 ISO 8601 的 "o" 格式往返）。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。追加消息或改名会刷新它，<see cref="ISessionStore.List"/> 按它倒序。</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// 消息条数。由 messages 表现场统计，不是独立字段 —— 不会出现"计数和历史对不上"的脏数据。
    /// </summary>
    public int MessageCount { get; init; }
}
