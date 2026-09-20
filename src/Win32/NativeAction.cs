// 动作层新增的 Win32 调用（感知层的那些在 Native.cs 里，这个文件不动它）。
//
// 三类：
//   * 输入注入 —— SendInput（键鼠一律走它，不用 mouse_event / keybd_event 那套老 API）
//   * 窗口聚焦 —— ShowWindow / SetForegroundWindow / AttachThreadInput
//   * 周边 —— ShellExecuteEx（启动程序）、权限探测（是否提权 / 是否锁屏）、菜单项矩形

using System.Runtime.InteropServices;

namespace PotatoAgent.Win32;

internal static class NativeAction
{
    // ---------------- SendInput ----------------

    internal const uint INPUT_MOUSE = 0;
    internal const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    internal const uint MOUSEEVENTF_MOVE = 0x0001;
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x1000;
    internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    /// <summary>绝对坐标以整个虚拟桌面为参照（多显示器必需）。</summary>
    internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    /// <summary>直接把 wScan 当成一个 UTF-16 码元打出去 —— 中文只能靠这条路。</summary>
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    internal const int WHEEL_DELTA = 120;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>字符 → (VK + 需要的修饰键状态)，按当前键盘布局。返回 -1 表示这个字符打不出来。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "VkKeyScanW")]
    internal static extern short VkKeyScan(char character);

    [DllImport("user32.dll")]
    internal static extern int GetDoubleClickTime();

    /// <summary>某个线程当前生效的键盘布局（低 16 位是 LANGID，例如 0x0804 中文 / 0x0409 英文）。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint threadId);

    internal static int InputSize => Marshal.SizeOf<INPUT>();

    // ---------------- 窗口聚焦 ----------------

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNORMAL = 1;
    internal const int SW_SHOWMINIMIZED = 2;
    internal const int SW_SHOWMAXIMIZED = 3;
    /// <summary>按当前尺寸与位置显示并激活；不会像 SW_RESTORE 那样把最大化窗口变回普通大小。</summary>
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWMINNOACTIVE = 7;
    internal const int SW_SHOWNA = 8;
    internal const int SW_RESTORE = 9;

    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    /// <summary>
    /// 读/改"前台锁定超时"。默认 200ms 内不许抢焦点，这正是 SetForegroundWindow 会静默失败的原因。
    /// 临时改成 0 是已知且可逆的解法，用完必须还原（见 FocusGuard 的 finally）。
    /// </summary>
    internal static bool TryReadForegroundLockTimeout(out uint timeout)
    {
        timeout = 0;
        return SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref timeout, 0);
    }

    internal static bool SetForegroundLockTimeout(uint timeout) =>
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)(int)timeout, SPIF_SENDCHANGE);

    // ---------------- 关窗口 ----------------

    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_SYSCOMMAND = 0x0112;
    /// <summary>系统菜单里的"关闭" —— 和点标题栏的 × 同一条路。</summary>
    internal const int SC_CLOSE = 0xF060;

    /// <summary>
    /// 投递一条窗口消息（不等待目标处理）。关窗口走它，不走 Alt+F4 ——
    /// 本机实测 Win11 记事本（WinUI）不响应注入的 Alt+F4，却老实响应 WM_CLOSE。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    // ---------------- 菜单（点击验证用：菜单项的真实屏幕矩形） ----------------

    [DllImport("user32.dll")]
    internal static extern IntPtr GetMenu(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr menu, uint item, out RECT rect);

    // ---------------- 权限天花板探测 ----------------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    internal const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out TOKEN_ELEVATION info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int GetProcessId(IntPtr handle);

    /// <summary>某个进程是否以管理员身份运行。读不到就返回 null（不知道 ≠ 没提权）。</summary>
    internal static bool? IsElevated(int pid)
    {
        IntPtr process = pid > 0 ? OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) : GetCurrentProcess();
        if (process == IntPtr.Zero) return null;

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
            try
            {
                if (!GetTokenInformation(token, TokenElevation, out TOKEN_ELEVATION info, Marshal.SizeOf<TOKEN_ELEVATION>(), out _)) return null;
                return info.TokenIsElevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally
        {
            if (pid > 0) CloseHandle(process);
        }
    }

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>
    /// 当前"输入桌面"是不是我们自己所在的那一个。
    /// 锁屏 / UAC 弹窗时，输入桌面会切成安全桌面，此时注入必然石沉大海 —— 提前问出来，别硬试。
    /// </summary>
    internal static bool InputDesktopAccessible()
    {
        IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop);
        return true;
    }

    // ---------------- 启动进程 ----------------

    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x0000_0040;
    internal const uint SEE_MASK_NOASYNC = 0x0000_0100;
    internal const uint SEE_MASK_FLAG_NO_UI = 0x0000_0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ShellExecuteExW")]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
}
