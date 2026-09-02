using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsDictation.Services;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly IntPtr _window;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private bool _registered;
    private bool _disposed;

    internal GlobalHotkey(IntPtr window, HotkeyBinding binding)
    {
        if (!binding.IsValid) throw new ArgumentException("The hotkey is invalid.", nameof(binding));

        _window = window;
        _subclassProc = WindowProcedure;
        if (!NativeMethods.SetWindowSubclass(_window, _subclassProc, (UIntPtr)NativeMethods.HotkeyId, UIntPtr.Zero))
        {
            throw new Win32Exception("Could not monitor the application window for hotkeys.");
        }

        if (!NativeMethods.RegisterHotKey(
                _window,
                NativeMethods.HotkeyId,
                binding.Modifiers | NativeMethods.ModNoRepeat,
                binding.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)NativeMethods.HotkeyId);
            throw new Win32Exception(error, $"{binding.DisplayText} is unavailable.");
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
