namespace PotatoAgent.Workspaces;

/// <summary>
/// 一个工作区 = 用户选的一个文件夹。
/// </summary>
/// <remarks>
/// <para><b>什么时候用</b>：界面要显示"当前工作区 / 最近打开"的某一项时，直接拿它的
/// <see cref="Path"/> 和 <see cref="Name"/> 就行，不需要再去碰磁盘。</para>
/// <para>这是<b>不可变</b>的值对象（属性全是 <c>init</c>）：建好之后不会再变，
/// 所以可以放心地在多个线程之间传、也可以长期持有（列表快照里存的就是这些实例）。</para>
/// <para>它只描述"哪个文件夹"，不负责读写 —— 落盘与最近列表见 <see cref="WorkspaceStore"/>。</para>
/// </remarks>
public sealed class Workspace
{
    /// <summary>
    /// 工作区文件夹的绝对路径：已用 <see cref="Path.GetFullPath(string)"/> 规范化、并去掉了尾部分隔符
    /// （根目录 <c>C:\</c> 例外，去掉会变成"当前盘当前目录"）。
    /// </summary>
    /// <remarks>比较两个工作区是不是同一个文件夹，一律用它 + <see cref="StringComparer.OrdinalIgnoreCase"/>。</remarks>
    public required string Path { get; init; }

    /// <summary>
    /// 给界面显示的名字，就是 <see cref="Path"/> 的最后一段（目录名）。
    /// </summary>
    /// <remarks>写入磁盘时也会存一份，纯粹是为了让 <c>workspaces.json</c> 用记事本打开时人眼看得出是哪个目录。</remarks>
    public required string Name { get; init; }

    /// <summary>
    /// 最后一次打开这个工作区的时间（本地时区偏移，落盘是 ISO 8601 字符串）。
    /// </summary>
    /// <remarks>用来给"最近打开"排序；从坏文件里勉强读出来的条目可能是默认值 <c>default</c>（排最后）。</remarks>
    public DateTimeOffset LastOpenedAt { get; init; }
}
