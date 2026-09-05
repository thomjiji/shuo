using System.Drawing;
using Forms = System.Windows.Forms;

namespace Shuo.Services;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notification;
    private bool _disposed;

    internal TrayIcon(string iconPath, Action openSettings, Action exit)
    {
        _icon = new Icon(iconPath);
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            RenderMode = Forms.ToolStripRenderMode.System
        };
        _menu.Items.Add("打开设置", null, (_, _) => openSettings());
        _menu.Items.Add("退出", null, (_, _) => exit());

        _notification = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "说",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notification.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) openSettings();
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notification.Visible = false;
        _notification.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
