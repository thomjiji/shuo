using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Shuo.Services;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Shuo;

internal sealed record TrayChoice(string Name, bool Selected, bool Enabled, Action Select);

internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _anchor = new();
    private readonly MenuFlyout _menu = new();
    private readonly Func<IReadOnlyList<TrayChoice>> _providers;
    private readonly Func<IReadOnlyList<TrayChoice>> _models;
    private readonly MenuFlyoutSubItem _providerMenu = new() { Text = "转录服务", FontSize = 13, MinHeight = 30, Padding = new Thickness(12, 4, 12, 4) };
    private readonly MenuFlyoutSubItem _modelMenu = new() { Text = "本地模型", FontSize = 13, MinHeight = 30, Padding = new Thickness(12, 4, 12, 4) };
    private bool _requested;
    private bool _closed;

    internal TrayMenuWindow(Action openSettings, Action exit, Func<IReadOnlyList<TrayChoice>> providers, Func<IReadOnlyList<TrayChoice>> models)
    {
        _providers = providers;
        _models = models;
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
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(_providerMenu);
        _menu.Items.Add(_modelMenu);
        _menu.Items.Add(new MenuFlyoutSeparator());
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
        var item = new MenuFlyoutItem { Text = text, FontSize = 13, MinHeight = 30, Padding = new Thickness(12, 4, 12, 4) };
        item.Click += (_, _) =>
        {
            HideMenu();
            action();
        };
        _menu.Items.Add(item);
    }

    private void Populate(MenuFlyoutSubItem menu, IReadOnlyList<TrayChoice> choices)
    {
        menu.Items.Clear();
        foreach (var choice in choices)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = choice.Name, IsChecked = choice.Selected, IsEnabled = choice.Enabled,
                FontSize = 13, MinHeight = 30, Padding = new Thickness(12, 4, 12, 4)
            };
            item.Click += (_, _) => { HideMenu(); choice.Select(); };
            menu.Items.Add(item);
        }
        if (choices.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "暂无可用模型", IsEnabled = false, FontSize = 13 });
    }

    internal void ShowMenu(int x, int y)
    {
        if (_closed) return;
        Populate(_providerMenu, _providers());
        Populate(_modelMenu, _models());
        _requested = true;
        AppWindow.Move(new PointInt32(x, y));
        Activate();
        if (_anchor.IsLoaded) ShowFlyout();
    }

    private void ShowFlyout()
    {
        if (!_requested || _menu.IsOpen) return;
        // A tray callback can activate the XAML window without making its HWND foreground.
        // Establish foreground ownership before opening the popup so outside clicks dismiss
        // the entire flyout (including submenus) through the normal deactivation path.
        NativeMethods.SetForegroundWindow(WindowNative.GetWindowHandle(this));
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
