using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WindowsDictation.Services;

internal static class NativeMethods
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModNoRepeat = 0x4000;
    internal const uint VkOem5 = 0xDC;
    internal const uint WmHotkey = 0x0312;
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const int HotkeyId = 1;
    internal const int SwHide = 0;
    internal const int SwShownoactivate = 4;
    internal static readonly IntPtr HwndTopmost = new(-1);

    private const int GwlExstyle = -20;
    private const long WsExNoactivate = 0x08000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint MonitorDefaulttonull = 0;

    internal delegate IntPtr SubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProc procedure,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(
        IntPtr window,
        SubclassProc procedure,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    internal static void MakeNoActivateToolWindow(IntPtr window)
    {
        var style = GetWindowLongPtr(window, GwlExstyle).ToInt64();
        SetWindowLongPtr(window, GwlExstyle, new IntPtr(style | WsExNoactivate | WsExToolwindow));
    }

    internal static RectInt32 GetForegroundWorkArea()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, MonitorDefaulttonull);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            var work = monitorInfo.WorkArea;
            return new RectInt32(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
        }

        return new RectInt32(0, 0, 1920, 1080);
    }

    internal static void ShowNoActivateTopmost(IntPtr window, RectInt32 bounds)
    {
        SetWindowPos(
            window,
            HwndTopmost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SwpNoactivate | SwpShowwindow);
        ShowWindow(window, SwShownoactivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect MonitorArea;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

}
