// 文件工具的门面：界面层只需要跟这两个方法打交道。
//
//   CreateAll()   -> 拿六个 ITool 实例（想自己塞进别的注册表时用）
//   RegisterAll() -> 直接注册进 ToolRegistry（App.xaml.cs 用的是这个）
//
// workspaceRoot 是一个回调而不是字符串：工作区随时会被用户换掉 / 关掉，
// 回调每次都问一次"现在的工作区是哪"，工具就不必跟着重建。

using PotatoAgent.Core.Tools;

namespace PotatoAgent.Files;

/// <summary>
/// 文件读写工具的入口：一次创建 / 注册六个 <c>file_*</c> 工具。
/// </summary>
/// <remarks>
/// <para>六个工具：<c>file_list</c>（列举）、<c>file_read</c>（读文本）、<c>file_write</c>（写/追加）、
/// <c>file_copy</c>（复制）、<c>file_move</c>（移动/改名）、<c>file_delete</c>（删进回收站）。</para>
/// <para>
/// 路径规则见 <see cref="FilePaths"/>：绝对路径原样用，相对路径拼到工作区根下，
/// 相对路径 + 没有工作区 = 明确报错。工具返回的文本里一律给解析后的绝对路径。
/// </para>
/// <para>
/// 危险等级：只有 <c>file_list</c> / <c>file_read</c> 是 <see cref="ToolRisk.Safe"/>，
/// 写/复制/移动是 <see cref="ToolRisk.Confirm"/>，删除是 <see cref="ToolRisk.Dangerous"/>
/// —— 界面按等级弹确认框，工具自己不做二次拦截。
/// </para>
/// </remarks>
public static class FileTools
{
    /// <summary>
    /// 创建全部六个文件工具（不注册）。
    /// </summary>
    /// <param name="workspaceRoot">
    /// 工作区回调：返回当前工作区根目录，<b>没有工作区时返回 null</b>（调用时机是每次工具调用，不是创建时）。
    /// 传 null 表示永远没有工作区，此时工具只接受绝对路径。
    /// </param>
    /// <returns>六个工具，顺序为 list / read / write / copy / move / delete。</returns>
    public static IReadOnlyList<ITool> CreateAll(Func<string?>? workspaceRoot = null) => new ITool[]
    {
        new FileListTool(workspaceRoot),
        new FileReadTool(workspaceRoot),
        new FileWriteTool(workspaceRoot),
        new FileCopyTool(workspaceRoot),
        new FileMoveTool(workspaceRoot),
        new FileDeleteTool(workspaceRoot),
    };

    /// <summary>
    /// 把六个文件工具注册进 <paramref name="registry"/>。
    /// </summary>
    /// <param name="registry">目标注册表，不能为 null。</param>
    /// <param name="workspaceRoot">工作区回调，同 <see cref="CreateAll"/>。</param>
    /// <param name="skipExisting">
    /// true = 已经存在同名工具就跳过（返回真正注册成功的个数，适合重复装配）；
    /// false = 重名直接抛 <see cref="ArgumentException"/>（默认，早炸早好）。
    /// </param>
    /// <returns>本次真正注册成功的工具个数（正常是 6）。</returns>
    public static int RegisterAll(ToolRegistry registry, Func<string?>? workspaceRoot = null, bool skipExisting = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var count = 0;
        foreach (var tool in CreateAll(workspaceRoot))
        {
            if (skipExisting)
            {
                if (registry.TryRegister(tool))
                {
                    count++;
                }
            }
            else
            {
                registry.Register(tool);
                count++;
            }
        }

        return count;
    }
}
