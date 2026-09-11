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
        if (element is null) return "";
        if (element.Current.IsPassword) throw new UnauthorizedAccessException("密码输入框不支持朗读。");
        var root = AutomationElement.FromHandle(foreground);
        // Browser document providers can live in a different process from their window.
        var ancestors = new List<AutomationElement>();
        for (var current = element; current is not null && ancestors.Count < 64;
            current = TreeWalker.RawViewWalker.GetParent(current))
        {
            ancestors.Add(current);
            if (current.Equals(root)) break;
        }
        if (!ancestors.Contains(root)) return "";
        var text = "";
        foreach (var candidate in ancestors)
        {
            text = Read(candidate);
            if (!string.IsNullOrWhiteSpace(text)) break;
            // A caret in a focused editor is an authoritative empty selection.
            // Do not read a retained selection in another editor or browser document.
            if (candidate.Current.IsPassword) throw new UnauthorizedAccessException("密码输入框不支持朗读。");
            if (candidate.Current.ControlType == ControlType.Edit
                && candidate.TryGetCurrentPattern(TextPattern.Pattern, out _)) return "";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            var candidates = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            var selections = new HashSet<string>(StringComparer.Ordinal);
            foreach (AutomationElement candidate in candidates)
            {
                var selected = Read(candidate);
                if (!string.IsNullOrWhiteSpace(selected)) selections.Add(selected);
                if (selections.Count > 1) return "";
            }
            text = selections.SingleOrDefault() ?? "";
        }
        return GetForegroundWindow() == foreground ? text : "";
    }

    private static string Read(AutomationElement element)
    {
        try
        {
            if (element.Current.IsPassword || element.Current.IsOffscreen || !element.Current.IsEnabled
                || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return "";
            return string.Join(Environment.NewLine, ((TextPattern)pattern).GetSelection()
                .Select(range => range.GetText(ReadingText.MaximumLength + 1)));
        }
        catch (ElementNotAvailableException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
