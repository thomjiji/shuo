using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Shuo.Services;
using WinRT.Interop;

namespace Shuo;

public sealed partial class MainWindow : Window
{
    private const int MinimumWindowWidth = 840;
    private const int MinimumWindowHeight = 600;
    private static readonly Brush ShortcutKeyBrush = new SolidColorBrush(Color.FromArgb(255, 76, 185, 242));
    private static readonly Brush ShortcutKeyForeground = new SolidColorBrush(Color.FromArgb(255, 10, 10, 10));

    private readonly DaemonClient _daemon = new();
    private readonly OverlayWindow _overlay = new();
    private readonly IntPtr _window;
    private readonly TrayIcon _tray;
    private readonly CancellationTokenSource _shutdown = new();
    private TextCleanupOptions _cleanupOptions = new();
    private TextCleanupOptions _recordingCleanupOptions = new();
    private bool _updatingCleanupControls = true;
    private bool _exiting;
    private HotkeyBinding? _hotkeyBinding;
    private HotkeyBinding? _draftHotkey;
    private GlobalHotkey? _hotkey;
    private string? _autocorrectPath;
    private bool _shortcutEditorOpen;
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

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _window = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        NativeMethods.SetWindowIcons(_window, iconPath);
        AppWindow.Resize(new SizeInt32(900, 820));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = MinimumWindowWidth;
            presenter.PreferredMinimumHeight = MinimumWindowHeight;
        }
        _tray = new TrayIcon(iconPath,
            () => DispatcherQueue.TryEnqueue(ShowSettings),
            () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync()));
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Closing += OnWindowClosing;

        _daemon.MessageReceived += OnDaemonMessage;
        _daemon.ErrorReceived += OnDaemonError;
        _daemon.Exited += OnDaemonExited;
        Closed += OnClosed;

        _hotkeyBinding = HotkeySettings.Load();
        if (_hotkeyBinding is { } binding && !TryRegisterHotkey(binding, out var error))
        {
            ShowError("快捷键不可用", error!.Message);
        }

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
            ProviderPicker.SelectedIndex = _cloudOptions.Enabled ? 1 : 0;
            CloudApiKey.Password = _cloudOptions.ApiKey;
            CloudAppId.Text = _cloudOptions.AppId;
            CloudAccessToken.Password = _cloudOptions.AccessToken;
            CloudResourceId.Text = _cloudOptions.ResourceId;
        }
        catch (Exception cloudError) { CloudStatus.Text = cloudError.Message; }
    }

    internal void ShowSettings()
    {
        if (_exiting || _closed) return;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        if (_shortcutEditorOpen) CloseShortcutEditor(false);
        AppWindow.Hide();
    }

    private CloudOptions _cloudOptions = new();
    private bool _cloudTesting;
    private bool _backendConfigured;

    private void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CloudFields is null || SaveCloudButton is null) return;
        CloudFields.Visibility = ProviderPicker.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SaveCloudButton.Content = ProviderPicker.SelectedIndex == 1 ? "保存并测试" : "保存";
    }

    private Task ConfigureBackendAsync() => _daemon.SendAsync(JsonSerializer.Serialize(new
    {
        type = "configure-backend",
        provider = _cloudOptions.Enabled ? "doubao" : "local",
        config = new { apiKey = _cloudOptions.ApiKey, appId = _cloudOptions.AppId,
            accessToken = _cloudOptions.AccessToken, resourceId = _cloudOptions.ResourceId }
    }));

    private async void SaveCloudButton_Click(object sender, RoutedEventArgs args)
    {
        if (_dictationActive || _togglePending || _modelChanging || !_daemonReady) return;
        try
        {
            var options = new CloudOptions(ProviderPicker.SelectedIndex == 1,
                CloudResourceId.Text.Trim(), CloudApiKey.Password.Trim(),
                CloudAppId.Text.Trim(), CloudAccessToken.Password.Trim());
            CloudSettings.Save(options);
            _cloudOptions = options;
            _modelChanging = true;
            _cloudTesting = options.Enabled;
            CloudStatus.Text = options.Enabled ? "正在测试豆包连接..." : "正在切换到本地模型...";
            UpdateModelControls();
            await ConfigureBackendAsync();
            if (_cloudTesting) await _daemon.SendAsync("test-cloud");
        }
        catch (Exception error)
        {
            _modelChanging = false;
            _cloudTesting = false;
            CloudStatus.Text = error.Message;
            UpdateModelControls();
        }
    }

    private void UpdateModelControls()
    {
        var idle = _daemonReady && !_dictationActive && !_togglePending && !_modelChanging && !_loadingModels;
        var cloudIdle = _daemonReady && !_dictationActive && !_togglePending && !_modelChanging;
        foreach (var control in new Control[] { ProviderPicker, CloudApiKey, CloudAppId, CloudAccessToken, CloudResourceId, SaveCloudButton }) control.IsEnabled = cloudIdle;
        ModelPicker.IsEnabled = idle && !_cloudOptions.Enabled && ModelPicker.Items.Count > 0;
        RefreshModelsButton.IsEnabled = _daemonReady && !_dictationActive && !_togglePending && !_modelChanging && !_loadingModels;
        EditShortcutButton.IsEnabled = !_modelChanging;
        RemoveFillerWordsToggle.IsEnabled = !_modelChanging;
        TrimTrailingPeriodToggle.IsEnabled = !_modelChanging;
    }

    private void SelectCurrentModel()
    {
        _updatingModelPicker = true;
        ModelPicker.SelectedItem = ModelPicker.Items.OfType<LocalModel>().FirstOrDefault(
            model => string.Equals(model.Path, _selectedModelPath, StringComparison.OrdinalIgnoreCase));
        ToolTipService.SetToolTip(ModelPicker, _selectedModelPath);
        _updatingModelPicker = false;
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs args)
    {
        _loadingModels = true;
        ModelStatus.Text = "正在读取模型...";
        UpdateModelControls();
        try
        {
            await _daemon.SendAsync("models");
        }
        catch (Exception error)
        {
            _loadingModels = false;
            ModelStatus.Text = "无法读取模型列表，请重试。";
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
        if (_shortcutEditorOpen) CloseShortcutEditor(false);
        _modelChanging = true;
        ModelStatus.Text = "正在加载模型...";
        UpdateModelControls();
        try
        {
            await _daemon.SendAsync(JsonSerializer.Serialize(new { type = "select-model", path = selected.Path }));
        }
        catch (Exception error)
        {
            _modelChanging = false;
            SelectCurrentModel();
            ModelStatus.Text = "切换失败，仍使用原模型。";
            ShowError("无法切换模型", error.Message);
            UpdateModelControls();
        }
    }

    private void UpdateCleanupControls()
    {
        _updatingCleanupControls = true;
        RemoveFillerWordsToggle.IsOn = _cleanupOptions.RemoveFillerWords;
        TrimTrailingPeriodToggle.IsOn = _cleanupOptions.TrimTrailingPeriod;
        _updatingCleanupControls = false;
    }

    private void TextCleanupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_updatingCleanupControls) return;
        var options = new TextCleanupOptions(RemoveFillerWordsToggle.IsOn, TrimTrailingPeriodToggle.IsOn);
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

    private async Task ToggleAsync()
    {
        if (_exiting || _closed) return;
        if (_togglePending || _modelChanging) return;
        _togglePending = true;
        UpdateModelControls();
        try
        {
            await StartAsync();
            if (!_backendConfigured) throw new InvalidOperationException("请先保存转录服务设置。");
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
                ModelStatus.Text = ModelPicker.Items.Count == 0
                    ? "没有找到模型。用 pi-transcribe 下载后，点击刷新。"
                    : "用 pi-transcribe 下载模型后，点击刷新。";
                break;
            case "model-list-error":
                _loadingModels = false;
                ModelStatus.Text = "无法读取模型列表，请重试。";
                ShowError("无法读取模型列表", message.Error ?? "未知错误。");
                break;
            case "model-changed":
            case "model-error":
                _modelChanging = false;
                _selectedModelPath = message.ModelPath;
                SelectCurrentModel();
                ModelStatus.Text = message.Type == "model-changed"
                    ? "模型已切换，下次听写生效。"
                    : "切换失败，仍使用原模型。";
                if (message.Type == "model-error") ShowError("无法切换模型", message.Error ?? "未知错误。");
                break;
            case "backend-configured":
                _backendConfigured = true;
                if (!_cloudTesting) _modelChanging = false;
                if (!_cloudTesting) CloudStatus.Text = _cloudOptions.Enabled
                    ? "当前使用豆包云端。按快捷键开始流式听写。" : "当前使用本地模型。";
                break;
            case "backend-error":
                _modelChanging = false;
                _cloudTesting = false;
                // Block dictation until the selected service is successfully applied.
                _backendConfigured = false;
                CloudStatus.Text = message.Error ?? "无法配置转录服务。";
                break;
            case "cloud-tested":
            case "cloud-test-error":
                _modelChanging = false;
                _cloudTesting = false;
                CloudStatus.Text = message.Type == "cloud-tested"
                    ? "豆包连接成功。按快捷键开始说话，再按一次结束。"
                    : message.Error ?? "豆包连接测试失败。";
                break;
            case "partial":
                LiveTranscript.Text = message.Text ?? "";
                LiveTranscript.Visibility = Visibility.Visible;
                _overlay.UpdateTranscript(message.Text);
                break;
            case "connecting":
                _dictationActive = true;
                _overlay.Begin(true);
                CloudStatus.Text = "正在连接豆包...";
                LiveTranscript.Text = "";
                LiveTranscript.Visibility = Visibility.Collapsed;
                break;
            case "recording":
                _dictationActive = true;
                _recordingCleanupOptions = _cleanupOptions;
                if (_cloudOptions.Enabled) CloudStatus.Text = "正在录音并转录...";
                _togglePending = false;
                _overlay.Recording();
                break;
            case "transcribing":
                _dictationActive = true;
                _togglePending = false;
                _overlay.Transcribing();
                break;
            case "transcript":
                if (_cloudOptions.Enabled) { LiveTranscript.Text = message.Text ?? ""; CloudStatus.Text = "转录完成。"; }
                _dictationActive = false;
                _togglePending = false;
                _overlay.Pasting(message.Text);
                _ = PasteTranscriptAsync(message.Text);
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

    private async Task PasteTranscriptAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await TranscriptPaster.PasteAsync(text, _autocorrectPath, _recordingCleanupOptions, _shutdown.Token);
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
    }

    private void EditShortcutButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_shortcutEditorOpen) return;
        _shortcutEditorOpen = true;
        _draftHotkey = _hotkeyBinding;
        _hotkey?.Dispose();
        _hotkey = null;
        RenderShortcutEditor();
        ShortcutEditorPanel.Visibility = Visibility.Visible;
        EditShortcutButton.Visibility = Visibility.Collapsed;
        HotkeyPreview.Visibility = Visibility.Collapsed;
        FocusShortcutCapture();
    }

    private void ShortcutCaptureSurface_KeyDown(object sender, KeyRoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;

        if (eventArgs.Key == VirtualKey.Escape)
        {
            CloseShortcutEditor(false);
            return;
        }

        var virtualKey = (uint)eventArgs.Key;
        if (HotkeyBinding.IsModifierKey(virtualKey)) return;

        var binding = new HotkeyBinding(CurrentModifiers(), virtualKey);
        if (!binding.IsValid)
        {
            SetShortcutEditorValidation("请按住 Windows、Ctrl、Alt 或 Shift，再按另一个按键。", false);
            return;
        }

        _draftHotkey = binding;
        RenderShortcutEditor();
    }

    private void ResetShortcutButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _draftHotkey = HotkeyBinding.Default;
        RenderShortcutEditor();
        FocusShortcutCapture();
    }

    private void ClearShortcutButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        _draftHotkey = null;
        RenderShortcutEditor();
        FocusShortcutCapture();
    }

    private void SaveShortcutButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            ApplyHotkey(_draftHotkey);
            CloseShortcutEditor(true);
        }
        catch (Exception error)
        {
            SetShortcutEditorValidation($"无法使用此快捷键：{error.Message}", true);
        }
    }

    private void CancelShortcutButton_Click(object sender, RoutedEventArgs eventArgs) => CloseShortcutEditor(false);

    private void CloseShortcutEditor(bool saved)
    {
        if (!saved) RestoreHotkey();
        ShortcutEditorPanel.Visibility = Visibility.Collapsed;
        EditShortcutButton.Visibility = Visibility.Visible;
        HotkeyPreview.Visibility = Visibility.Visible;
        _shortcutEditorOpen = false;
    }

    private void RenderShortcutEditor()
    {
        SetKeyChips(ShortcutEditorKeys, _draftHotkey);
        ShortcutEditorPlaceholder.Visibility = _draftHotkey is null ? Visibility.Visible : Visibility.Collapsed;
        ShortcutEditorPlaceholder.Text = _draftHotkey is null ? "未设置" : "按下新的快捷键";
        SetShortcutEditorValidation(
            _draftHotkey is null
                ? "清除后将无法通过全局快捷键开始听写。"
                : "按下新的组合键后，点击保存应用。",
            true);
    }

    private void SetShortcutEditorValidation(string text, bool canSave)
    {
        ShortcutEditorValidation.Text = text;
        SaveShortcutButton.IsEnabled = canSave;
    }

    private void FocusShortcutCapture()
    {
        DispatcherQueue.TryEnqueue(() => { ShortcutCaptureSurface.Focus(FocusState.Programmatic); });
    }

    private void ApplyHotkey(HotkeyBinding? binding)
    {
        GlobalHotkey? replacement = null;
        try
        {
            if (binding is { } selected)
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
        if (_hotkey is not null || _hotkeyBinding is not { } binding) return;
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
        HotkeyPreview.Children.Clear();
        if (_hotkeyBinding is not { } binding)
        {
            HotkeyPreview.Children.Add(new TextBlock
            {
                Text = "未设置",
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        SetKeyChips(HotkeyPreview, binding);
    }

    private static void SetKeyChips(StackPanel target, HotkeyBinding? binding)
    {
        target.Children.Clear();
        if (binding is not { } hotkey) return;

        foreach (var label in hotkey.KeyLabels)
        {
            target.Children.Add(CreateKeyChip(label));
        }
    }

    private static Border CreateKeyChip(string label)
    {
        return new Border
        {
            MinWidth = 32,
            Height = 32,
            Padding = new Thickness(6, 3, 6, 3),
            Background = ShortcutKeyBrush,
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ShortcutKeyForeground,
                FontFamily = new FontFamily(label is "⊞" or "⇧" ? "Segoe UI Symbol" : "Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            },
        };
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
        _shutdown.Cancel();
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
