using Microsoft.UI.Dispatching;
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
    private readonly DispatcherQueueTimer _pulseTimer;
    private bool _pulseDimmed;
    public OverlayWindow()
    {
        InitializeComponent();
        _pulseTimer = DispatcherQueue.CreateTimer();
        _pulseTimer.Interval = TimeSpan.FromMilliseconds(450);
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseDimmed = !_pulseDimmed;
            ActivityIcon.Opacity = _pulseDimmed ? 0.45 : 1;
        };
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
        _pulseDimmed = false;
        ActivityIcon.Opacity = 1;
        _pulseTimer.Start();
        var workArea = NativeMethods.GetForegroundWorkArea();
        var bounds = new RectInt32(
            workArea.X + (workArea.Width - OverlaySize) / 2,
            workArea.Y + workArea.Height - OverlaySize - 28,
            OverlaySize,
            OverlaySize);
        AppWindow.MoveAndResize(bounds);
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);
    }

    internal void Hide()
    {
        _pulseTimer.Stop();
        ActivityIcon.Opacity = 1;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }
}
