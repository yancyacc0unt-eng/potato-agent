using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_screenshot</c>：真的看一眼屏幕 —— 截图会作为图片回灌给模型，不只是存个文件。
/// </summary>
/// <remarks>
/// <para>只读，<see cref="ToolRisk.Safe"/>。</para>
/// <para>
/// 返回的 <see cref="ToolResult"/> 带一张 <see cref="ToolImage"/>，AgentSession 会把它转成
/// <c>image_url</c>（data URI）塞进下一轮请求，所以模型看到的是真图。
/// </para>
/// <para>结果文本里带完整的坐标换算关系（图片像素 → 屏幕物理像素），模型据此才能算出该点哪里。</para>
/// </remarks>
public sealed class PcScreenshotTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_screenshot";

    /// <inheritdoc />
    public string Description =>
        "Take a screenshot and LOOK at it - the image is returned to you as a picture, so you can read text, " +
        "recognize buttons and judge layout. Captures the whole screen by default; pass 'region' for a rectangle, " +
        "'monitor' for one display, or 'hwnd'/'window' for a single window. " +
        "Use 'scale' (0.05-1.0) to shrink the picture when you only need an overview - " +
        "a full 1920x1080 screen at scale 1.0 is a large image. " +
        "The result text tells you the exact mapping from image pixels back to screen coordinates, " +
        "which is what pc_click expects.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "scale": {
              "type": "number",
              "description": "Output scale, 0.05 to 1.0 (default 1.0). Values above 1.0 are clamped; the image is never enlarged."
            },
            "mark_cursor": {
              "type": "boolean",
              "description": "Draw a red crosshair at the mouse pointer (default true). Turn it off for a clean picture."
            },
            "region": {
              "type": "object",
              "description": "Capture only this rectangle, in physical screen pixels.",
              "properties": {
                "x": { "type": "integer" },
                "y": { "type": "integer" },
                "width": { "type": "integer" },
                "height": { "type": "integer" }
              },
              "required": ["x", "y", "width", "height"]
            },
            "monitor": {
              "type": "integer",
              "description": "Capture one monitor by index (0 = primary). Ignored when 'region' or 'hwnd' is given."
            },
            "hwnd": {
              "type": "string",
              "description": "Handle of a single window to capture, as returned by pc_windows (e.g. \"0x00123456\")."
            },
            "window": {
              "type": "string",
              "description": "Case-insensitive substring of a window title to capture instead of giving hwnd."
            }
          },
          "required": []
        }
        """;

    /// <inheritdoc />
    public ToolRisk Risk => ToolRisk.Safe;

    /// <inheritdoc />
    public Task<ToolResult> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var options = new ShotOptions
        {
            Scale = Math.Clamp(args.Number("scale") ?? 1.0, 0.05, 1.0),
            MarkCursor = args.Flag("mark_cursor", true),
        };

        var window = IntPtr.Zero;
        RECT rect;
        string what;

        var hasHandle = !string.IsNullOrWhiteSpace(args.Text("hwnd")) || !string.IsNullOrWhiteSpace(args.Text("window"));
        if (hasHandle)
        {
            if (!ToolWindow.TryResolve(args, out window, out var windowError))
            {
                return Task.FromResult(ToolResult.Error(windowError!));
            }

            if (Native.IsIconic(window))
            {
                return Task.FromResult(ToolResult.Error(
                    $"window {Desktop.Describe(window)} is minimized, so it has no visible pixels. " +
                    "Restore it first, or capture the whole screen."));
            }

            rect = Desktop.Bounds(window);
            what = $"window {Desktop.Describe(window)}";
        }
        else if (args.Object("region") is { } region)
        {
            var x = region.Integer("x") ?? 0;
            var y = region.Integer("y") ?? 0;
            var width = region.Integer("width") ?? 0;
            var height = region.Integer("height") ?? 0;

            if (width <= 0 || height <= 0)
            {
                return Task.FromResult(ToolResult.Error("region.width and region.height must both be greater than 0."));
            }

            rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
            what = $"region ({x},{y}) {width}x{height}";
        }
        else if (args.Integer("monitor") is { } monitorIndex)
        {
            var screens = Screen.AllScreens;
            if (monitorIndex < 0 || monitorIndex >= screens.Length)
            {
                return Task.FromResult(ToolResult.Error(
                    $"monitor {monitorIndex} does not exist; this machine has {screens.Length} monitor(s), valid indexes are 0..{screens.Length - 1}."));
            }

            var bounds = screens[monitorIndex].Bounds;
            rect = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
            what = $"monitor {monitorIndex} ({screens[monitorIndex].DeviceName})";
        }
        else
        {
            rect = Native.VirtualScreen();
            what = "the whole screen";
        }

        using var bitmap = Capture.Grab(rect, window, options, out var info);

        byte[] png;
        using (var buffer = new MemoryStream())
        {
            bitmap.Save(buffer, ImageFormat.Png);
            png = buffer.ToArray();
        }

        var image = ToolImage.FromBytes(png, "image/png", what);

        var content =
            $"Screenshot of {what}: {bitmap.Width}x{bitmap.Height} image pixels, captured via {info.Via}" +
            (info.Clipped ? ", clipped to the screen edges" : string.Empty) +
            (info.CursorMarked ? $", red crosshair at the mouse pointer" : string.Empty) +
            $".{Environment.NewLine}" +
            $"Screen area covered: x={info.Capture.Left}, y={info.Capture.Top}, width={info.Capture.Width}, height={info.Capture.Height} " +
            $"(physical pixels of the virtual desktop).{Environment.NewLine}" +
            $"To turn an image pixel (px, py) into a screen coordinate: screenX = {info.Capture.Left} + round(px / {info.ScaleX.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}), " +
            $"screenY = {info.Capture.Top} + round(py / {info.ScaleY.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}). " +
            "Pass those screen coordinates to pc_click. Mouse pointer is at " +
            $"({info.Cursor.X},{info.Cursor.Y}). Took {info.ElapsedMs} ms.";

        return Task.FromResult(ToolResult.OkWithImages(content, new[] { image }));
    }
}
