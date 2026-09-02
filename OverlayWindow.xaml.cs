using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WindowsDictation.Services;
using WinRT.Interop;

namespace WindowsDictation;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 232;
    private const int OverlayHeight = 72;

    private readonly IntPtr _handle;
    private readonly DispatcherQueueTimer _pulseTimer;
    private bool _pulseDimmed;
    public OverlayWindow()
    {
        InitializeComponent();
        _pulseTimer = DispatcherQueue.CreateTimer();
        _pulseTimer.Interval = TimeSpan.FromMilliseconds(650);
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseDimmed = !_pulseDimmed;
            ActivityWaveform.Opacity = _pulseDimmed ? 0.72 : 1;
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
        ActivityWaveform.Opacity = 1;
        _pulseTimer.Start();
        var workArea = NativeMethods.GetForegroundWorkArea();
        var bounds = new RectInt32(
            workArea.X + (workArea.Width - OverlayWidth) / 2,
            workArea.Y + workArea.Height - OverlayHeight - 28,
            OverlayWidth,
            OverlayHeight);
        AppWindow.MoveAndResize(bounds);
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);
    }

    internal void Hide()
    {
        _pulseTimer.Stop();
        ActivityWaveform.Opacity = 1;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }
}
