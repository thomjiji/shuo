using System.Runtime.InteropServices;
using System.Text;

// Run the Debug app with --caption-preview before this physical-input smoke test.
Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
IntPtr window = IntPtr.Zero;
Native.EnumWindows((handle, _) =>
{
    var title = new StringBuilder(256);
    Native.GetWindowText(handle, title, title.Capacity);
    if (title.ToString() == "Shuo 状态" && Native.IsWindowVisible(handle)) window = handle;
    return true;
}, IntPtr.Zero);
if (window == IntPtr.Zero) throw new Exception("Start the Debug app with --caption-preview first.");
Native.GetWindowThreadProcessId(window, out var processId);
using var process = System.Diagnostics.Process.GetProcessById((int)processId);
var expectedPath = Path.GetFullPath("artifacts/preview/shuo.exe");
if (!string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
    throw new Exception("The overlay must belong to this repository's preview.");
await Drag("caption text moves window", .5, .25, -100, -160, false);
await Drag("top left resizes", 0, 0, -45, -45, true);
await Drag("top right resizes", 1, 0, 45, -45, true);
await Drag("bottom left resizes", 0, 1, -45, 45, true);
await Drag("bottom right resizes", 1, 1, 45, 45, true);
await Drag("left edge resizes", 0, .5, -30, 0, true);
await Drag("right edge resizes", 1, .5, 30, 0, true);
await Drag("top edge resizes", .5, 0, 0, -30, true);
await Drag("bottom edge resizes", .5, 1, 0, 30, true);
await Drag("caption text still moves after resize", .5, .25, 80, -60, false);

async Task Drag(string label, double x, double y, int dx, int dy, bool resize)
{
    Native.GetWindowRect(window, out var before);
    var sx = before.Left + 5 + (int)((before.Width - 10) * x);
    var sy = before.Top + 5 + (int)((before.Height - 10) * y);
    Native.SetCursorPos(sx, sy);
    await Task.Delay(150);
    Native.mouse_event(2, 0, 0, 0, UIntPtr.Zero);
    try
    {
        await Task.Delay(150);
        for (var step = 1; step <= 12; step++)
        {
            Native.SetCursorPos(sx + dx * step / 12, sy + dy * step / 12);
            await Task.Delay(30);
        }
    }
    finally { Native.mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
    await Task.Delay(250);
    Native.GetWindowRect(window, out var after);
    var changedSize = before.Width != after.Width || before.Height != after.Height;
    var moved = before.Left != after.Left || before.Top != after.Top;
    if (resize ? !changedSize : !moved || changedSize)
        throw new Exception($"fail {label}: {before} -> {after}");
    Console.WriteLine($"ok {label}: {before} -> {after}");
    Native.SetCursorPos(sx, sy);
    await Task.Delay(100);
    Native.GetWindowRect(window, out var released);
    if (!released.Equals(after)) throw new Exception("Window continued moving after mouse release.");
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"{Left},{Top} {Width}x{Height}";
    }
    internal delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
