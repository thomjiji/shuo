using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Shuo.Services;

internal static class NativeMethods
{
    internal const uint ModNoRepeat = 0x4000;
    internal const uint VkShift = 0x10;
    internal const uint VkControl = 0x11;
    internal const uint VkMenu = 0x12;
    internal const uint VkLwin = 0x5B;
    internal const uint VkRwin = 0x5C;
    internal const uint VkOem5 = 0xDC;
    internal const uint WmHotkey = 0x0312;
    private const uint WmSetIcon = 0x0080;
    internal const int HotkeyId = 1;
    internal const int SwHide = 0;
    internal const int SwShownoactivate = 4;
    internal static readonly IntPtr HwndTopmost = new(-1);

    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsExNoactivate = 0x08000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;
    private const uint SwpShowwindow = 0x0040;
    private const uint ImageIcon = 1;
    private const uint LrLoadfromfile = 0x0010;
    private const int IconSmall = 0;
    private const int IconBig = 1;

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

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    internal static bool IsKeyDown(uint virtualKey) => (GetKeyState((int)virtualKey) & 0x8000) != 0;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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

    internal static void MakeNoActivateToolWindow(IntPtr window)
    {
        var exStyle = GetWindowLongPtr(window, GwlExstyle).ToInt64();
        SetWindowLongPtr(window, GwlExstyle, new IntPtr(exStyle | WsExNoactivate | WsExToolwindow));
        var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
        SetWindowLongPtr(window, GwlStyle, new IntPtr(style & ~(WsCaption | WsThickFrame)));
        SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
    }

    internal static void SetWindowIcons(IntPtr window, string iconPath)
    {
        if (!File.Exists(iconPath)) return;

        var smallIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 16, 16, LrLoadfromfile);
        if (smallIcon != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, new IntPtr(IconSmall), smallIcon);
        }

        var largeIcon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 48, 48, LrLoadfromfile);
        if (largeIcon != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, new IntPtr(IconBig), largeIcon);
        }
    }

    internal static RectInt32 GetForegroundWorkArea()
    {
        var foreground = GetForegroundWindow();
        var windowId = Win32Interop.GetWindowIdFromWindow(foreground);
        return DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
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

}
