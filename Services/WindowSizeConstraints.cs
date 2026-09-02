using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsDictation.Services;

internal sealed class WindowSizeConstraints : IDisposable
{
    private const uint SubclassId = 2;

    private readonly IntPtr _window;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private readonly int _minimumWidth;
    private readonly int _minimumHeight;
    private bool _disposed;

    internal WindowSizeConstraints(IntPtr window, int minimumWidth, int minimumHeight)
    {
        _window = window;
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
        _subclassProc = WindowProcedure;
        if (!NativeMethods.SetWindowSubclass(_window, _subclassProc, (UIntPtr)SubclassId, UIntPtr.Zero))
        {
            throw new Win32Exception("Could not set the minimum window size.");
        }
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        var result = NativeMethods.DefSubclassProc(window, message, wParam, lParam);
        if (message != NativeMethods.WmGetMinMaxInfo) return result;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, _minimumWidth);
        info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, _minimumHeight);
        Marshal.StructureToPtr(info, lParam, false);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMethods.RemoveWindowSubclass(_window, _subclassProc, (UIntPtr)SubclassId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal Point Reserved;
        internal Point MaxSize;
        internal Point MaxPosition;
        internal Point MinTrackSize;
        internal Point MaxTrackSize;
    }
}
