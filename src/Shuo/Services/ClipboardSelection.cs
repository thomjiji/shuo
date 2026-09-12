using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Shuo.Services;

// A single explicit copy transaction. Never poll the clipboard outside this operation.
internal static class ClipboardSelection
{
    private const uint UnicodeText = 13;
    private static readonly uint WebMetadata = RegisterClipboardFormat("Chromium Web Custom MIME Data Format");
    private static readonly uint LineSelection = RegisterClipboardFormat("MSDEVLineSelect");

    internal static string Capture(IntPtr foreground, IntPtr owner, CancellationToken token)
    {
        var releaseDeadline = Stopwatch.StartNew();
        while (ModifiersDown())
        {
            token.ThrowIfCancellationRequested();
            if (releaseDeadline.ElapsedMilliseconds > 1500) throw new IOException("请松开快捷键后重试。");
            Thread.Sleep(15);
        }
        if (GetForegroundWindow() != foreground) return "";
        GetWindowThreadProcessId(foreground, out var processId);
        using var original = Snapshot.Create(owner, token);
        if (GetForegroundWindow() != foreground || GetClipboardSequenceNumber() != original.Sequence) return "";
        token.ThrowIfCancellationRequested();
        uint copiedSequence = 0;
        try
        {
            var inputs = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            {
                // Release only the modifier injected by this transaction after a partial send.
                SendInput(1, new[] { Key(0x11, true) }, Marshal.SizeOf<Input>());
                throw new IOException("无法向当前窗口发送复制操作；请检查窗口是否以管理员身份运行。");
            }
            var deadline = Stopwatch.StartNew();
            // Even cancellation must allow an in-flight copy to finish so it can be restored.
            while (deadline.ElapsedMilliseconds < 1500)
            {
                var sequence = GetClipboardSequenceNumber();
                if (sequence != original.Sequence)
                {
                    using var locked = Open(owner, CancellationToken.None);
                    if (GetClipboardSequenceNumber() != sequence) continue;
                    GetWindowThreadProcessId(GetClipboardOwner(), out var clipboardProcess);
                    if (clipboardProcess != processId) return "";
                    copiedSequence = sequence;
                    if (token.IsCancellationRequested || GetForegroundWindow() != foreground) return "";
                    if (IsClipboardFormatAvailable(LineSelection)) return "";
                    if (ClipboardSelectionMetadata.IsEmptySelection(ReadGlobal(WebMetadata, 1024 * 1024))) return "";
                    var bytes = ReadGlobal(UnicodeText, (ReadingText.MaximumLength + 2) * 2);
                    return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                }
                Thread.Sleep(20);
            }
            return "";
        }
        finally
        {
            // A later user/app copy wins. Do not overwrite it with the saved clipboard.
            if (copiedSequence != 0) original.RestoreIfUnchanged(owner, copiedSequence);
        }
    }

    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }
        .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static byte[] ReadGlobal(uint format, int maximum)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return [];
        var size = GlobalSize(handle).ToUInt64();
        if (size > (ulong)maximum) throw new IOException("复制内容过大，请缩小选区。");
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw new IOException("无法读取本次复制的内容。");
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    internal sealed class Snapshot : IDisposable
    {
        private readonly List<(uint Format, IntPtr Handle)> _formats = [];
        internal uint Sequence { get; private set; }

        internal static Snapshot Create(IntPtr owner, CancellationToken token)
        {
            var result = new Snapshot();
            try
            {
                using var locked = Open(owner, token);
                result.Sequence = GetClipboardSequenceNumber();
                ulong size = 0;
                for (uint format = 0; (format = EnumClipboardFormats(format)) != 0;)
                {
                    // Owner-display and private handles cannot be safely duplicated generically.
                    if (format == 0x80 || format is >= 0x200 and <= 0x3FF || result._formats.Count >= 128)
                        throw new IOException("当前剪贴板包含无法完整恢复的格式，本次未执行复制。");
                    var source = GetClipboardData(format);
                    size += GlobalSize(source).ToUInt64();
                    if (size > 64 * 1024 * 1024) throw new IOException("当前剪贴板过大，无法安全暂存。");
                    var duplicate = OleDuplicateData(source, format, 0);
                    if (duplicate == IntPtr.Zero) throw new IOException("无法完整暂存剪贴板，本次未执行复制。");
                    result._formats.Add((format, duplicate));
                }
                if (Marshal.GetLastWin32Error() != 0) throw new IOException("无法枚举全部剪贴板格式，本次未执行复制。");
                result.Sequence = GetClipboardSequenceNumber();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal void RestoreIfUnchanged(IntPtr owner, uint sequence)
        {
            using var locked = Open(owner, CancellationToken.None);
            if (GetClipboardSequenceNumber() != sequence) return;
            if (!EmptyClipboard()) throw new IOException("无法恢复剪贴板。");
            for (var index = 0; index < _formats.Count; index++)
            {
                var entry = _formats[index];
                if (SetClipboardData(entry.Format, entry.Handle) == IntPtr.Zero)
                    throw new IOException("剪贴板恢复不完整。");
                _formats[index] = (entry.Format, IntPtr.Zero); // Ownership passed to Windows.
            }
        }

        public void Dispose()
        {
            foreach (var entry in _formats)
            {
                if (entry.Handle == IntPtr.Zero) continue;
                var medium = new STGMEDIUM
                {
                    tymed = entry.Format switch { 2 or 9 => TYMED.TYMED_GDI, 3 => TYMED.TYMED_MFPICT, 14 => TYMED.TYMED_ENHMF, _ => TYMED.TYMED_HGLOBAL },
                    unionmember = entry.Handle,
                };
                ReleaseStgMedium(ref medium);
            }
            _formats.Clear();
        }
    }

    private static ClipboardLock Open(IntPtr owner, CancellationToken token)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (OpenClipboard(owner)) return new();
            Thread.Sleep(10);
        }
        throw new IOException("剪贴板正被其他程序占用，请稍后重试。");
    }

    private sealed class ClipboardLock : IDisposable { public void Dispose() => CloseClipboard(); }
    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0 } } };
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr source, uint format, uint flags);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
