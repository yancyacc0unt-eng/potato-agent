using System.Text.Json;
using System.Text.Json.Nodes;
using PotatoAgent.Core.Tools;

namespace PotatoAgent.Win32.Tools;

/// <summary>
/// <c>pc_windows</c>：列出屏幕上看得见的顶层窗口（按 Z 序，最上层在前）。
/// </summary>
/// <remarks>只读，<see cref="ToolRisk.Safe"/>。想操作某个窗口之前先用它拿到 <c>hwnd</c>。</remarks>
public sealed class PcWindowsTool : ITool
{
    /// <inheritdoc />
    public string Name => "pc_windows";

    /// <inheritdoc />
    public string Description =>
        "List the visible top-level windows on the desktop, ordered by Z-order (frontmost first). " +
        "Each entry has hwnd (use it as the 'hwnd' argument of pc_click / pc_type / pc_keys / pc_screenshot), " +
        "title, process, pid, class, bounds, minimized/maximized and foreground flags. " +
        "Hidden, cloaked (suspended UWP) and tool windows are filtered out.";

    /// <inheritdoc />
    public string ParametersJsonSchema => """
        {
          "type": "object",
          "properties": {
            "filter": {
              "type": "string",
              "description": "Optional case-insensitive substring; only windows whose title contains it are returned."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of windows to return (1-500, default 40)."
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

        var request = new JsonObject();
        if (args.Text("filter") is { Length: > 0 } filter)
        {
            request["filter"] = filter;
        }

        if (args.Integer("limit") is { } limit)
        {
            request["limit"] = limit;
        }

        var windows = Desktop.Windows(request);
        return Task.FromResult(ToolResult.OkJson(
            "Visible top-level windows, frontmost first. Use hwnd with pc_click / pc_type / pc_keys.",
            windows.ToJsonString()));
    }
}
