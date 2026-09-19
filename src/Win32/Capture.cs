// 截图 —— "用眼睛"的那条通道。搬运自 dsh_plugins/ctrl-computer/daemon/Capture.cs，
// 命名空间改为 PotatoAgent.Win32，并按本项目的要求做了两处结构性改动：
//
//   1) 拆掉「落盘 + JSON 外壳」：核心方法一律【直接返回 Bitmap】，调用方拿到就用；
//      要存盘的时候另外调用 Capture.Save(...)（可选，不是必经之路）。
//   2) 元数据不再包成 JsonObject，而是通过 out ShotInfo 给出 —— 坐标换算关系
//      （图片像素 ↔ 屏幕物理像素）仍然保留，因为它是"图上看到的点怎么换算成
//      可点击坐标"的唯一依据，丢了它这张图就没法用。
//
// 三条老规矩没变：
//   * 坐标可解释：每张图都带 capture 矩形与 scaleX/scaleY；
//   * 成本可控制：可以只截某个窗口或某块区域，可以缩放；
//   * 失败可降级：PrintWindow 抓不到内容（DirectX / 受保护内容）时自动退回屏幕抓取。

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace PotatoAgent.Win32;

/// <summary>截图选项。</summary>
public sealed class ShotOptions
{
    /// <summary>输出缩放：1.0 = 原始物理像素。取值会被夹到 [0.05, 1.0]，不放大。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>是否在光标位置画一个红圈准星（默认开）。</summary>
    public bool MarkCursor { get; init; } = true;

    public static ShotOptions Default { get; } = new();
}

/// <summary>一张截图的坐标关系与其他事实，用于把图上的点换算回屏幕坐标。</summary>
/// <param name="Capture">这张图覆盖的屏幕物理像素矩形。</param>
/// <param name="ScaleX">图片像素 / 屏幕像素。screenX = Capture.Left + imageX / ScaleX。</param>
/// <param name="ScaleY">同上，纵向。</param>
/// <param name="Via">screen = 屏幕抓取；printwindow = 窗口自绘。</param>
/// <param name="Clipped">请求区域是否被屏幕边界裁剪过。</param>
/// <param name="CursorMarked">是否画了光标准星。</param>
/// <param name="Cursor">截图时的鼠标位置。</param>
/// <param name="ElapsedMs">耗时。</param>
public readonly record struct ShotInfo(
    RECT Capture,
    double ScaleX,
    double ScaleY,
    string Via,
    bool Clipped,
    bool CursorMarked,
    POINT Cursor,
    long ElapsedMs);

public static class Capture
{
    /// <summary>整个虚拟桌面（多显示器时含所有屏幕）。调用方负责 Dispose。</summary>
    public static Bitmap FullScreen(ShotOptions? options = null) =>
        Grab(Native.VirtualScreen(), IntPtr.Zero, options, out _);

    /// <summary>某一个显示器。</summary>
    public static Bitmap Monitor(int index, ShotOptions? options = null)
    {
        var screens = Screen.AllScreens;
        if (index < 0 || index >= screens.Length)
            throw new ArgumentException($"monitor {index} does not exist; this machine has {screens.Length} monitor(s), valid indexes are 0..{screens.Length - 1}");

        return Grab(FromRectangle(screens[index].Bounds), IntPtr.Zero, options, out _);
    }

    /// <summary>
    /// 单个窗口。优先让窗口自己渲染自己（PrintWindow），抓不到内容时退回屏幕抓取。
    /// 窗口最小化时没有可见像素，直接抛错而不是返回一张黑图。
    /// </summary>
    public static Bitmap Window(IntPtr hwnd, ShotOptions? options = null)
    {
        if (!Native.IsWindow(hwnd))
            throw new ArgumentException("the given handle is not a window");
        if (Native.IsIconic(hwnd))
            throw new InvalidOperationException($"window \"{Native.WindowText(hwnd)}\" is minimized; it has no visible pixels — capture the screen instead");

        return Grab(Native.WindowBounds(hwnd), hwnd, options, out _);
    }

    /// <summary>屏幕上的任意矩形（物理像素）。</summary>
    public static Bitmap Region(RECT rect, ShotOptions? options = null) =>
        Grab(rect, IntPtr.Zero, options, out _);

    /// <summary>带坐标元数据的窗口截图。</summary>
    public static Bitmap Window(IntPtr hwnd, ShotOptions? options, out ShotInfo info)
    {
        if (!Native.IsWindow(hwnd)) throw new ArgumentException("the given handle is not a window");
        if (Native.IsIconic(hwnd)) throw new InvalidOperationException($"window \"{Native.WindowText(hwnd)}\" is minimized");
        return Grab(Native.WindowBounds(hwnd), hwnd, options, out info);
    }

