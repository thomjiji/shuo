using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WindowsDictation.Services;
using WinRT.Interop;

namespace WindowsDictation;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 920;
    private const int MinimumWindowHeight = 990;

    private readonly DaemonClient _daemon = new();
    private readonly OverlayWindow _overlay = new();
    private readonly WindowSizeConstraints _sizeConstraints;
    private GlobalHotkey? _hotkey;
    private string? _autocorrectPath;
    private bool _started;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(MinimumWindowWidth, MinimumWindowHeight));
        var window = WindowNative.GetWindowHandle(this);
        _sizeConstraints = new WindowSizeConstraints(window, MinimumWindowWidth, MinimumWindowHeight);

        _daemon.MessageReceived += OnDaemonMessage;
        _daemon.ErrorReceived += OnDaemonError;
        _daemon.Exited += OnDaemonExited;
        Closed += OnClosed;

        try
        {
            _hotkey = new GlobalHotkey(window);
            _hotkey.Pressed += (_, _) => _ = ToggleAsync();
        }
        catch (Exception error)
        {
            SetStatus("快捷键不可用", error.Message, InfoBarSeverity.Error);
        }
    }

    internal async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "dictation-daemon.mjs");
            var bundledNode = Path.Combine(AppContext.BaseDirectory, "node.exe");
            var node = Environment.GetEnvironmentVariable("WINDOWS_DICTATION_NODE");
            if (string.IsNullOrWhiteSpace(node)) node = File.Exists(bundledNode) ? bundledNode : "node.exe";
            await _daemon.StartAsync(node, workerPath);
            SetStatus("正在启动", "正在连接本地听写服务。", InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            _started = false;
            SetStatus("无法启动", error.Message, InfoBarSeverity.Error);
            _overlay.Hide();
        }
    }

    private async Task ToggleAsync()
    {
        try
        {
            await StartAsync();
            await _daemon.SendAsync("toggle");
        }
        catch (Exception error)
        {
            SetStatus("听写服务不可用", error.Message, InfoBarSeverity.Error);
            _overlay.Hide();
        }
    }

    private void OnDaemonMessage(object? sender, DaemonMessage message)
    {
        DispatcherQueue.TryEnqueue(() => HandleDaemonMessage(message));
    }

    private void OnDaemonError(object? sender, string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return;
        DispatcherQueue.TryEnqueue(() => SetStatus("听写服务错误", error, InfoBarSeverity.Error));
    }

    private void OnDaemonExited(object? sender, EventArgs eventArgs)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) SetStatus("听写服务已停止", "可以重新打开应用以恢复。", InfoBarSeverity.Warning);
        });
    }

    private void HandleDaemonMessage(DaemonMessage message)
    {
        switch (message.Type)
        {
            case "ready":
                _autocorrectPath = message.AutocorrectPath;
                SetStatus("准备好了", $"本地模型：{message.Model ?? "已加载"}", InfoBarSeverity.Success);
                ToggleButton.Content = "开始录音";
                break;
            case "recording":
                SetStatus("正在录音", "再次按快捷键即可停止并开始转写。", InfoBarSeverity.Informational);
                ToggleButton.Content = "停止并转写";
                _overlay.ShowActivity();
                break;
            case "transcribing":
                SetStatus("正在转写", "本地模型正在整理录音。", InfoBarSeverity.Informational);
                ToggleButton.Content = "正在转写";
                _overlay.ShowActivity();
                break;
            case "transcript":
                _ = PasteTranscriptAsync(message.Text);
                break;
            case "empty":
                SetStatus("没有听到语音", "请再试一次。", InfoBarSeverity.Warning);
                ToggleButton.Content = "开始录音";
                _overlay.Hide();
                break;
            case "busy":
                SetStatus("正在忙", "请等待当前转写完成。", InfoBarSeverity.Warning);
                break;
            case "error":
                SetStatus("听写失败", message.Error ?? "未知错误。", InfoBarSeverity.Error);
                ToggleButton.Content = "开始录音";
                _overlay.Hide();
                break;
            case "stopped":
                SetStatus("已停止", "听写服务已关闭。", InfoBarSeverity.Warning);
                break;
        }
    }

    private async Task PasteTranscriptAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await TranscriptPaster.PasteAsync(text, _autocorrectPath);
            SetStatus("已粘贴", "文字已送到当前输入框。", InfoBarSeverity.Success);
            ToggleButton.Content = "开始录音";
            _overlay.Hide();
        }
        catch (Exception error)
        {
            SetStatus("粘贴失败", error.Message, InfoBarSeverity.Error);
            ToggleButton.Content = "开始录音";
            _overlay.Hide();
        }
    }

    private void SetStatus(string title, string detail, InfoBarSeverity severity)
    {
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusInfo.Title = title;
        StatusInfo.Message = detail;
        StatusInfo.Severity = severity;
    }

    private async void ToggleButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await ToggleAsync();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private async void OnClosed(object sender, WindowEventArgs eventArgs)
    {
        if (_closed) return;
        _closed = true;
        _hotkey?.Dispose();
        _sizeConstraints.Dispose();
        _overlay.Close();
        await _daemon.DisposeAsync();
        Application.Current.Exit();
    }
}
