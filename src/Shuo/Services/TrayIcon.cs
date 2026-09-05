using System.Drawing;
using Forms = System.Windows.Forms;

namespace Shuo.Services;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _icon;
    private readonly TrayMenuWindow _menu;
    private readonly Forms.NotifyIcon _notification;
    private bool _disposed;

    internal TrayIcon(string iconPath, Action openSettings, Action exit)
    {
        _icon = new Icon(iconPath);
        _menu = new TrayMenuWindow(openSettings, exit);

        _notification = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "说",
            Visible = true
        };
        _notification.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) openSettings();
            if (args.Button == Forms.MouseButtons.Right)
            {
                var cursor = Forms.Cursor.Position;
                _menu.DispatcherQueue.TryEnqueue(() => _menu.ShowMenu(cursor.X, cursor.Y));
            }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notification.Visible = false;
        _notification.Dispose();
        _menu.Close();
        _icon.Dispose();
    }
}
