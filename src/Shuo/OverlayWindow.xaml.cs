using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Shuo.Services;
using WinRT.Interop;

namespace Shuo;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 320;
    private const int OverlayHeight = 36;
    private const double SlideSeconds = 0.16;
    private readonly IntPtr _handle;
    private readonly Stopwatch _clock = new();
    private RectInt32 _workArea;
    private bool _visible;
    private bool _hasText;
    private double _textWidth;
    private double _slideFrom;
    private double _slideTo;

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

    internal void Begin(bool busy)
    {
        Hide();
        _workArea = NativeMethods.GetForegroundWorkArea();
        _hasText = false;
        TranscriptText.Text = "";
        _visible = true;
        SetBusy(busy);
        Position();
    }

    internal void Recording()
    {
        if (!_visible) Begin(false);
        SetBusy(false);
    }

    internal void UpdateTranscript(string? text)
    {
        if (!_visible) return;
        var value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ');
        if (value == TranscriptText.Text) return;
        var hadText = _hasText;
        _hasText = !string.IsNullOrWhiteSpace(value);
        TranscriptText.Text = value;
        TranscriptText.Measure(new Size(double.PositiveInfinity, 22));
        _textWidth = TranscriptText.DesiredSize.Width;
        if (hadText != _hasText)
        {
            Position();
            TextViewport.UpdateLayout();
            if (_hasText) TextOffset.X = TextViewport.ActualWidth;
        }
        if (_hasText) FollowLatestText();
        else StopScrolling();
    }

    internal void Transcribing()
    {
        if (!_visible) Begin(true);
        SetBusy(true);
    }

    internal void Pasting(string? text)
    {
        UpdateTranscript(text);
        SetBusy(true);
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecordingDot.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FollowLatestText()
    {
        if (!_visible || !_hasText) return;
        _slideFrom = TextOffset.X;
        _slideTo = TextViewport.ActualWidth - _textWidth;
        StopScrolling();
        if (Math.Abs(_slideTo - _slideFrom) < 0.1)
        {
            TextOffset.X = _slideTo;
            return;
        }
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        var progress = Math.Min(_clock.Elapsed.TotalSeconds / SlideSeconds, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        TextOffset.X = _slideFrom + (_slideTo - _slideFrom) * eased;
        if (progress >= 1) StopScrolling();
    }

    private void TextViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        TextClip.Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height));
        FollowLatestText();
    }

    private void Position()
    {
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
        var width = Math.Max(1, Math.Min((int)Math.Round((_hasText ? OverlayWidth : 36) * scale), _workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(OverlayHeight * scale), _workArea.Height - margin * 2));
        return new RectInt32(_workArea.X + (_workArea.Width - width) / 2,
            _workArea.Y + _workArea.Height - height - margin, width, height);
    }

    private void StopScrolling()
    {
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    internal void Hide()
    {
        _visible = false;
        StopScrolling();
        BusyRing.IsActive = false;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }
}
