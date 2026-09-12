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

    private bool CanStartTranslation => _readingCancellation is null && !_exiting && !_closed && !_installingUpdate
        && !_dictationActive && !_togglePending && !_modelChanging && _pendingPastes == 0;

    private void InitializeTranslation()
    {
        _overlay.TranslationCloseRequested += StopTranslation;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TranslationModelPicker, TranslationSession.Model);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslationModelPicker, "翻译模型：" + TranslationSession.Model);
        try
        {
            var options = TranslationSettings.Load();
            TranslationEnabled.IsOn = options.Enabled;
            TranslationWorkspace.Text = options.WorkspaceId;
            TranslationApiKey.Password = TranslationSettings.LoadApiKey();
            TranslationLanguage.SelectedIndex = options.TargetLanguage == "en" ? 1 : 0;
            _translationHotkeyBinding = options.Hotkey;
            TranslationShortcutButton.Content = _translationHotkeyBinding.DisplayText;
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
        HotkeyVirtualKey: _translationHotkeyBinding.VirtualKey);

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
            TranslationStatus.Text = $"翻译快捷键已保存：{selected.DisplayText}。";
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
                TranslationSession.Endpoint(options);
                if (string.IsNullOrWhiteSpace(TranslationApiKey.Password))
                    throw new ArgumentException("请填写翻译 API Key。");
            }
            RegisterTranslationHotkey();
            TranslationSettings.Save(options, TranslationApiKey.Password);
            TranslationStatus.Text = options.Enabled
                ? $"设置已保存。按 {_translationHotkeyBinding.DisplayText} 开始或停止实时翻译。"
                : "设置已保存，翻译快捷键已关闭。";
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
    }

    private void StopTranslation()
    {
        if (_translationCancellation is not { } active) return;
        TranslationStatus.Text = "正在停止采集并等待最后一段译文...";
        _overlay.Hide();
        active.Cancel();
    }

    private void UpdateTranslationControls()
    {
        if (TranslationButton is null) return;
        var running = _translationCancellation is not null;
        TranslationButton.Content = running ? "停止翻译" : "开始翻译";
        TranslationButton.IsEnabled = running || TranslationEnabled.IsOn && CanStartTranslation;
        TranslationEnabled.IsEnabled = !running;
        TranslationShortcutButton.IsEnabled = !running;
        TranslationModelPicker.IsEnabled = !running;
        TranslationWorkspace.IsEnabled = !running;
        TranslationApiKey.IsEnabled = !running;
        TranslationLanguage.IsEnabled = !running;
        TranslationSaveButton.IsEnabled = !running;
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
            TranslationSession.Endpoint(options);
            apiKey = TranslationApiKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请填写翻译 API Key。");
            TranslationSettings.Save(options, TranslationApiKey.Password);
        }
        catch (Exception error) { TranslationStatus.Text = error.Message; return; }
        var cancellation = _translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        TranslationStatus.Text = "正在连接百炼实时翻译...";
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
            await Task.Run(() => new TranslationSession(options, apiKey,
                () => Dispatch(() =>
                {
                    if (cancellation.IsCancellationRequested) return;
                    _overlay.Recording();
                    TranslationStatus.Text = $"正在翻译系统声音。再次按 {_translationHotkeyBinding.DisplayText} 可收起并停止。";
                }),
                text => Dispatch(() => _overlay.UpdateTranscript(text)),
                level => Dispatch(() => _overlay.UpdateAudioLevel(level)),
                (text, token) => TranscriptFormatter.FormatAsync(text, _autocorrectPath, token)).RunAsync(cancellation.Token));
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
            if (!_closed && !_exiting) { _overlay.Hide(); UpdateModelControls(); }
        }
    }
}
