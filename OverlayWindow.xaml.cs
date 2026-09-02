using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WindowsDictation.Services;
using WinRT.Interop;

namespace WindowsDictation;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 120;
    private const int OverlayHeight = 40;

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

    internal void ShowRecording() => ShowStatus("录音中", Color.FromArgb(255, 239, 68, 68));

    internal void ShowTranscribing() => ShowStatus("正在转写", Color.FromArgb(255, 96, 165, 250));

    internal void Hide() => NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);

    private void ShowStatus(string text, Color color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(color);

        var workArea = NativeMethods.GetForegroundWorkArea();
        var bounds = new RectInt32(
            workArea.X + (workArea.Width - OverlayWidth) / 2,
            workArea.Y + workArea.Height - OverlayHeight - 20,
            OverlayWidth,
            OverlayHeight);
        AppWindow.MoveAndResize(bounds);
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);
    }
}
