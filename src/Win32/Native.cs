// Win32 互操作 —— 只保留 ctrl-computer 真正用到的调用。
// 搬运自 dsh_plugins/ctrl-computer/daemon/Native.cs，命名空间改为 PotatoAgent.Win32。
//
// 坐标系约定：本进程在入口处声明 Per-Monitor-V2 DPI 感知，所以
// 鼠标坐标、窗口矩形、屏幕截图三者共用同一个坐标系 —— 物理屏幕像素。
// 这一点是整个插件可靠性的地基：如果进程是 DPI-unaware 的，
// 1920x1080 的屏幕会被报告成 1280x720，所有点击都会系统性偏掉。

using System.Runtime.InteropServices;
using System.Text;

namespace PotatoAgent.Win32;

/// <summary>Win32 RECT（左上右下，单位：物理像素）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool IsEmpty => Width <= 0 || Height <= 0;

    public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;

    public override readonly string ToString() => $"({X},{Y})";
}

/// <summary>
/// 进程 DPI 感知状态。必须在任何窗口 / GDI / 坐标调用之前声明一次，
/// 声明后全生命周期只读 —— 属于"地基已经浇好，别再动它"的那类状态。
/// </summary>
public static class Dpi
{
    private static readonly object Gate = new();
    private static string _awareness = "undeclared";

    /// <summary>实际生效的 DPI 感知级别：per-monitor-v2 / per-monitor / system / unaware。</summary>
    public static string Awareness
    {
        get { lock (Gate) return _awareness; }
    }

    /// <summary>是否已经声明过（无论成功与否，只声明一次）。</summary>
    public static bool Declared
    {
        get { lock (Gate) return _awareness != "undeclared"; }
    }

    /// <summary>
    /// 幂等声明。由 <see cref="PotatoAgentWin32.Initialize"/> 与模块初始化器共同调用，
    /// 无论谁先到都只真正执行一次。
    /// </summary>
    public static string Declare()
    {
        lock (Gate)
        {
            if (_awareness != "undeclared") return _awareness;
            _awareness = Native.EnablePerMonitorDpi();
            return _awareness;
        }
    }
}

internal static class Native
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x0000_0080;
    internal const uint PW_RENDERFULLCONTENT = 0x0000_0002;

    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private static readonly IntPtr DpiAwarenessPerMonitor = new(-3);
    private static readonly IntPtr DpiAwarenessSystem = new(-2);
    private static readonly IntPtr DpiAwarenessUnaware = new(-1);

    /// <summary>
    /// 声明进程 DPI 感知级别，必须在任何窗口 / GDI / 坐标调用之前执行一次。
    /// </summary>
    /// <returns>实际生效的级别，便于在 state 里如实报告。</returns>
    internal static string EnablePerMonitorDpi()
    {
        if (SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2)) return "per-monitor-v2";
        if (SetProcessDpiAwarenessContext(DpiAwarenessPerMonitor)) return "per-monitor";
        if (SetProcessDPIAware()) return "system";

        // 三个都失败，说明进程在更早的时候（宿主 manifest / WPF / WinForms 初始化）
        // 已经声明过了。这时回读真实级别，而不是谎报成 unaware。
        return CurrentDpiAwareness();
    }

    /// <summary>回读当前线程真正生效的 DPI 感知级别（不听 API 的返回值，直接问系统）。</summary>
    internal static string CurrentDpiAwareness()
    {
        IntPtr context = GetThreadDpiAwarenessContext();
        if (AreDpiAwarenessContextsEqual(context, DpiAwarenessPerMonitorV2)) return "per-monitor-v2";
        if (AreDpiAwarenessContextsEqual(context, DpiAwarenessPerMonitor)) return "per-monitor";
        if (AreDpiAwarenessContextsEqual(context, DpiAwarenessSystem)) return "system";
        if (AreDpiAwarenessContextsEqual(context, DpiAwarenessUnaware)) return "unaware";
        return GetAwarenessFromDpiAwarenessContext(context) switch
        {
            0 => "unaware",
            1 => "system",
            2 => "per-monitor",
            _ => "unknown",
        };
    }

    /// <summary>整个虚拟桌面的边界，多显示器时起点可能是负数。</summary>
    internal static RECT VirtualScreen() => new()
    {
        Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
        Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
        Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
        Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN),
    };

    internal static int SystemDpi() => GetDpiForSystem();

    internal static string WindowText(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    internal static int ProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    /// <summary>
    /// 窗口的真实可见边界。优先用 DWM 的扩展边框：它排除了 Win10 之后
    /// 窗口四周那圈不可见的调整边框，最接近"用户看到的那块矩形"。
    /// </summary>
    internal static RECT WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0 && !frame.IsEmpty)
            return frame;
        return GetWindowRect(hwnd, out RECT rect) ? rect : default;
    }

    /// <summary>被 DWM 隐藏的窗口（UWP 挂起、虚拟桌面上不显示的窗口）。</summary>
    internal static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    internal static long WindowStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE) : GetWindowLong32(hwnd, GWL_EXSTYLE);

    /// <summary>客户区尺寸（宽高）。</summary>
    internal static bool ClientSize(IntPtr hwnd, out RECT client) => GetClientRect(hwnd, out client);

    /// <summary>客户区在屏幕坐标系里的位置（点(clientX,clientY) 换算成屏幕坐标要用它）。</summary>
    internal static bool ClientOrigin(IntPtr hwnd, out POINT origin)
    {
        origin = default;
        if (!GetClientRect(hwnd, out RECT client)) return false;
        var point = new POINT { X = client.Left, Y = client.Top };
        if (!ClientToScreen(hwnd, ref point)) return false;
        origin = point;
        return true;
    }

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    // ---- user32 ----
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(IntPtr hwnd, int index);

    // ---- dwmapi ----
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