    /// <summary>核心实现：抓一块屏幕区域，可选让指定窗口自绘，可选缩放与标记光标。</summary>
    public static Bitmap Grab(RECT rect, IntPtr window, ShotOptions? options, out ShotInfo info)
    {
        long started = Environment.TickCount64;
        options ??= ShotOptions.Default;

        var virtualScreen = Native.VirtualScreen();

        // 只截屏幕上真实存在的部分，避免 BitBlt 出来一圈黑边。
        var clipped = Intersect(rect, virtualScreen);
        if (clipped.IsEmpty)
            throw new ArgumentException($"the requested area {rect} lies outside the screen {virtualScreen}");
        bool wasClipped = clipped.Left != rect.Left || clipped.Top != rect.Top || clipped.Right != rect.Right || clipped.Bottom != rect.Bottom;
        rect = clipped;

        string via = window != IntPtr.Zero ? "printwindow" : "screen";

        // ⚠ 这里【不能】写成 using var raw：
        // 不缩放时 image 就是 raw 本身，方法一返回 using 就把它 Dispose 掉了，
        // 调用方拿到的是一张"已经死了"的 Bitmap —— 读 Width 直接
        // ArgumentException: Parameter is not valid.（本机实测踩过）
        // 所以只有换成了缩放图之后才手动 Dispose 原图，其余情况所有权交给调用方。
        Bitmap raw = window != IntPtr.Zero ? GrabWindow(window, rect, ref via) : GrabScreen(rect);

        double scale = Math.Clamp(options.Scale, 0.05, 1.0);
        Bitmap image = raw;
        if (scale < 0.999)
        {
            int width = Math.Max(1, (int)Math.Round(raw.Width * scale));
            int height = Math.Max(1, (int)Math.Round(raw.Height * scale));
            var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(raw, new Rectangle(0, 0, width, height));
            }
            raw.Dispose();
            image = scaled;
        }

        double scaleX = (double)image.Width / rect.Width;
        double scaleY = (double)image.Height / rect.Height;

        bool cursorMarked = false;
        Native.GetCursorPos(out POINT cursor);
        if (options.MarkCursor && Contains(rect, cursor.X, cursor.Y))
        {
            MarkCursor(image, (cursor.X - rect.Left) * scaleX, (cursor.Y - rect.Top) * scaleY);
            cursorMarked = true;
        }

        info = new ShotInfo(rect, scaleX, scaleY, via, wasClipped, cursorMarked, cursor, Environment.TickCount64 - started);
        return image;
    }

    /// <summary>
    /// 可选的存盘。format：png（默认）/ jpeg / jpg。返回写出的完整路径。
    /// 目录不存在会自动建；这一步失败会抛异常，不静默吞掉。
    /// </summary>
    public static string Save(Bitmap image, string path, string format = "png", int quality = 85)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));
        if (!Path.IsPathRooted(path)) throw new ArgumentException($"path must be absolute, got \"{path}\"", nameof(path));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (format.ToLowerInvariant() is "jpeg" or "jpg") SaveJpeg(image, path, Math.Clamp(quality, 10, 100));
        else image.Save(path, ImageFormat.Png);

        return path;
    }

    private static Bitmap GrabScreen(RECT rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// 优先让窗口自己渲染自己（PrintWindow + PW_RENDERFULLCONTENT，对 Chromium/UWP 有效）。
    /// 失败或者抓到一张纯色空图时，退回屏幕抓取 —— 后者至少保证拿到"用户看到的东西"。
    /// </summary>
    private static Bitmap GrabWindow(IntPtr window, RECT rect, ref string via)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        bool printed;

        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                printed = Native.PrintWindow(window, hdc, Native.PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        if (printed && !LooksBlank(bitmap)) return bitmap;

        bitmap.Dispose();
        via = "screen";
        return GrabScreen(rect);
    }

    /// <summary>抽样判断是否是"没抓到内容"的纯色图。只采样 256 个点，代价可忽略。</summary>
    private static bool LooksBlank(Bitmap bitmap)
    {
        int first = 0;
        for (int row = 1; row <= 16; row++)
        {
            for (int column = 1; column <= 16; column++)
            {
                int pixel = bitmap.GetPixel(bitmap.Width * column / 17, bitmap.Height * row / 17).ToArgb();
                if (first == 0) first = pixel;
                else if (pixel != first) return false;
            }
        }
        return true;
    }

    /// <summary>在光标位置画一个红色十字准星：让"鼠标在哪"在不看屏幕时也一目了然。</summary>
    private static void MarkCursor(Bitmap image, double x, double y)
    {
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(230, 255, 40, 40), 2f);

        const int radius = 14;
        float cx = (float)x, cy = (float)y;
        graphics.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
        graphics.DrawLine(pen, cx - radius - 8, cy, cx - radius + 4, cy);
        graphics.DrawLine(pen, cx + radius - 4, cy, cx + radius + 8, cy);
        graphics.DrawLine(pen, cx, cy - radius - 8, cx, cy - radius + 4);
        graphics.DrawLine(pen, cx, cy + radius - 4, cx, cy + radius + 8);
    }

    private static void SaveJpeg(Bitmap image, string path, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        image.Save(path, encoder, parameters);
    }

    private static RECT Intersect(RECT a, RECT b) => new()
    {
        Left = Math.Max(a.Left, b.Left),
        Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right),
        Bottom = Math.Min(a.Bottom, b.Bottom),
    };

    private static bool Contains(RECT rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    private static RECT FromRectangle(Rectangle rect) => new()
    {
        Left = rect.Left,
        Top = rect.Top,
        Right = rect.Right,
        Bottom = rect.Bottom,
    };
}
