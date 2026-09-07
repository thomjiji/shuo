using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using WinRT;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using Shuo.Services;
using WinRT.Interop;

namespace Shuo;

public sealed partial class OverlayWindow : Window
{
    private const int OverlayWidth = 320;
    private const int OverlayHeight = 36;
    private const double SlideSeconds = 0.16;
    private readonly IntPtr _handle;
    private readonly UISettings _themeSettings = new();
    private bool _closed;
    private SpriteVisual? _textVisual;
    private CompositionLinearGradientBrush? _textGradient;
    private CompositionColorGradientStop? _fadeStart;
    private CompositionColorGradientStop? _fadeEnd;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly Stopwatch _clock = new();
    private RectInt32 _workArea;
    private bool _visible;
    private bool _hasText;
    private double _textWidth;
    private double _slideFrom;
    private double _slideTo;
    private double _panelWidth = 36;
    private readonly Stopwatch _voiceClock = new();
    private double _targetLevel;
    private double _displayLevel;
    private double _voiceFrame;
    private bool _busy;
    private readonly double[] _rippleBorn = [-10, -10];
    private readonly double[] _rippleStrength = [0, 0];
    private int _nextRipple;
    private double _lastPulse = -10;
    private double _previousLevel;

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
        _themeSettings.ColorValuesChanged += OnSystemColorsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _acrylic?.Dispose();
            _acrylic = null;
            _themeSettings.ColorValuesChanged -= OnSystemColorsChanged;
            StopScrolling();
            StopVoiceAnimation();
        };
        if (DesktopAcrylicController.IsSupported())
        {
            // This passive overlay must keep its material while the typing app has focus.
            _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
            _acrylic = new DesktopAcrylicController();
            _acrylic.SetSystemBackdropConfiguration(_backdropConfiguration);
            _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        }
        InitializeTextMask();
        ApplySystemTheme();
        Hide();
    }

    private void OnSystemColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) ApplySystemTheme();
        });

    private void ApplySystemTheme()
    {
        var systemColor = _themeSettings.GetColorValue(UIColorType.Background);
        var light = systemColor.R * 299 + systemColor.G * 587 + systemColor.B * 114 >= 128000;
        OverlaySurface.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        var surface = light ? Color.FromArgb(255, 250, 250, 250) : Color.FromArgb(255, 12, 12, 12);
        SurfaceBrush.Color = _acrylic is null ? surface : Color.FromArgb(0, 0, 0, 0);
        if (_acrylic is not null && _backdropConfiguration is not null)
        {
            _backdropConfiguration.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
            _acrylic.TintColor = surface;
            _acrylic.TintOpacity = light ? 0.05f : 0.15f;
            _acrylic.LuminosityOpacity = 0.65f;
            _acrylic.FallbackColor = surface;
        }
        SurfaceBorderBrush.Color = light ? Color.FromArgb(255, 208, 208, 208) : Color.FromArgb(255, 48, 48, 48);
        TranscriptBrush.Color = light ? Color.FromArgb(255, 22, 22, 22) : Color.FromArgb(255, 245, 245, 245);
        UpdateTextMask();
    }

    internal void Begin(bool busy)
    {
        Hide();
        _workArea = NativeMethods.GetForegroundWorkArea();
        _hasText = false;
        _panelWidth = 36;
        OverlaySurface.Padding = new Thickness(5, 0, 5, 0);
        TrackGrid.ColumnSpacing = 0;
        TextOffset.X = 0;
        TranscriptText.Text = "";
        _textWidth = 0;
        UpdateTextMask();
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
        TrackGrid.ColumnSpacing = _hasText ? 8 : 0;
        // The 6 px dot is centered in a 24 px slot: its left inset is 5 + 9 px.
        OverlaySurface.Padding = new Thickness(5, 0, _hasText ? 14 : 5, 0);
        var width = _hasText ? Math.Min(OverlayWidth, Math.Ceiling(_textWidth) + 53) : 36;
        if (_panelWidth != width)
        {
            _panelWidth = width;
            Position();
            TextViewport.UpdateLayout();
        }
        if (!hadText && _hasText) TextOffset.X = TextViewport.ActualWidth;
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
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VoiceIndicator.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopVoiceAnimation();
        if (!busy && _visible)
        {
            _voiceClock.Restart();
            CompositionTarget.Rendering += OnVoiceRendering;
        }
    }

    internal void UpdateAudioLevel(double level)
    {
        if (_visible && !_busy) _targetLevel = Math.Clamp(Math.Pow(Math.Max(0, level), 0.75) * 1.8, 0, 1);
    }

    private void OnVoiceRendering(object? sender, object args)
    {
        if (!_themeSettings.AnimationsEnabled)
        {
            RippleOne.Opacity = RippleTwo.Opacity = 0;
            DotScale.ScaleX = DotScale.ScaleY = 1;
            HaloScale.ScaleX = HaloScale.ScaleY = 1;
            VoiceHalo.Opacity = 0.10;
            return;
        }

        var now = _voiceClock.Elapsed.TotalSeconds;
        var elapsed = Math.Clamp(now - _voiceFrame, 0, 0.05);
        _voiceFrame = now;
        var response = _targetLevel > _displayLevel ? 22 : 7;
        _displayLevel += (_targetLevel - _displayLevel) * (1 - Math.Exp(-response * elapsed));

        // Accented syllables release a ring; sustained speech gets a slower heartbeat.
        var rising = _targetLevel - _previousLevel > 0.10;
        var sincePulse = now - _lastPulse;
        if (_displayLevel > 0.08 && sincePulse > 0.24 &&
            (rising || sincePulse > 0.72 - _displayLevel * 0.20))
        {
            _rippleBorn[_nextRipple] = now;
            _rippleStrength[_nextRipple] = _displayLevel;
            _nextRipple = 1 - _nextRipple;
            _lastPulse = now;
        }
        _previousLevel = _targetLevel;

        var breath = Math.Sin(now * Math.PI * 2 / 2.8);
        var pulseAge = now - _lastPulse;
        var bounce = Math.Sin(pulseAge * 24) * Math.Exp(-pulseAge * 9) * _displayLevel;
        var size = 1 + 0.045 * breath + _displayLevel * 0.45;
        DotScale.ScaleX = size + bounce * 0.18;
        DotScale.ScaleY = size - bounce * 0.12;
        HaloScale.ScaleX = HaloScale.ScaleY = 0.88 + _displayLevel * 0.30 + breath * 0.04;
        VoiceHalo.Opacity = 0.08 + _displayLevel * 0.10;
        DrawRipple(RippleOne, RippleOneScale, now - _rippleBorn[0], _rippleStrength[0]);
        DrawRipple(RippleTwo, RippleTwoScale, now - _rippleBorn[1], _rippleStrength[1]);
    }

    private static void DrawRipple(Microsoft.UI.Xaml.Shapes.Ellipse ripple, ScaleTransform scale,
        double age, double strength)
    {
        var phase = Math.Clamp(age / 0.85, 0, 1);
        var spread = 1 - Math.Pow(1 - phase, 2);
        scale.ScaleX = scale.ScaleY = 0.35 + spread * (0.40 + strength * 0.20);
        ripple.Opacity = (0.18 + strength * 0.40) * Math.Pow(1 - phase, 2);
    }

    private void StopVoiceAnimation()
    {
        CompositionTarget.Rendering -= OnVoiceRendering;
        _voiceClock.Stop();
        _targetLevel = _displayLevel = _voiceFrame = _previousLevel = 0;
        _lastPulse = -10;
        _rippleBorn[0] = _rippleBorn[1] = -10;
        _rippleStrength[0] = _rippleStrength[1] = 0;
        _nextRipple = 0;
        RippleOne.Opacity = RippleTwo.Opacity = 0;
        DotScale.ScaleX = DotScale.ScaleY = 1;
        HaloScale.ScaleX = HaloScale.ScaleY = 1;
        VoiceHalo.Opacity = 0.10;
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
            UpdateTextMask();
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
        UpdateTextMask();
        if (progress >= 1) StopScrolling();
    }

    private void InitializeTextMask()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(TextViewport).Compositor;
        _textGradient = compositor.CreateLinearGradientBrush();
        _textGradient.MappingMode = CompositionMappingMode.Absolute;
        _fadeStart = compositor.CreateColorGradientStop();
        _fadeEnd = compositor.CreateColorGradientStop();
        _fadeEnd.Offset = 1;
        _textGradient.ColorStops.Add(_fadeStart);
        _textGradient.ColorStops.Add(_fadeEnd);
        var mask = compositor.CreateMaskBrush();
        mask.Source = _textGradient;
        mask.Mask = TranscriptText.GetAlphaMask();
        _textVisual = compositor.CreateSpriteVisual();
        _textVisual.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(TextViewport, _textVisual);
        // Render the glyph mask once as a whole, rather than shading individual text runs.
        ElementCompositionPreview.GetElementVisual(TranscriptText).Opacity = 0;
    }

    private void UpdateTextMask()
    {
        if (_textVisual is null || _textGradient is null || _fadeStart is null || _fadeEnd is null) return;
        _textVisual.Size = new Vector2((float)_textWidth, 22);
        _textVisual.Offset = new Vector3((float)TextOffset.X, 0, 0);
        var color = TranscriptBrush.Color;
        _fadeEnd.Color = color;
        color.A = (byte)Math.Round(255 * (1 - Math.Clamp(-TextOffset.X / 18, 0, 1)));
        _fadeStart.Color = color;
        var left = (float)Math.Max(0, -TextOffset.X);
        _textGradient.StartPoint = new Vector2(left, 0);
        _textGradient.EndPoint = new Vector2(left + 18, 0);
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
        var width = Math.Max(1, Math.Min((int)Math.Round(_panelWidth * scale), _workArea.Width - margin * 2));
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
        StopVoiceAnimation();
        BusyRing.IsActive = false;
        NativeMethods.ShowWindow(_handle, NativeMethods.SwHide);
    }
}
