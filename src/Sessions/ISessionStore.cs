using PotatoAgent.Core.Brain;

namespace PotatoAgent.Sessions;

/// <summary>会话仓库。界面 / 自测只依赖这个接口。</summary>
/// <remarks>
/// <para>
/// 契约：<b>所有</b> 方法都不接受 null 集合，失败一律抛异常，不做静默兜底；
/// 但"查不到"（Get / LoadMessages）用 null / 空表表达，不算失败。
/// </para>
/// <para>
/// 实现可以是 SQLite（<see cref="SqliteSessionStore"/>），也可以是内存假实现 ——
/// 界面层不要 <c>new SqliteSessionStore(...)</c>，要拿注入进来的 <see cref="ISessionStore"/>。
/// </para>
/// </remarks>
public interface ISessionStore
{
    /// <summary>列出所有会话元数据，按 <see cref="SessionRecord.UpdatedAt"/> 倒序（最近动过的在最前）。</summary>
    /// <returns>只含元数据，不含消息；空库返回空表。</returns>
    IReadOnlyList<SessionRecord> List();

    /// <summary>新建一个空会话并立刻落库。</summary>
    /// <param name="title">标题；null / 空白时用 <see cref="SessionTitle.DefaultTitle"/>（"New chat"）。</param>
    /// <param name="workspace">绑定的工作区根路径；null / 空白表示不绑。</param>
    /// <returns>刚创建的记录，消息数为 0。</returns>
    SessionRecord Create(string? title = null, string? workspace = null);

    /// <summary>按 id 取会话元数据。</summary>
    /// <param name="id">会话 id。</param>
    /// <returns>不存在（或 id 为空）时返回 null。</returns>
    SessionRecord? Get(string id);

    /// <summary>读出一个会话的全部消息，按写入顺序（seq 升序）返回。</summary>
    /// <param name="id">会话 id。</param>
    /// <returns>不存在时返回空表（不是 null）。</returns>
    IReadOnlyList<ChatMessage> LoadMessages(string id);

    /// <summary>
    /// 追加一批消息并落盘，同时刷新会话的 <see cref="SessionRecord.UpdatedAt"/>。
    /// </summary>
    /// <param name="id">目标会话 id；会话不存在时抛 <see cref="InvalidOperationException"/>（不隐式建会话）。</param>
    /// <param name="messages">要追加的消息，按顺序写；空集合是 no-op。落盘前会过一遍 <see cref="SessionMessageSanitizer"/>。</param>
    void Append(string id, IEnumerable<ChatMessage> messages);

    /// <summary>改标题并刷新 <see cref="SessionRecord.UpdatedAt"/>（列表会因此把它排到最前）。</summary>
    /// <param name="id">会话 id。</param>
    /// <param name="title">新标题；空白标题视为无效请求，直接返回 false。</param>
    /// <returns>真的改到了返回 true；会话不存在返回 false。</returns>
    bool Rename(string id, string title);

    /// <summary>删除会话，连同它的全部消息一起删（不可撤销）。</summary>
    /// <param name="id">会话 id。</param>
    /// <returns>真的删掉了返回 true；会话不存在返回 false。</returns>
    bool Delete(string id);

    /// <summary>绑定 / 解绑工作区。只改元数据，不动 <see cref="SessionRecord.UpdatedAt"/>。</summary>
    /// <param name="id">会话 id。</param>
    /// <param name="workspace">工作区根路径；null / 空白表示解绑（写 NULL）。</param>
    /// <returns>真的改到了返回 true；会话不存在返回 false。</returns>
    bool SetWorkspace(string id, string? workspace);
}
