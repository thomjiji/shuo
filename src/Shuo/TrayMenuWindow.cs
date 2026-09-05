using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Shuo.Services;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Shuo;

internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _anchor = new();
    private readonly MenuFlyout _menu = new();
    private bool _requested;
    private bool _closed;

    internal TrayMenuWindow(Action openSettings, Action exit)
    {
        Content = _anchor;
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.Resize(new SizeInt32(1, 1));
        // The visible menu is a separate WinUI popup; its anchor stays transparent.
        NativeMethods.MakeTransparentMenuHost(WindowNative.GetWindowHandle(this));

        AddItem("打开设置", openSettings);
        AddItem("退出", exit);
        _anchor.Loaded += (_, _) => ShowFlyout();
        _menu.Closed += (_, _) => HideMenu();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) HideMenu();
        };
        Closed += (_, _) => _closed = true;
    }

    private void AddItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            HideMenu();
            action();
        };
        _menu.Items.Add(item);
    }

    internal void ShowMenu(int x, int y)
    {
        if (_closed) return;
        _requested = true;
        AppWindow.Move(new PointInt32(x, y));
        Activate();
        if (_anchor.IsLoaded) ShowFlyout();
    }

    private void ShowFlyout()
    {
        if (!_requested || _menu.IsOpen) return;
        _menu.ShowAt(_anchor, new FlyoutShowOptions
        {
            Position = new Point(0, 0),
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void HideMenu()
    {
        if (!_requested || _closed) return;
        _requested = false;
        _menu.Hide();
        AppWindow.Hide();
    }
}
