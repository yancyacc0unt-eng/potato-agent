// UIA 桥：TypeText 的首选路径（ValuePattern.SetValue）与回读（验收用）。
//
// 用托管 UIA（System.Windows.Automation，来自 UIAutomationClient）而不是 COM 手工封送：
// 少 400 行胶水代码，且能拿到 .NET 侧的元素缓存与异常语义。
// 这套程序集由 Win32.csproj 里的 <UseWPF>true</UseWPF> 引入，不需要 NuGet 包。
//
// 任何一步不成立都返回 false + 原因，绝不抛给调用方 —— 调用方据此决定是否退回 SendInput。

using System.Windows.Automation;

namespace PotatoAgent.Win32;

internal static class UiaText
{
    /// <summary>在窗口里找承载文本的控件：Document（RichEdit/WinUI）优先，其次 Edit（经典 Win32 编辑框）。</summary>
    private static AutomationElement? FindTextElement(AutomationElement root)
    {
        foreach (var type in new[] { ControlType.Document, ControlType.Edit })
        {
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
            AutomationElement? found = root.FindFirst(TreeScope.Descendants, condition);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 首选路径：ValuePattern.SetValue 一次性写入。
    /// 控件不支持 ValuePattern / 只读 / 元素找不到 → false，调用方退回 SendInput。
    /// </summary>
    internal static bool TrySetValue(IntPtr hwnd, string text, out string message)
    {
        message = string.Empty;
        try
        {
            AutomationElement? root = AutomationElement.FromHandle(hwnd);
            if (root is null)
            {
                message = "UIA: the window has no automation element";
                return false;
            }

            AutomationElement? target = FindTextElement(root);
            if (target is null)
            {
                message = "UIA: no Document/Edit element found in the window";
                return false;
            }

            if (!target.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern))
            {
                message = $"UIA: the {target.Current.ControlType.ProgrammaticName} element does not support ValuePattern";
                return false;
            }

            var value = (ValuePattern)pattern;
            if (value.Current.IsReadOnly)
            {
                message = "UIA: the element is read-only";
                return false;
            }

            try { target.SetFocus(); }
            catch (Exception error) { message = $"UIA: SetFocus failed ({error.GetType().Name}) "; }

            value.SetValue(text);
            message = "UIA: ValuePattern.SetValue" + (message.Length > 0 ? " (" + message.Trim() + ")" : string.Empty);
            return true;
        }
        catch (Exception error)
        {
            message = $"UIA: {error.GetType().Name}: {error.Message}";
            return false;
        }
    }

    /// <summary>回读文本内容：ValuePattern 优先，其次 TextPattern。via 说明用的是哪一条。</summary>
    internal static bool TryRead(IntPtr hwnd, out string text, out string via)
    {
        text = string.Empty;
        via = string.Empty;

        try
        {
            AutomationElement? root = AutomationElement.FromHandle(hwnd);
            if (root is null) return false;

            AutomationElement? target = FindTextElement(root) ?? root;

            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePattern))
            {
                text = ((ValuePattern)valuePattern).Current.Value;
                via = "ValuePattern";
                return true;
            }

            if (target.TryGetCurrentPattern(TextPattern.Pattern, out object? textPattern))
            {
                text = ((TextPattern)textPattern).DocumentRange.GetText(-1);
                via = "TextPattern";
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一句话描述窗口里的文本控件，报错时用得上。</summary>
    internal static string Describe(IntPtr hwnd)
    {
        try
        {
            AutomationElement? root = AutomationElement.FromHandle(hwnd);
            if (root is null) return "no automation element";

            AutomationElement? target = FindTextElement(root);
            if (target is null) return "no Document/Edit element";

            return $"{target.Current.ControlType.ProgrammaticName} class=\"{target.Current.ClassName}\" " +
                   $"name=\"{target.Current.Name}\" readOnly={SafeReadOnly(target)}";
        }
        catch (Exception error)
        {
            return $"{error.GetType().Name}: {error.Message}";
        }
    }

    private static string SafeReadOnly(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                ? ((ValuePattern)pattern).Current.IsReadOnly.ToString()
                : "n/a(no ValuePattern)";
        }
        catch
        {
            return "?";
        }
    }
}
