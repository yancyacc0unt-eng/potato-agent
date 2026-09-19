// Win32 层的门面：整个层只从这里进来。
//
// DPI 那一条护栏在这里落地：进程必须在【最早期】声明 per-monitor-v2。
// 两道保险：
//   1) 模块初始化器 —— 只要有人碰到本程序集里的任何类型，就先把 DPI 声明掉，
//      早于任何窗口 / GDI / 坐标调用（这是类库能做到的最早期）；
//   2) PotatoAgentWin32.Initialize() —— 宿主进程在 Main 里第一件事调它，语义最清楚。
// 两者都幂等，谁先到都行。

using System.Runtime.CompilerServices;

namespace PotatoAgent.Win32;

public static class PotatoAgentWin32
{
    /// <summary>本层标识，便于日志区分来源。</summary>
    public const string LayerName = "PotatoAgent.Win32";

    /// <summary>程序集被加载后、任何本层代码真正执行之前，先把 DPI 感知声明掉。</summary>
    // CA2255 说模块初始化器"只该给应用程序代码用"。这里是有意为之：
    /// 类库没法知道宿主什么时候才会调 Initialize()，而 DPI 声明必须抢在最前面，
    /// 所以两道保险都要有（另一道是显式的 Initialize()）。
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void DeclareDpiOnModuleLoad() => Dpi.Declare();
#pragma warning restore CA2255

    /// <summary>
    /// 宿主进程应在 Main 的最开头调用（在任何窗口 / GDI / 坐标调用之前）。
    /// 返回实际生效的 DPI 感知级别；重复调用无副作用。
    /// </summary>
    public static string Initialize() => Dpi.Declare();

    /// <summary>实际生效的 DPI 感知级别：per-monitor-v2 / per-monitor / system / unaware。</summary>
    public static string DpiAwareness => Dpi.Awareness;

    /// <summary>本层自述，一行。</summary>
    public static string Describe() => $"{LayerName} · dpi={Dpi.Awareness} · focus-gate=on-by-default";
}
