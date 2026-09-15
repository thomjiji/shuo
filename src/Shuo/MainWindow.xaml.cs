using Windows.ApplicationModel.DataTransfer;
using System.Numerics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using Shuo.Services;
using WinRT.Interop;

namespace Shuo;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 840;
    private const int MinimumWindowHeight = 600;
    private const float SettingsPageOffset = 24;
    private static readonly TimeSpan SettingsPageMotionDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SettingsPageFadeDuration = TimeSpan.FromMilliseconds(167);
    private readonly DaemonClient _daemon = new();
    private readonly OverlayWindow _overlay = new();
    private readonly UISettings _uiSettings = new();
    private readonly IntPtr _window;
    private readonly TrayIcon _tray;
    private readonly CancellationTokenSource _shutdown = new();
    private TextCleanupOptions _cleanupOptions = new();
    private TextCleanupOptions _recordingCleanupOptions = new();
    private bool _updatingCleanupControls = true;
    private bool _exiting;
    private HotkeyBinding? _hotkeyBinding;
    private GlobalHotkey? _hotkey;
    private bool _transcriptionHotkeyLoaded;
    private bool _updatingTranscriptionToggle;
    private bool _capturingHotkey;
    private string? _autocorrectPath;
    private bool _togglePending;
    private bool _started;
    private bool _daemonReady;
    private string? _startupError;
    private bool _closed;
    private bool _dictationActive;
    private bool _modelChanging;
    private bool _loadingModels = true;
    private bool _updatingModelPicker;
    private string? _selectedModelPath;

    private readonly TranscriptHistory _history = new(TranscriptHistory.DefaultPath);
    private List<TranscriptEntry>? _historyEntries;
    private int _historyVisibleCount = 50;

    public MainWindow()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(PageSurface, true);
        SettingsNavigation.SelectedItem = ListenNavigationItem;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _window = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        NativeMethods.SetWindowIcons(_window, iconPath);
        var scale = NativeMethods.GetDpiForWindow(_window) / 96.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(960 * scale), workArea.Width), Math.Min((int)(760 * scale), workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinimumWindowWidth;
            presenter.PreferredMinimumHeight = MinimumWindowHeight;
        }
        _tray = new TrayIcon(iconPath,
            () => DispatcherQueue.TryEnqueue(ShowSettings),
            () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync()), TrayProviders, TrayModels);
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Closing += OnWindowClosing;

        _daemon.MessageReceived += OnDaemonMessage;
        _daemon.ErrorReceived += OnDaemonError;
        _daemon.Exited += OnDaemonExited;
        Closed += OnClosed;

        var savedHotkey = HotkeySettings.Load();
        _hotkeyBinding = savedHotkey ?? HotkeyBinding.Default;
        TranscriptionEnabled.IsOn = HotkeySettings.LoadEnabled();
        if (TranscriptionEnabled.IsOn && !TryRegisterHotkey(_hotkeyBinding.Value, out var error))
        {
            TranscriptionEnabled.IsOn = false;
            ShowError("快捷键不可用", error!.Message);
        }
        _transcriptionHotkeyLoaded = true;

        UpdateHotkeyPreview();
        try
        {
            _cleanupOptions = TextCleanupSettings.Load();
        }
        catch (Exception settingsError)
        {
            ShowError("无法读取文本整理设置", settingsError.Message);
        }
        _recordingCleanupOptions = _cleanupOptions;
        UpdateCleanupControls();
        try
        {
            _cloudOptions = CloudSettings.Load();
            ProviderPicker.SelectedIndex = _cloudOptions.Backend switch { "selfhosted" => 3, "qwen" => 2, "doubao" => 1, _ => 0 };
            SelfHostedUrl.Text = SelfHostedAddress.ToDisplay(_cloudOptions.SelfHostedUrl);
            SelfHostedModelPicker.SelectedIndex = _cloudOptions.SelfHostedModel == SelfHostedAsrModels.Small ? 1 : 0;
            QwenApiKey.Password = _cloudOptions.QwenApiKey;
            QwenModelPicker.SelectedIndex = _cloudOptions.QwenModel == "qwen3-asr-flash-realtime" ? 1 : 0;
            CloudApiKey.Password = _cloudOptions.ApiKey;
            UpdateDoubaoModelPicker();
        }
        catch (Exception cloudError) { CloudStatusMessage = cloudError.Message; }
        ProviderPicker_SelectionChanged(this, null!);
        _cloudFieldsLoaded = true;
        RefreshCloudStatus();
        InitializeUpdates();
        InitializeTranslation();
        InitializeReading();
        InitializeDailyTasks();
        InitializeServiceConnections();
        InitializeStartupRegistration();
    }

    internal void ShowSettings()
    {
        if (_exiting || _closed) return;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        RefreshStartupRegistration();
        _ = RefreshModelsAsync();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (GeneralPage is null || ServicesPage is null) return;
        var section = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "listen";
        GeneralPage.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        ServicesPage.Visibility = section == "services" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = section == "history" ? Visibility.Visible : Visibility.Collapsed;
        ReadingPage.Visibility = section == "reading" ? Visibility.Visible : Visibility.Collapsed;
        ListenPage.Visibility = section == "listen" ? Visibility.Visible : Visibility.Collapsed;
        CaptionPage.Visibility = section == "captions" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "general") AcknowledgeAvailableUpdate();
        if (section == "history" && _historyEntries is null) LoadHistory();
        if (section == "services") _ = RefreshModelsAsync();
        PageTitle.Text = section switch { "listen" => "语音输入", "captions" => "实时字幕", "reading" => "文字朗读", "general" => "关于", "history" => "历史", "services" => "设置", _ => "语音输入" };
        PageScroll.ChangeView(null, 0, null, disableAnimation: true);
        PlaySettingsPageTransition();
    }

    private void PlaySettingsPageTransition()
    {
        var visual = ElementCompositionPreview.GetElementVisual(PageSurface);
        visual.StopAnimation("Opacity");
        visual.Properties.StopAnimation("Translation");
        visual.Opacity = 1;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!_uiSettings.AnimationsEnabled) return;

        var easing = visual.Compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1));
        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(0, SettingsPageOffset, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, easing);
        translation.Duration = SettingsPageMotionDuration;

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, easing);
        opacity.Duration = SettingsPageFadeDuration;

        visual.Properties.StartAnimation("Translation", translation);
        visual.StartAnimation("Opacity", opacity);
    }

    private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (SettingsContent is not null)
            SettingsContent.Width = Math.Max(0, Math.Min(920, args.NewSize.Width - 72));
    }

    private bool CanSwitchFromTray => _readingCancellation is null && _translationCancellation is null && _daemonReady && !_dictationActive && !_togglePending
        && !_modelChanging && !_loadingModels && !_installingUpdate;

    private IReadOnlyList<TrayChoice> TrayProviders() =>
        new[] { ("local", "本地模型"), ("doubao", "火山引擎"), ("qwen", "阿里云百炼"), ("selfhosted", "自托管（MLX）") }
            .Select(item => new TrayChoice(item.Item2, _cloudOptions.Backend == item.Item1,
                CanSwitchFromTray, () => _ = SwitchProviderAsync(item.Item1))).ToArray();

    private IReadOnlyList<TrayChoice> TrayModels() => ModelPicker.Items.OfType<LocalModel>()
        .Select(model => new TrayChoice(Path.GetFileNameWithoutExtension(model.Path),
            string.Equals(model.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase),
            CanSwitchFromTray && !_cloudOptions.Enabled,
            () =>
            {
                if (CanSwitchFromTray && !_cloudOptions.Enabled) ModelPicker.SelectedItem = model;
            })).ToArray();

    private async Task SwitchProviderAsync(string provider)
    {
        if (!_daemonReady || _dictationActive || _togglePending || _modelChanging || _installingUpdate || provider == _cloudOptions.Backend) return;
        try
        {
            var options = _cloudOptions with { Enabled = provider != "local", Provider = provider == "local" ? "doubao" : provider };
            CloudSettings.SaveProvider(provider);
            _cloudOptions = options;
            ProviderPicker.SelectedIndex = provider switch { "selfhosted" => 3, "qwen" => 2, "doubao" => 1, _ => 0 };
            _modelChanging = true;
            _cloudTesting = false;
            _backendConfigured = false;
            CloudStatusMessage = "已保存，正在切换转录服务...";
            UpdateModelControls();
            await ConfigureBackendAsync();
        }
        catch (Exception error)
        {
            _modelChanging = false;
            _cloudTesting = false;
            CloudStatusMessage = error.Message;
            ProviderPicker.SelectedIndex = _cloudOptions.Backend switch { "selfhosted" => 3, "qwen" => 2, "doubao" => 1, _ => 0 };
            CloudStatus.Text = error.Message;
            UpdateModelControls();
            ShowSettings();
        }
    }
    private CloudOptions _cloudOptions = new();
    private bool _cloudTesting;
    private bool _backendConfigured;
    private bool _cloudFieldsLoaded;
    private string _cloudStatusMessage = "";
    private string CloudStatusMessage
    {
        set
        {
            _cloudStatusMessage = value;
            RefreshCloudStatus();
        }
    }

    private void UpdateDoubaoModelPicker()
    {
        DoubaoModelPicker.SelectedIndex = _cloudOptions.ResourceId.Trim() switch
        {
            "volc.seedasr.sauc.duration" or "volc.seedasr.sauc.concurrent" => 0,
            "volc.bigasr.sauc.duration" or "volc.bigasr.sauc.concurrent" => 1,
            _ => 2,
        };
    }

    private string SelectedDoubaoResource()
    {
        var suffix = _cloudOptions.ResourceId.EndsWith(".concurrent", StringComparison.Ordinal) ? "concurrent" : "duration";
        return DoubaoModelPicker.SelectedIndex switch
        {
            0 => $"volc.seedasr.sauc.{suffix}",
            1 => $"volc.bigasr.sauc.{suffix}",
            _ => _cloudOptions.ResourceId,
        };
    }

    private CloudOptions ReadCloudOptions() => new(ProviderPicker.SelectedIndex > 0,
        SelectedDoubaoResource(), CloudApiKey.Password.Trim(),
        _cloudOptions.AppId, _cloudOptions.AccessToken,
        Provider: ProviderPicker.SelectedIndex switch { 3 => "selfhosted", 2 => "qwen", _ => "doubao" },
        QwenApiKey: QwenApiKey.Password.Trim(),
        QwenRegion: "cn-beijing",
        QwenModel: QwenModelPicker.SelectedIndex == 1 ? "qwen3-asr-flash-realtime" : "fun-asr-realtime",
        SelfHostedUrl: SelfHostedAddress.ToUrl(SelfHostedUrl.Text),
        SelfHostedModel: SelfHostedModelPicker.SelectedIndex == 1 ? SelfHostedAsrModels.Small : SelfHostedAsrModels.Large);

    private void RefreshCloudStatus()
    {
        if (CloudStatus is null) return;
        CloudStatus.Text = _cloudFieldsLoaded && ProviderPicker.SelectedIndex > 0 && ReadCloudOptions() != _cloudOptions
            ? "有未保存的更改" : _cloudStatusMessage;
        CloudStatus.Visibility = string.IsNullOrWhiteSpace(CloudStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RefreshCloudStatus();
        var provider = ProviderPicker.SelectedIndex switch { 3 => "selfhosted", 2 => "qwen", 1 => "doubao", _ => "local" };
        if (!_cloudFieldsLoaded || provider == _cloudOptions.Backend) return;
        await SwitchProviderAsync(provider);
    }

    private Task ConfigureBackendAsync() => _daemon.SendAsync(JsonSerializer.Serialize(new
    {
        type = "configure-backend",
        provider = _cloudOptions.Backend,
        config = new { apiKey = _cloudOptions.Provider == "qwen" ? _cloudOptions.QwenApiKey : _cloudOptions.ApiKey, region = _cloudOptions.QwenRegion, appId = _cloudOptions.AppId,
            accessToken = _cloudOptions.AccessToken, resourceId = _cloudOptions.ResourceId, url = _cloudOptions.SelfHostedUrl, model = _cloudOptions.Provider == "qwen" ? _cloudOptions.QwenModel : _cloudOptions.SelfHostedModel }
    }));

    private void UpdateModelControls()
    {
        UpdateInstallControls();
        var idle = _readingCancellation is null && _translationCancellation is null && !_installingUpdate && _daemonReady && !_dictationActive && !_togglePending && !_modelChanging && !_loadingModels;
        var cloudIdle = _readingCancellation is null && _translationCancellation is null && !_installingUpdate && _daemonReady && !_dictationActive && !_togglePending && !_modelChanging;
        foreach (var control in new Control[] { ProviderPicker, DoubaoModelPicker, CloudApiKey, QwenApiKey, QwenModelPicker, SelfHostedUrl, SelfHostedModelPicker }) control.IsEnabled = cloudIdle;
        ModelPicker.IsEnabled = idle && !_cloudOptions.Enabled && ModelPicker.Items.Count > 0;
        UpdateModelDownloadControls();
        var transcriptionHotkeyIdle = !_modelChanging && !_dictationActive && !_togglePending
            && _readingCancellation is null && _translationCancellation is null;
        TranscriptionShortcutButton.IsEnabled = transcriptionHotkeyIdle;
        TranscriptionEnabled.IsEnabled = transcriptionHotkeyIdle;
        TrimTrailingPeriodToggle.IsEnabled = !_modelChanging;
        UpdateTranslationControls();
        UpdateDailyControls();
        UpdateReadingControls();
        UpdateServiceControls();
    }

    private void SelectCurrentModel()
    {
        _updatingModelPicker = true;
        ModelPicker.SelectedItem = ModelPicker.Items.OfType<LocalModel>().FirstOrDefault(
            model => string.Equals(model.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase));
        ToolTipService.SetToolTip(ModelPicker, _selectedModelPath);
        _updatingModelPicker = false;
    }

    private async Task RefreshModelsAsync()
    {
        if (!_daemonReady || _dictationActive || _togglePending || _modelChanging || _loadingModels || _closed || _exiting) return;
        _loadingModels = true;
        UpdateModelControls();
        try
        {
            await _daemon.SendAsync("models");
        }
        catch (Exception error)
        {
            _loadingModels = false;
            ShowError("无法读取模型列表", error.Message);
            UpdateModelControls();
        }
    }

    private async void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingModelPicker || ModelPicker.SelectedItem is not LocalModel selected) return;
        if (string.Equals(selected.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase)) return;
        if (_dictationActive || _togglePending || _modelChanging || !_daemonReady)
        {
            SelectCurrentModel();
            return;
        }
        _modelChanging = true;
        UpdateModelControls();
        try
        {
            await _daemon.SendAsync(JsonSerializer.Serialize(new { type = "select-model", path = selected.Path }));
        }
        catch (Exception error)
        {
            _modelChanging = false;
            SelectCurrentModel();
            ShowError("无法切换模型", error.Message);
            UpdateModelControls();
        }
    }

    private void UpdateCleanupControls()
    {
        _updatingCleanupControls = true;
        TrimTrailingPeriodToggle.IsOn = _cleanupOptions.TrimTrailingPeriod;
        _updatingCleanupControls = false;
    }

    private void TextCleanupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingCleanupControls) return;
        var options = new TextCleanupOptions(TrimTrailingPeriodToggle.IsOn);
        try
        {
            TextCleanupSettings.Save(options);
            _cleanupOptions = options;
        }
        catch (Exception error)
        {
            UpdateCleanupControls();
            ShowError("无法保存文本整理设置", error.Message);
        }
    }

    internal async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        _daemonReady = false;
        _startupError = null;
        try
        {
            var workerPath = Path.Combine(AppContext.BaseDirectory, "worker", "dictation-daemon.mjs");
            var bundledNode = Path.Combine(AppContext.BaseDirectory, "node.exe");
            var node = Environment.GetEnvironmentVariable("SHUO_NODE");
            if (string.IsNullOrWhiteSpace(node)) node = Environment.GetEnvironmentVariable("WINDOWS_DICTATION_NODE");
            if (string.IsNullOrWhiteSpace(node)) node = File.Exists(bundledNode) ? bundledNode : "node.exe";
            _modelChanging = true;
            await _daemon.StartAsync(node, workerPath);
            await ConfigureBackendAsync();
        }
        catch (Exception error)
        {
            _started = false;
            _modelChanging = false;
            ShowError("无法启动听写服务", error.Message);
            _overlay.Hide();
        }
    }

    private async Task ToggleAsync(bool preview = false)
    {
        if (_capturingHotkey) return;
        if (_readingCancellation is not null || _translationCancellation is not null) return;
        if (_exiting || _closed || _installingUpdate) return;
        if (_togglePending || _modelChanging || _pendingPastes > 0) return;
        if (!_dictationActive) _dictationPreview = preview;
        _togglePending = true;
        UpdateModelControls();
        try
        {
            await StartAsync();
            if (!_backendConfigured) throw new InvalidOperationException("转录服务尚未就绪，请检查配置或稍后重试。");
            await _daemon.SendAsync("toggle");
        }
        catch (Exception error)
        {
            _togglePending = false;
            ShowError("听写服务不可用", error.Message);
            _overlay.Hide();
            UpdateModelControls();
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs eventArgs) => _ = ToggleAsync();

    private void OnDaemonMessage(object? sender, DaemonMessage message)
    {
        if (message.Type == "ready") _daemonReady = true;
        else if (message.Type == "error" && !_daemonReady) _startupError = message.Error;
        DispatcherQueue.TryEnqueue(() => HandleDaemonMessage(message));
    }

    private void OnDaemonError(object? sender, string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return;
        DispatcherQueue.TryEnqueue(() => ShowError("听写服务错误", error));
    }

    private void OnDaemonExited(object? sender, EventArgs eventArgs)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_exiting || _closed) return;
            _started = false;
            _daemonReady = false;
            _backendConfigured = false;
            _togglePending = false;
            _dictationActive = false;
            _modelChanging = false;
            _loadingModels = false;
            SelectCurrentModel();
            UpdateModelControls();
            _overlay.Hide();
            if (_closed) return;
            var startupError = _startupError;
            if (string.IsNullOrWhiteSpace(startupError))
            {
                ShowError("听写服务已停止", "再次按快捷键可重新启动。");
            }
            else
            {
                ShowError("听写失败", startupError);
            }
        });
    }

    private void HandleDaemonMessage(DaemonMessage message)
    {
        if (_exiting || _closed) return;
        switch (message.Type)
        {
            case "ready":
                _autocorrectPath = message.AutocorrectPath;
                _selectedModelPath = message.ModelPath;
                _loadingModels = true;
                break;
            case "models":
                _updatingModelPicker = true;
                ModelPicker.ItemsSource = message.Models ?? [];
                _updatingModelPicker = false;
                _selectedModelPath = message.ModelPath;
                _loadingModels = false;
                SelectCurrentModel();
                ModelPicker.PlaceholderText = ModelPicker.Items.Count == 0 ? "Qwen3-ASR-0.6B（未下载）" : "选择模型";
                if (_downloadedModelToSelect is { } downloaded)
                {
                    _downloadedModelToSelect = null;
                    if (!_cloudOptions.Enabled && !_dictationActive && !_togglePending && !_modelChanging)
                        ModelPicker.SelectedItem = ModelPicker.Items.OfType<LocalModel>().FirstOrDefault(model => model.Path == downloaded);
                }
                break;
            case "model-list-error":
                _loadingModels = false;
                ShowError("无法读取模型列表", message.Error ?? "未知错误。");
                break;
            case "model-changed":
            case "model-error":
                _modelChanging = false;
                _selectedModelPath = message.ModelPath;
                SelectCurrentModel();
                if (message.Type == "model-error") ShowError("无法切换模型", message.Error ?? "未知错误。");
                break;
            case "backend-configured":
                _backendConfigured = true;
                if (!_cloudTesting) _modelChanging = false;
                if (!_cloudTesting) CloudStatusMessage = "";
                break;
            case "backend-error":
                _modelChanging = false;
                _cloudTesting = false;
                // Block dictation until the selected service is successfully applied.
                _backendConfigured = false;
                CloudStatusMessage = message.Error ?? "无法配置转录服务。";
                break;
            case "cloud-tested":
            case "cloud-test-error":
                _modelChanging = false;
                _cloudTesting = false;
                CloudStatusMessage = message.Type == "cloud-tested"
                    ? $"{_cloudOptions.ServiceName}连接成功，可在下方试用听写。"
                    : message.Error ?? $"{_cloudOptions.ServiceName}连接测试失败。";
                break;
            case "audio-level":
                _overlay.UpdateAudioLevel(message.Level ?? 0);
                return;
            case "partial":
                _overlay.UpdateTranscript(message.Text);
                break;
            case "connecting":
                _dictationActive = true;
                _overlay.Begin(true);
                break;
            case "recording":
                _dictationActive = true;
                _recordingCleanupOptions = _cleanupOptions;
                _togglePending = false;
                _overlay.Recording();
                break;
            case "transcribing":
                _dictationActive = true;
                _togglePending = false;
                _overlay.Transcribing();
                break;
            case "transcript":
                _dictationActive = false;
                _togglePending = false;
                if (_cloudOptions.Enabled) _overlay.Pasting(message.Text);
                _ = PasteTranscriptAsync(message.Text, message.Model);
                break;
            case "busy":
                _togglePending = false;
                break;
            case "empty":
            case "error":
            case "stopped":
                _dictationActive = false;
                _togglePending = false;
                _overlay.Hide();
                break;
        }

        UpdateModelControls();
        if (message.Type == "error") ShowError("听写失败", message.Error ?? "未知错误。");
    }

    private async Task PasteTranscriptAsync(string? text, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _pendingPastes++;
        UpdateModelControls();
        var completedAt = DateTimeOffset.Now;
        var provider = _cloudOptions.Backend == "selfhosted" ? $"自托管 / {model ?? "Qwen3-ASR"}"
            : _cloudOptions.Backend == "qwen" ? model ?? _cloudOptions.QwenModel
            : TranscriptHistory.ModelName(_cloudOptions.Enabled, _cloudOptions.ResourceId, _selectedModelPath);
        try
        {
            var formatted = await TranscriptPaster.PrepareAsync(text, _autocorrectPath, _recordingCleanupOptions);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                try
                {
                    var entry = new TranscriptEntry(completedAt, formatted, provider);
                    _history.Append(entry);
                    if (_historyEntries is not null)
                    {
                        _historyEntries.Insert(0, entry);
                        RenderHistory();
                    }
                }
                catch (Exception error)
                {
                    HistoryNotice.Text = "无法保存本次转录记录：" + error.Message;
                    CloudStatusMessage = HistoryNotice.Text;
                }
            }
            if (_dictationPreview) { ListenResult.Text = formatted; ListenStatus.Text = "试用完成"; }
            else { TranscriptPaster.Paste(formatted, _shutdown.Token); ListenStatus.Text = "已输入"; }
            _overlay.Hide();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Explicit exit cancels any pending paste.
        }
        catch (Exception error)
        {
            ShowError("粘贴失败", error.Message);
            _overlay.Hide();
        }
        finally
        {
            _pendingPastes--;
            UpdateModelControls();
        }
    }

    private void LoadHistory()
    {
        try
        {
            _historyEntries = _history.Load(out var skipped).ToList();
            HistoryNotice.Text = skipped == 0 ? "" : $"有 {skipped} 条损坏记录无法读取，其余记录正常显示。";
            _historyVisibleCount = 50;
            RenderHistory();
        }
        catch (Exception error)
        {
            HistoryEmpty.Visibility = Visibility.Collapsed;
            HistoryNotice.Text = "无法读取转录历史，请点击刷新重试：" + error.Message;
        }
    }

    private void RenderHistory()
    {
        if (_historyEntries is null) return;
        var query = HistorySearch.Text.Trim();
        var matches = _historyEntries.Where(entry =>
            entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        HistoryItems.ItemsSource = matches.Take(_historyVisibleCount).ToArray();
        HistoryCount.Text = query.Length == 0 ? $"共 {_historyEntries.Count} 条" : $"找到 {matches.Length} 条 / 共 {_historyEntries.Count} 条";
        HistoryEmpty.Text = query.Length == 0 ? "还没有转录记录。完成一次听写后会显示在这里。" : "没有匹配的记录。";
        HistoryEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreHistoryButton.Visibility = matches.Length > _historyVisibleCount ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs args)
    {
        _historyVisibleCount = 50;
        RenderHistory();
    }

    private async void OpenHistory_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = TranscriptHistory.DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var opened = await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = true });
            if (!opened) HistoryNotice.Text = "未打开文件。历史文件：" + path;
        }
        catch (Exception error) { HistoryNotice.Text = "无法打开历史文件：" + error.Message; }
    }

    private async void ExportHistory_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var path = _history.ExportText(out var skipped);
            HistoryNotice.Text = skipped == 0 ? "已导出全部记录。每次导出都会更新文本文件。" : $"已导出可读取的记录，跳过 {skipped} 条损坏记录。";
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = true });
        }
        catch (Exception error) { HistoryNotice.Text = "无法导出或打开文本：" + error.Message; }
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs args) => LoadHistory();

    private void MoreHistory_Click(object sender, RoutedEventArgs args)
    {
        _historyVisibleCount += 50;
        RenderHistory();
    }

    private void CopyHistory_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string text }) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            HistoryNotice.Text = "已复制到剪贴板。";
        }
        catch (Exception error) { HistoryNotice.Text = "复制失败：" + error.Message; }
    }

    private async void TranscriptionShortcut_Click(object sender, RoutedEventArgs eventArgs)
    {
        var previous = _hotkeyBinding;
        _hotkey?.Dispose();
        _hotkey = null;
        try
        {
            if (await CaptureHotkeyAsync("听写快捷键", previous ?? HotkeyBinding.Default) is { } selected)
                ApplyHotkey(selected);
            else
                RestoreHotkey();
        }
        catch (Exception error)
        {
            RestoreHotkey();
            ShowError("快捷键未更改", error.Message);
        }
        finally { UpdateHotkeyPreview(); }
    }

    private async Task<HotkeyBinding?> CaptureHotkeyAsync(string title, HotkeyBinding current)
    {
        var selected = current;
        var capture = new TextBox
        {
            IsReadOnly = true,
            Text = current.DisplayText,
            Header = "点击输入框，按住修饰键并按另一个键",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = capture,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
        };
        capture.KeyDown += (_, key) =>
        {
            if (key.Key == VirtualKey.Escape) return;
            key.Handled = true;
            var binding = new HotkeyBinding(CurrentModifiers(), (uint)key.Key);
            if (!binding.IsValid)
            {
                capture.Text = "请按住 Windows、Ctrl、Alt 或 Shift，再按另一个按键";
                return;
            }
            selected = binding;
            capture.Text = binding.DisplayText;
        };
        dialog.Opened += (_, _) => capture.Focus(FocusState.Programmatic);
        _capturingHotkey = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary ? selected : null; }
        finally { _capturingHotkey = false; }
    }

    private void ApplyHotkey(HotkeyBinding? binding)
    {
        if (TranscriptionEnabled.IsOn && ReadingEnabled.IsOn && binding == _readingHotkeyBinding)
            throw new ArgumentException("此组合已用于朗读，请选择其他转录快捷键。");
        if (TranscriptionEnabled.IsOn && TranslationEnabled.IsOn && binding == _translationHotkeyBinding)
            throw new ArgumentException("此组合已用于翻译，请选择其他转录快捷键。");
        GlobalHotkey? replacement = null;
        try
        {
            if (TranscriptionEnabled.IsOn && binding is { } selected)
            {
                replacement = new GlobalHotkey(_window, selected);
                replacement.Pressed += OnHotkeyPressed;
            }

            HotkeySettings.Save(binding);
            _hotkey = replacement;
            _hotkeyBinding = binding;
            UpdateHotkeyPreview();
        }
        catch
        {
            replacement?.Dispose();
            throw;
        }
    }

    private void RestoreHotkey()
    {
        if (!TranscriptionEnabled.IsOn || _hotkey is not null || _hotkeyBinding is not { } binding) return;
        if (!TryRegisterHotkey(binding, out var error))
        {
            ShowError("快捷键不可用", error!.Message);
        }
    }

    private bool TryRegisterHotkey(HotkeyBinding binding, out Exception? error)
    {
        try
        {
            var hotkey = new GlobalHotkey(_window, binding);
            hotkey.Pressed += OnHotkeyPressed;
            _hotkey = hotkey;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception;
            return false;
        }
    }

    private void UpdateHotkeyPreview()
    {
        UpdateDailyControls();
        if (TranscriptionShortcutButton is not null)
            TranscriptionShortcutButton.Content = _hotkeyBinding?.DisplayText ?? "未设置";

    }

    private void TranscriptionToggle_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (!_transcriptionHotkeyLoaded || _updatingTranscriptionToggle) return;
        var enabled = TranscriptionEnabled.IsOn;
        try
        {
            SetTranscriptionHotkeyEnabled(enabled);
        }
        catch (Exception error)
        {
            _updatingTranscriptionToggle = true;
            try { TranscriptionEnabled.IsOn = _hotkey is not null; }
            finally { _updatingTranscriptionToggle = false; }
            ShowError("无法更改语音输入快捷键", error.Message);
        }
        UpdateHotkeyPreview();
    }

    private void SetTranscriptionHotkeyEnabled(bool enabled)
    {
        if (enabled)
        {
            var binding = _hotkeyBinding ?? HotkeyBinding.Default;
            if (ReadingEnabled.IsOn && binding == _readingHotkeyBinding)
                throw new ArgumentException("此组合已用于朗读，请选择其他语音输入快捷键。");
            if (TranslationEnabled.IsOn && binding == _translationHotkeyBinding)
                throw new ArgumentException("此组合已用于翻译，请选择其他语音输入快捷键。");
            if (_hotkey is null && !TryRegisterHotkey(binding, out var registrationError))
                throw registrationError!;
            try
            {
                HotkeySettings.SaveEnabled(true);
            }
            catch
            {
                _hotkey?.Dispose();
                _hotkey = null;
                throw;
            }
            return;
        }

        _hotkey?.Dispose();
        _hotkey = null;
        try
        {
            HotkeySettings.SaveEnabled(false);
        }
        catch
        {
            if (_hotkeyBinding is { } binding) TryRegisterHotkey(binding, out _);
            throw;
        }
    }

    private static uint CurrentModifiers()
    {
        var modifiers = 0u;
        if (NativeMethods.IsKeyDown(NativeMethods.VkControl)) modifiers |= HotkeyBinding.Control;
        if (NativeMethods.IsKeyDown(NativeMethods.VkMenu)) modifiers |= HotkeyBinding.Alt;
        if (NativeMethods.IsKeyDown(NativeMethods.VkShift)) modifiers |= HotkeyBinding.Shift;
        if (NativeMethods.IsKeyDown(NativeMethods.VkLwin) || NativeMethods.IsKeyDown(NativeMethods.VkRwin))
        {
            modifiers |= HotkeyBinding.Windows;
        }

        return modifiers;
    }

    private void ShowError(string title, string detail)
    {
        if (_exiting || _closed) return;
        ErrorInfo.Title = title;
        ErrorInfo.Message = detail;
        ErrorInfo.Severity = InfoBarSeverity.Error;
        ErrorInfo.IsOpen = true;
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _updateTimer?.Stop();
        _shutdown.Cancel();
        _readingCancellation?.Cancel();
        if (_readingTask is not null) await _readingTask;
        _readingSelectionHotkey?.Dispose();
        _translationCancellation?.Cancel();
        if (_translationTask is not null) await _translationTask;
        _translationHotkey?.Dispose();
        _tray.Dispose();
        _hotkey?.Dispose();
        _overlay.Hide();
        AppWindow.Hide();
        try
        {
            await _daemon.DisposeAsync();
        }
        finally
        {
            _overlay.Close();
            if (!_closed) Close();
            Application.Current.Exit();
        }
    }

    private async void OnClosed(object sender, WindowEventArgs eventArgs)
    {
        _closed = true;
        await ExitAsync();
    }
}

internal sealed record ServiceModelOption(string Name, string Model);
