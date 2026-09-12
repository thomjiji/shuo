using System.Runtime.InteropServices;
using Shuo.Services;

internal static class Program
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [STAThread]
    private static void Main()
    {
        Exception? failure = null;
        using var form = new Form { Text = "Shuo 选区回归测试", Width = 500, Height = 180 };
        using var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Text = "selected text\r\nsecond line" };
        form.Controls.Add(editor);
        form.Shown += async (_, _) =>
        {
            ClipboardSelection.Snapshot? original = null;
            uint ownedSequence = 0;
            try
            {
                original = ClipboardSelection.Snapshot.Create(form.Handle, default);
                var data = new DataObject();
                data.SetText("clipboard sentinel");
                data.SetData(DataFormats.Html, "<b>rich sentinel</b>");
                Clipboard.SetDataObject(data, true);
                ownedSequence = GetClipboardSequenceNumber();
                editor.Focus();
                editor.Select(0, "selected text".Length);
                var handle = form.Handle;
                form.Activate();
                await Task.Delay(200);
                Check(GetForegroundWindow() == handle, "test window owns foreground before copy");
                var selected = await Task.Run(() => ClipboardSelection.Capture(handle, handle, default));
                Check(selected == "selected text", "system copy reads the selected text");
                Check(Clipboard.GetText() == "clipboard sentinel", "original text clipboard restored");
                Check(Clipboard.TryGetData<string>(DataFormats.Html, out var html) && html == "<b>rich sentinel</b>", "rich clipboard format restored");
                ownedSequence = GetClipboardSequenceNumber();

                editor.Select(0, 0);
                selected = await Task.Run(() => ClipboardSelection.Capture(handle, handle, default));
                Check(selected.Length == 0, "empty selection does not read an old clipboard");
                Check(Clipboard.GetText() == "clipboard sentinel", "no-copy operation preserves clipboard");
                ownedSequence = GetClipboardSequenceNumber();

                using var snapshot = ClipboardSelection.Snapshot.Create(handle, default);
                var oldSequence = GetClipboardSequenceNumber();
                Clipboard.SetText("later user copy");
                ownedSequence = GetClipboardSequenceNumber();
                snapshot.RestoreIfUnchanged(handle, oldSequence);
                Check(Clipboard.GetText() == "later user copy", "later clipboard writes are not overwritten");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { if (original is not null && ownedSequence != 0) original.RestoreIfUnchanged(form.Handle, ownedSequence); }
                catch (Exception error) { failure ??= error; }
                original?.Dispose();
                form.Close();
            }
        };
        Application.Run(form);
        if (failure is not null) throw failure;
    }

    private static void Check(bool passed, string name)
    {
        if (!passed) throw new Exception(name);
        Console.WriteLine("ok " + name);
    }
}
