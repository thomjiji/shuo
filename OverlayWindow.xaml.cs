using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WindowsDictation.Services;
using WinRT.Interop;

namespace WindowsDictation;

public sealed partial class OverlayWindow : Window
{
    private const int OverlaySize = 64;

    private readonly IntPtr _handle;

    public OverlayWindow()
    {
        InitializeComponent();
        _handle = WindowNative.GetWindowHandle(this);
        NativeMethods.MakeNoActivateToolWindow(_handle);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        Hide();
    }

    internal void ShowActivity()
    {
        var workArea = NativeMethods.GetForegroundWorkArea();
        var bounds = new RectInt32(
            workArea.X + (workArea.Width - OverlaySize) / 2,
            workArea.Y + workArea.Height - OverlaySize - 28,
            OverlaySize,
            OverlaySize);
        AppWindow.MoveAndResize(bounds);
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);
    }

    internal void Hide() => NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
}
