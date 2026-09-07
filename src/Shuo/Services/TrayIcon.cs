using System.Drawing;
using Forms = System.Windows.Forms;

namespace Shuo.Services;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _icon;
    private readonly TrayMenuWindow _menu;
    private readonly Forms.NotifyIcon _notification;
    private bool _disposed;

    internal TrayIcon(string iconPath, Action openSettings, Action exit, Func<IReadOnlyList<TrayChoice>> providers, Func<IReadOnlyList<TrayChoice>> models)
    {
        _icon = new Icon(iconPath);
        _menu = new TrayMenuWindow(openSettings, exit, providers, models);

        _notification = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "说",
            Visible = true
        };
        _notification.BalloonTipClicked += (_, _) => openSettings();
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

    internal void NotifyUpdate(string version)
    {
        if (!_disposed)
            _notification.ShowBalloonTip(8000, "说有新版本", $"版本 {version} 已发布，点击打开应用更新。", Forms.ToolTipIcon.Info);
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
