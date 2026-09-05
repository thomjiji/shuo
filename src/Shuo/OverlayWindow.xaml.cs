using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Shuo.Services;
using WinRT.Interop;

namespace Shuo;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 480;
    private const int CompactHeight = 104;
    private const int TranscriptHeight = 156;
    private readonly IntPtr _handle;
    private RectInt32 _workArea;
    private bool _visible;
    private bool _hasText;

    public OverlayWindow()
    {
        InitializeComponent();
        _handle = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        NativeMethods.MakeNoActivateToolWindow(_handle);
        NativeMethods.RoundWindowCorners(_handle);
        Hide();
    }

    internal void Begin(bool connecting, bool streaming)
    {
        _workArea = NativeMethods.GetForegroundWorkArea();
        _hasText = false;
        TranscriptText.Text = connecting ? "连接成功后即可开始说话..." :
            streaming ? "请开始说话..." : "停止录音后开始转录";
        SetState(connecting ? "正在连接豆包" : "正在聆听", connecting, !connecting);
        _visible = true;
        Position();
    }

    internal void Recording(bool streaming)
    {
        if (!_visible) { Begin(false, streaming); return; }
        SetState("正在聆听", false, true);
        if (!_hasText) TranscriptText.Text = streaming ? "请开始说话..." : "停止录音后开始转录";
    }

    internal void UpdateTranscript(string? text)
    {
        if (!_visible) return;
        var hasText = !string.IsNullOrWhiteSpace(text);
        TranscriptText.Text = hasText ? text! : "正在识别...";
        if (_hasText != hasText)
        {
            _hasText = hasText;
            Position();
        }
        TranscriptText.UpdateLayout();
        FollowLatestText();
    }

    internal void Transcribing()
    {
        if (!_visible) Begin(false, false);
        SetState("正在完成转录", true, false);
        if (!_hasText) TranscriptText.Text = "请稍候...";
    }

    internal void Pasting(string? text)
    {
        UpdateTranscript(text);
        SetState("正在输入", true, false);
    }

    private void SetState(string status, bool busy, bool recording)
    {
        StatusLabel.Text = status;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        ShortcutHint.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Position()
    {
        // Move onto the target monitor before reading this window's DPI.
        var scale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        var bounds = CalculateBounds(scale);
        AppWindow.MoveAndResize(bounds);
        var targetScale = NativeMethods.GetDpiForWindow(_handle) / 96.0;
        if (targetScale != scale)
        {
            bounds = CalculateBounds(targetScale);
            AppWindow.MoveAndResize(bounds);
        }
        NativeMethods.ShowNoActivateTopmost(_handle, bounds);
    }

    private RectInt32 CalculateBounds(double scale)
    {
        var margin = (int)Math.Round(20 * scale);
        var width = Math.Max(1, Math.Min((int)Math.Round(OverlayWidth * scale), _workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round((_hasText ? TranscriptHeight : CompactHeight) * scale),
            _workArea.Height - margin * 2));
        return new RectInt32(_workArea.X + (_workArea.Width - width) / 2,
            _workArea.Y + _workArea.Height - height - margin, width, height);
    }

    private void TranscriptText_SizeChanged(object sender, SizeChangedEventArgs args) => FollowLatestText();

    private void FollowLatestText()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_visible) TranscriptScroll.ChangeView(null, TranscriptScroll.ScrollableHeight, null, disableAnimation: true);
        });
    }

    internal void Hide()
    {
        _visible = false;
        BusyRing.IsActive = false;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }
}
