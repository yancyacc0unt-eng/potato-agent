// 联网工具的门面：界面层只跟这两个方法打交道。
//
//   CreateAll()   -> 拿工具实例（想自己塞进别的注册表时用）
//   RegisterAll() -> 直接注册进 ToolRegistry（App.xaml.cs 用的是这个）
//
// 目前只有一个工（web_search），但门面照样按"一整套"写：
// 以后加 web_fetch（抓正文）时只改这个文件，App.xaml.cs 一行都不用动。

using PotatoAgent.Core.Tools;

namespace PotatoAgent.Web;

/// <summary>
/// 联网工具的入口：创建 / 注册 <c>web_search</c>。
/// </summary>
/// <remarks>
/// 依赖方向是 Web → Core，符合本项目唯一允许的那条引用方向。
/// </remarks>
public static class WebTools
{
    /// <summary>
    /// 创建全部联网工具（不注册）。
    /// </summary>
    /// <returns>工具列表，目前只有 <c>web_search</c>。</returns>
    public static IReadOnlyList<ITool> CreateAll() => new ITool[]
    {
        new WebSearchTool(),
    };

    /// <summary>
    /// 把联网工具注册进 <paramref name="registry"/>。
    /// </summary>
    /// <param name="registry">目标注册表，不能为 null。</param>
    /// <param name="skipExisting">
    /// true = 已经有同名工具就跳过，返回真正注册成功的个数（适合重复装配）；
    /// false = 重名直接抛 <see cref="ArgumentException"/>（默认，早炸早好）。
    /// </param>
    /// <returns>本次真正注册成功的工具个数（正常情况下是 1）。</returns>
    public static int RegisterAll(ToolRegistry registry, bool skipExisting = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var registered = 0;

        foreach (var tool in CreateAll())
        {
            if (skipExisting)
            {
                if (registry.TryRegister(tool))
                {
                    registered++;
                }
            }
            else
            {
                registry.Register(tool);
                registered++;
            }
        }

        return registered;
    }
}
