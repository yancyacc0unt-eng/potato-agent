// 桌面状态与窗口清单 —— "不截图就能拿到信息"的第一层，成本几乎为零。
//
// 这一层回答的是：现在前台是谁、鼠标在哪、屏幕多大、桌面上有哪些窗口。
// 有了它，绝大多数"先截个图看看"的冲动都可以取消。
//
// 搬运自 dsh_plugins/ctrl-computer/daemon/Desktop.cs，命名空间改为 PotatoAgent.Win32。
// 唯一的改动：原来读 Program.DpiAwareness，现在读 Dpi.Awareness（Program.cs 没有搬过来，
// 它的 stdio / --serve / Usage 属于命令行外壳，不属于这一层）。
// 另外补了三个给动作层用的定位助手：Foreground / FindByPid / WaitFor。

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace PotatoAgent.Win32;

public static class Desktop
{
    /// <summary>一次调用回答所有"我现在在哪"的问题。</summary>
    public static JsonObject State(JsonObject args)
    {
        Native.GetCursorPos(out POINT cursor);
        var foreground = Native.GetForegroundWindow();

        var monitors = new JsonArray();
        foreach (var screen in Screen.AllScreens)
        {
            monitors.Add(new JsonObject
            {
                ["device"] = screen.DeviceName,
                ["primary"] = screen.Primary,
                ["bounds"] = J.Rect(screen.Bounds),
            });
        }

        return new JsonObject
        {
            ["screen"] = new JsonObject
            {
                ["virtualBounds"] = J.Rect(Native.VirtualScreen()),
                ["monitorCount"] = Screen.AllScreens.Length,
                ["monitors"] = monitors,
                ["dpiScale"] = Math.Round(Native.SystemDpi() / 96.0, 3),
                ["dpiAwareness"] = Dpi.Awareness,
            },
            ["cursor"] = new JsonObject { ["x"] = cursor.X, ["y"] = cursor.Y },
            ["foreground"] = DescribeWindow(foreground),
        };
    }

    /// <summary>
    /// 可见的顶层窗口，按 Z 序（最上层在前）。默认过滤掉隐藏窗口、
    /// DWM 藏起来的窗口（挂起的 UWP）和工具窗口（没有任务栏按钮的辅助窗口）。
    /// </summary>
    public static JsonObject Windows(JsonObject args)
    {
        string? filter = args.Text("filter");
        int limit = Math.Clamp(args.Int("limit", 40), 1, 500);
        var foreground = Native.GetForegroundWindow();
        var matches = new List<JsonObject>();
        int total = 0;

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.IsCloaked(hwnd)) return true;
            if ((Native.WindowStyle(hwnd) & Native.WS_EX_TOOLWINDOW) != 0) return true;

            string title = Native.WindowText(hwnd);
            if (title.Length == 0) return true;
            if (filter is { Length: > 0 } && !title.Contains(filter, StringComparison.OrdinalIgnoreCase)) return true;

