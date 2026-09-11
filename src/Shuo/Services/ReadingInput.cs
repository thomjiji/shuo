using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Shuo.Services;

internal static class ReadingInput
{
    // The caller captures the foreground window before showing any Shuo UI.
    // Query on a worker thread: cross-process UIA providers can block.
    internal static string GetSelection(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || GetForegroundWindow() != foreground) return "";
        var element = AutomationElement.FocusedElement;
        if (element is null || element.Current.IsPassword) return "";
        var root = AutomationElement.FromHandle(foreground);
        if (element.Current.ProcessId != root.Current.ProcessId) return "";
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return "";
        var ranges = ((TextPattern)pattern).GetSelection();
        var text = string.Join(Environment.NewLine, ranges.Select(range => range.GetText(ReadingText.MaximumLength + 1)));
        return GetForegroundWindow() == foreground ? text : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
