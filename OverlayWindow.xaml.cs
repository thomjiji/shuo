using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WindowsDictation.Services;
using WinRT.Interop;

namespace WindowsDictation;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 420;
    private const int OverlayHeight = 78;

    private readonly IntPtr _handle;
    private readonly DispatcherQueueTimer _hideTimer;

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

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Tick += (_, _) => Hide();
        Hide();
    }

    internal void ShowRecording()
    {
        Show("正在录音", "再次按 Ctrl + Alt + \\ 停止", autoHide: null);
    }

    internal void ShowTranscribing()
    {
        Show("正在转写", "本地模型正在整理你的话", autoHide: null);
    }

    internal void ShowPasted()
    {
        Show("已粘贴", "文字已经送到当前输入框", TimeSpan.FromSeconds(1.5));
    }

    internal void ShowNotice(string title, string detail)
    {
        Show(title, detail, TimeSpan.FromSeconds(3));
    }

    internal void Hide()
    {
        _hideTimer.Stop();
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }

    private void Show(string title, string detail, TimeSpan? autoHide)
    {
        _hideTimer.Stop();
        StatusText.Text = title;
        DetailText.Text = detail;
        ActivityRing.IsActive = autoHide is null;

        var workArea = NativeMethods.GetForegroundWorkArea();
        var bounds = new RectInt32(
            workArea.X + (workArea.Width - OverlayWidth) / 2,
            workArea.Y + workArea.Height - OverlayHeight - 28,
            OverlayWidth,
            OverlayHeight);
        AppWindow.MoveAndResize(bounds);
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);

        if (autoHide is { } delay)
        {
            _hideTimer.Interval = delay;
            _hideTimer.Start();
        }
    }
}
