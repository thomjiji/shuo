using System.ComponentModel;

namespace WindowsDictation.Services;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly IntPtr _window;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private bool _registered;
    private bool _disposed;

    internal GlobalHotkey(IntPtr window)
    {
        _window = window;
        _subclassProc = WindowProcedure;
        if (!NativeMethods.SetWindowSubclass(_window, _subclassProc, (UIntPtr)NativeMethods.HotkeyId, UIntPtr.Zero))
        {
            throw new Win32Exception("Could not monitor the application window for hotkeys.");
        }

        var modifiers = NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModNoRepeat;
        if (!NativeMethods.RegisterHotKey(_window, NativeMethods.HotkeyId, modifiers, NativeMethods.VkOem5))
        {
            NativeMethods.RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)NativeMethods.HotkeyId);
            throw new Win32Exception("Ctrl+Alt+\\ is already in use by another application.");
        }

        _registered = true;
    }

    internal event EventHandler? Pressed;

    private IntPtr WindowProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == NativeMethods.WmHotkey && wParam.ToInt32() == NativeMethods.HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window, NativeMethods.HotkeyId);
        }
        NativeMethods.RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)NativeMethods.HotkeyId);
    }
}
