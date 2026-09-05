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

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _window = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);
        NativeMethods.SetWindowIcons(_window, iconPath);
        AppWindow.Resize(new SizeInt32(1000, 700));
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
            await _daemon.StartAsync(node, workerPath);
        }
        catch (Exception error)
        {
            _started = false;
            ShowError("无法启动听写服务", error.Message);
            _overlay.Hide();
        }
    }

    private async Task ToggleAsync()
    {
        if (_exiting || _closed) return;
        if (_togglePending) return;
        _togglePending = true;
        try
        {
            await StartAsync();
            await _daemon.SendAsync("toggle");
        }
        catch (Exception error)
        {
            _togglePending = false;
            ShowError("听写服务不可用", error.Message);
            _overlay.Hide();
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
            _togglePending = false;
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
                break;
            case "recording":
                _recordingCleanupOptions = _cleanupOptions;
                _togglePending = false;
                _overlay.Show();
                break;
            case "transcribing":
                _togglePending = false;
                _overlay.Hide();
                break;
            case "transcript":
                _togglePending = false;
                _ = PasteTranscriptAsync(message.Text);
                break;
            case "busy":
                _togglePending = false;
                break;
            case "empty":
            case "error":
            case "stopped":
                _togglePending = false;
                _overlay.Hide();
                break;
        }

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
