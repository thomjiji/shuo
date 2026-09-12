using Microsoft.UI.Xaml;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _translationCancellation;
    private Task? _translationTask;
    private GlobalHotkey? _translationHotkey;
    private HotkeyBinding _translationHotkeyBinding = new TranslationOptions().Hotkey;
    private bool _translationLoaded;
    private bool _translationPaused;
    private bool _keepTranslationOverlay;
    private string? _pausedTranslationText;

    private bool CanStartTranslation => _readingCancellation is null && !_exiting && !_closed && !_installingUpdate
        && !_dictationActive && !_togglePending && !_modelChanging && _pendingPastes == 0;

    private void InitializeTranslation()
    {
        _overlay.TranslationCloseRequested += CloseTranslation;
        _overlay.TranslationPauseRequested += PauseTranslation;
        _overlay.TranslationStopRequested += StopTranslation;
        try
        {
            var options = TranslationSettings.Load();
            TranslationEnabled.IsOn = options.Enabled;
            TranslationWorkspace.Text = options.WorkspaceId;
            TranslationApiKey.Password = TranslationSettings.LoadApiKey();
            TranslationLanguage.SelectedIndex = options.TargetLanguage == "en" ? 1 : 0;
            TranslationModelPicker.SelectedIndex = options.Backend == "self-hosted" ? 1 : 0;
            TranslationHost.Text = string.IsNullOrWhiteSpace(options.Host) ? ReadingSettings.Load().SelfHostedHost : options.Host;
            _translationHotkeyBinding = options.Hotkey;
            TranslationShortcutButton.Content = _translationHotkeyBinding.DisplayText;
            TranslationSettingsExpander.IsExpanded = !options.Enabled || (options.Backend == "self-hosted"
                ? string.IsNullOrWhiteSpace(TranslationHost.Text)
                : string.IsNullOrWhiteSpace(options.WorkspaceId) || string.IsNullOrWhiteSpace(TranslationApiKey.Password));
            if (!options.Enabled) TranslationStatus.Text = "未启用";
            RegisterTranslationHotkey();
        }
        catch (Exception error) { TranslationStatus.Text = "无法读取翻译设置：" + error.Message; }
        _translationLoaded = true;
        UpdateTranslationControls();
    }

    private TranslationOptions CurrentTranslationOptions() => new(
        WorkspaceId: TranslationWorkspace.Text.Trim(),
        TargetLanguage: TranslationLanguage.SelectedIndex == 1 ? "en" : "zh",
        Enabled: TranslationEnabled.IsOn,
        HotkeyModifiers: _translationHotkeyBinding.Modifiers,
        HotkeyVirtualKey: _translationHotkeyBinding.VirtualKey,
        Backend: TranslationModelPicker.SelectedIndex == 1 ? "self-hosted" : "cloud",
        Host: TranslationHost.Text.Trim());

    private void TranslationBackend_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (!_translationLoaded) return;
        UpdateTranslationControls();
        var needsSetup = TranslationModelPicker.SelectedIndex == 1
            ? string.IsNullOrWhiteSpace(TranslationHost.Text)
            : string.IsNullOrWhiteSpace(TranslationWorkspace.Text) || string.IsNullOrWhiteSpace(TranslationApiKey.Password);
        if (needsSetup) TranslationSettingsExpander.IsExpanded = true;
    }

    private static void ValidateTranslation(TranslationOptions options, string apiKey)
    {
        if (options.Backend == "self-hosted") SelfHostedTranslationSession.Endpoint(options.Host);
        else
        {
            TranslationSession.Endpoint(options);
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请填写翻译 API Key。");
        }
    }

    private async void TranslationShortcut_Click(object sender, RoutedEventArgs args)
    {
        var previous = _translationHotkeyBinding;
        _translationHotkey?.Dispose();
        _translationHotkey = null;
        try
        {
            if (await CaptureHotkeyAsync("翻译快捷键", previous) is not { } selected)
            {
                RegisterTranslationHotkey();
                return;
            }
            if (selected == _hotkeyBinding) throw new ArgumentException("此组合已用于听写，请选择其他快捷键。");
            if (ReadingEnabled.IsOn && selected == _readingHotkeyBinding)
                throw new ArgumentException("此组合已用于朗读，请选择其他快捷键。");
            _translationHotkeyBinding = selected;
            RegisterTranslationHotkey();
            var saved = TranslationSettings.Load() with
            {
                HotkeyModifiers = selected.Modifiers,
                HotkeyVirtualKey = selected.VirtualKey,
            };
            TranslationSettings.Save(saved, TranslationSettings.LoadApiKey());
            TranslationStatus.Text = "快捷键已保存";
        }
        catch (Exception error)
        {
            _translationHotkeyBinding = previous;
            TranslationStatus.Text = "快捷键未更改：" + error.Message;
            try { RegisterTranslationHotkey(); }
            catch (Exception restoreError) { TranslationStatus.Text += " 原快捷键恢复失败：" + restoreError.Message; }
        }
        finally { TranslationShortcutButton.Content = _translationHotkeyBinding.DisplayText; }
    }

    private void RegisterTranslationHotkey()
    {
        if (TranslationEnabled.IsOn && _translationHotkeyBinding == _hotkeyBinding)
            throw new ArgumentException("翻译与听写不能使用同一个快捷键，请更换其中一个。");
        if (TranslationEnabled.IsOn && ReadingEnabled.IsOn && _translationHotkeyBinding == _readingHotkeyBinding)
            throw new ArgumentException("翻译与朗读不能使用同一个快捷键，请更换其中一个。");
        _translationHotkey?.Dispose();
        _translationHotkey = null;
        if (!TranslationEnabled.IsOn) return;
        try
        {
            _translationHotkey = new GlobalHotkey(_window, _translationHotkeyBinding, 3);
            _translationHotkey.Pressed += (_, _) => ToggleTranslation();
        }
        catch
        {
            _translationHotkey?.Dispose();
            _translationHotkey = null;
            throw;
        }
    }

    private void TranslationSave_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var options = CurrentTranslationOptions();
            if (options.Enabled)
            {
                ValidateTranslation(options, TranslationApiKey.Password);
            }
            RegisterTranslationHotkey();
            TranslationSettings.Save(options, TranslationApiKey.Password);
            TranslationStatus.Text = options.Enabled ? "已保存" : "已保存，未启用";
        }
        catch (Exception error) { TranslationStatus.Text = "保存失败：" + error.Message; }
    }

    private void TranslationToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (!_translationLoaded) return;
        if (!TranslationEnabled.IsOn)
        {
            StopTranslation();
            RegisterTranslationHotkey();
        }
        UpdateTranslationControls();
        if (_translationCancellation is null) TranslationStatus.Text = TranslationEnabled.IsOn ? "" : "未启用";
    }

    private void StopTranslation()
    {
        if (_translationCancellation is not { } active) return;
        _keepTranslationOverlay = true;
        Volatile.Write(ref _translationPaused, false);
        _pausedTranslationText = null;
        TranslationStatus.Text = "正在接收最后一段译文...";
        _overlay.FinishTranslation();
        active.Cancel();
    }

    private void CloseTranslation()
    {
        if (_translationCancellation is not null) StopTranslation();
        _keepTranslationOverlay = false;
        _overlay.Hide();
    }

    private void PauseTranslation()
    {
        if (_translationCancellation is not { IsCancellationRequested: false }) return;
        Volatile.Write(ref _translationPaused, !_translationPaused);
        _overlay.TranslationPaused(_translationPaused);
        if (!_translationPaused && _pausedTranslationText is { } text)
        {
            _overlay.UpdateTranscript(text);
            _pausedTranslationText = null;
        }
        TranslationStatus.Text = _translationPaused ? "已暂停" : "正在翻译系统声音";
    }

    private void UpdateTranslationControls()
    {
        if (TranslationButton is null) return;
        var running = _translationCancellation is not null;
        var settingsBusy = running || _readingCancellation is not null;
        TranslationButton.Content = running ? "停止翻译" : "开始翻译";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TranslationButton, _translationHotkeyBinding.DisplayText);
        TranslationButton.IsEnabled = running || TranslationEnabled.IsOn && CanStartTranslation;
        TranslationEnabled.IsEnabled = !settingsBusy;
        TranslationShortcutButton.IsEnabled = !settingsBusy;
        TranslationModelPicker.IsEnabled = !settingsBusy;
        TranslationHost.IsEnabled = !settingsBusy;
        var local = TranslationModelPicker.SelectedIndex == 1;
        TranslationHostLabel.Text = local ? "Mac 主机 IP" : "Workspace ID";
        TranslationHost.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        TranslationWorkspace.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        TranslationCredentialCard.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        TranslationWorkspace.IsEnabled = !settingsBusy;
        TranslationApiKey.IsEnabled = !settingsBusy;
        TranslationLanguage.IsEnabled = !settingsBusy;
        TranslationSaveButton.IsEnabled = !settingsBusy;
    }

    private void TranslationButton_Click(object sender, RoutedEventArgs args) => ToggleTranslation();

    private void ToggleTranslation()
    {
        if (_capturingHotkey) return;
        if (_translationCancellation is not null) { StopTranslation(); return; }
        if (!TranslationEnabled.IsOn) return;
        if (!CanStartTranslation) return;
        var options = CurrentTranslationOptions();
        string apiKey;
        try
        {
            apiKey = TranslationApiKey.Password.Trim();
            ValidateTranslation(options, apiKey);
            TranslationSettings.Save(options, TranslationApiKey.Password);
        }
        catch (Exception error)
        {
            TranslationSettingsExpander.IsExpanded = true;
            TranslationStatus.Text = error.Message;
            return;
        }
        var cancellation = _translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _keepTranslationOverlay = true;
        Volatile.Write(ref _translationPaused, false);
        _pausedTranslationText = null;
        TranslationStatus.Text = options.Backend == "self-hosted" ? "正在连接 Mac 实时翻译..." : "正在连接百炼实时翻译...";
        _overlay.Begin(true, translation: true);
        UpdateModelControls();
        _translationTask = RunTranslationAsync(options, apiKey, cancellation);
    }

    private async Task RunTranslationAsync(TranslationOptions options, string apiKey, CancellationTokenSource cancellation)
    {
        void Dispatch(Action action) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_exiting && !_closed
                && ReferenceEquals(_translationCancellation, cancellation)) action();
        });
        try
        {
            void Ready() => Dispatch(() =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    _overlay.TranslationPaused(_translationPaused);
                    TranslationStatus.Text = _translationPaused ? "已暂停" : "正在翻译系统声音";
                });
            void Caption(string text) => Dispatch(() =>
            {
                if (_translationPaused) _pausedTranslationText = text;
                else _overlay.UpdateTranscript(text);
            });
            void Level(double level) => Dispatch(() => { if (!_translationPaused) _overlay.UpdateAudioLevel(level); });
            Task<string> Format(string text, CancellationToken token) => TranscriptFormatter.FormatAsync(text, _autocorrectPath, token);
            await Task.Run(() =>
            {
                var audio = SystemAudioSource.ReadAsync(Level, cancellation.Token, () => Volatile.Read(ref _translationPaused));
                return options.Backend == "self-hosted"
                    ? new SelfHostedTranslationSession(options, Ready, Caption, Format).RunAsync(audio, cancellation.Token)
                    : new TranslationSession(options, apiKey, Ready, Caption, Level, Format).RunAsync(cancellation.Token, audio);
            });
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译已停止。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译已停止。";
        }
        catch (Exception error)
        {
            if (!_closed && !_exiting) TranslationStatus.Text = "翻译失败：" + error.Message;
        }
        finally
        {
            _translationCancellation = null;
            cancellation.Dispose();
            if (!_closed && !_exiting)
            {
                if (_keepTranslationOverlay) _overlay.FinishTranslation(); else _overlay.Hide();
                UpdateModelControls();
            }
        }
    }
}