            total++;
            if (matches.Count < limit) matches.Add(DescribeWindow(hwnd, hwnd == foreground));
            return true;
        }, IntPtr.Zero);

        return new JsonObject
        {
            ["count"] = total,
            ["returned"] = matches.Count,
            ["truncated"] = total > matches.Count,
            ["windows"] = J.Array(matches),
        };
    }

    public static JsonObject DescribeWindow(IntPtr hwnd) =>
        DescribeWindow(hwnd, hwnd == Native.GetForegroundWindow());

    public static JsonObject DescribeWindow(IntPtr hwnd, bool isForeground)
    {
        if (hwnd == IntPtr.Zero) return new JsonObject { ["none"] = true };

        var bounds = Native.WindowBounds(hwnd);
        int pid = Native.ProcessId(hwnd);

        string process = "unknown";
        try
        {
            if (pid > 0) process = Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            // 提权进程或已退出的进程读不到名字；这不是错误，保持 unknown。
        }

        return new JsonObject
        {
            ["hwnd"] = "0x" + hwnd.ToInt64().ToString("X8"),
            ["title"] = Native.WindowText(hwnd),
            ["process"] = process,
            ["pid"] = pid,
            ["class"] = Native.ClassName(hwnd),
            ["bounds"] = J.Rect(bounds),
            ["minimized"] = Native.IsIconic(hwnd),
            ["maximized"] = Native.IsZoomed(hwnd),
            ["foreground"] = isForeground,
            ["monitor"] = MonitorIndex(bounds),
        };
    }

    // ---------- 以下为动作层新增的定位助手（原来只有 JSON 版，命令行外壳才能用） ----------

    /// <summary>当前前台窗口。锁屏 / UAC 安全桌面上会返回 Zero。</summary>
    public static IntPtr Foreground() => Native.GetForegroundWindow();

    /// <summary>窗口的真实可见边界（DWM 扩展边框优先），物理屏幕像素。</summary>
    public static RECT Bounds(IntPtr hwnd) => Native.WindowBounds(hwnd);

    /// <summary>
    /// 窗口客户区在屏幕坐标系里的矩形。要把"客户区里的某个位置"换算成可点击的屏幕
    /// 坐标时用它 —— 左上角就是换算原点。
    /// </summary>
    public static RECT ClientBounds(IntPtr hwnd)
    {
        if (!Native.ClientOrigin(hwnd, out POINT origin)) return default;
        if (!Native.ClientSize(hwnd, out RECT client)) return default;

        return new RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height,
        };
    }

    /// <summary>鼠标当前位置（物理屏幕像素）。</summary>
    public static POINT Cursor()
    {
        Native.GetCursorPos(out POINT point);
        return point;
    }

    /// <summary>一句话描述一个窗口，用于日志与报错（标题为空时退化成类名）。</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";

        string title = Native.WindowText(hwnd);
        string className = Native.ClassName(hwnd);
        int pid = Native.ProcessId(hwnd);

        string process = "unknown";
        try
        {
            if (pid > 0) process = Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            // 同 DescribeWindow：读不到就保持 unknown。
        }

        string label = title.Length > 0 ? title : className;
        return $"0x{hwnd.ToInt64():X8} \"{label}\" [{process}#{pid}]";
    }

    /// <summary>窗口所属进程 pid。</summary>
    public static int ProcessId(IntPtr hwnd) => Native.ProcessId(hwnd);

    /// <summary>
    /// 窗口所在线程当前生效的键盘布局 LANGID（低 16 位）：0x0804 = 简体中文，0x0409 = 英文(美国)。
    /// 排查"注入的键被输入法吃掉"这类问题时要看它。
    /// </summary>
    public static int KeyboardLayoutId(IntPtr hwnd)
    {
        uint thread = Native.GetWindowThreadProcessId(hwnd, out _);
        return (int)(NativeAction.GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
    }

    /// <summary>
    /// 当前所有"看得见"的顶层窗口（可见、未被 DWM 隐藏、非工具窗口、有标题）。
    /// 用来在启动程序前后拍快照，只认新出现的那个窗口 —— 免得把用户已经开着的同名窗口认成自己的。
    /// </summary>
    public static List<IntPtr> VisibleWindows()
    {
        var windows = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.IsCloaked(hwnd)) return true;
            if ((Native.WindowStyle(hwnd) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.WindowText(hwnd).Length == 0) return true;
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>按 pid 找它第一个可见的顶层窗口。</summary>
    public static IntPtr FindByPid(int pid)
    {
        IntPtr match = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.ProcessId(hwnd) != pid) return true;
            if (Native.WindowText(hwnd).Length == 0) return true;
            match = hwnd;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    /// <summary>按标题片段找第一个可见的顶层窗口（大小写不敏感）。</summary>
    public static IntPtr FindByTitle(string titlePart)
    {
        IntPtr match = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            string title = Native.WindowText(hwnd);
            if (title.Length == 0) return true;
            if (!title.Contains(titlePart, StringComparison.OrdinalIgnoreCase)) return true;
            match = hwnd;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    /// <summary>轮询等待某个窗口出现；超时返回 Zero。等待不是重试，最多轮询 timeoutMs。</summary>
    public static IntPtr WaitFor(Func<IntPtr, bool> match, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((hwnd, _) =>
            {
                if (found != IntPtr.Zero) return false;
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.WindowText(hwnd).Length == 0) return true;
                if (!match(hwnd)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero) return found;
            if (Environment.TickCount64 >= deadline) return IntPtr.Zero;
            Thread.Sleep(100);
        }
    }

    /// <summary>窗口落在哪个显示器上：取相交面积最大的那个。</summary>
    private static int MonitorIndex(RECT bounds)
    {
        var screens = Screen.AllScreens;
        int best = -1, bestArea = 0;

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i].Bounds;
            int width = Math.Min(bounds.Right, screen.Right) - Math.Max(bounds.Left, screen.Left);
            int height = Math.Min(bounds.Bottom, screen.Bottom) - Math.Max(bounds.Top, screen.Top);
            int area = width > 0 && height > 0 ? width * height : 0;
            if (area > bestArea)
            {
                bestArea = area;
                best = i;
            }
        }

        return best;
    }
}
