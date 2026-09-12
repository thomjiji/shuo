using Microsoft.UI.Xaml;
using Shuo.Services;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _readingCancellation;
    private Task? _readingTask;
    private Task<string>? _selectionRead;
    private ReadingPlayback? _readingPlayback;
    private GlobalHotkey? _readingSelectionHotkey;
    private HotkeyBinding _readingHotkeyBinding = new ReadingOptions().Hotkey;
    private bool _readingLoaded;
    private bool _keepReadingOverlay;
    private bool _testingReadingHost;
    private static readonly double[] LocalReadingSpeeds = [0.85, 1.0, 1.15, 1.3];
    private bool CanStartReading => !_exiting && !_closed && !_installingUpdate && !_dictationActive
        && !_togglePending && !_modelChanging && _pendingPastes == 0 && _translationCancellation is null
        && _readingCancellation is null;

    private void InitializeReading()
    {
        _overlay.TranslationCloseRequested += CloseReading;
        _overlay.ReadingPauseRequested += ToggleReadingPause;
        _overlay.ReadingStopRequested += StopReading;
        _overlay.ReadingCloseRequested += CloseReading;
        try
        {
            var options = ReadingSettings.Load();
            ReadingEnabled.IsOn = options.Enabled;
            ReadingUseExistingKey.IsOn = options.UseExistingKey;
            ReadingApiKey.Password = ReadingSettings.LoadApiKey();
            var voices = ReadingVoices.All;
            var selectedVoice = voices.FirstOrDefault(voice => voice.Id == options.Speaker);
            if (selectedVoice is null)
            {
                selectedVoice = new ReadingVoice("已保存的音色", options.Speaker);
                voices = [selectedVoice, .. voices];
            }
            ReadingSpeaker.ItemsSource = voices;
            ReadingSpeaker.SelectedItem = selectedVoice;
            ReadingSpeed.Value = options.SpeechRate;
            ReadingTranslationSpeed.SelectedIndex = options.TranslationSpeechRate + 1;
            ReadingLocalHost.Text = string.IsNullOrWhiteSpace(options.SelfHostedHost)
                ? SelfHostedAddress.ToDisplay(_cloudOptions.SelfHostedUrl) : options.SelfHostedHost;
            ReadingLocalSpeed.SelectedIndex = Math.Max(0, Array.IndexOf(LocalReadingSpeeds, options.LocalPlaybackSpeed));
            ReadingTranslationBackend.SelectedIndex = options.UseSelfHostedTranslation ? 1 : 0;
            ReadingMode.SelectedIndex = options.TranslateToChinese ? 1 : 0;
            _readingHotkeyBinding = options.Hotkey;
            ReadingShortcutButton.Content = _readingHotkeyBinding.DisplayText;
            RegisterReadingHotkeys();
        }
        catch (Exception error) { ReadingStatus.Text = "无法初始化朗读：" + error.Message; }
        _readingLoaded = true;
        UpdateReadingControls();
    }

    private ReadingOptions CurrentReadingOptions() => new(ReadingEnabled.IsOn, ReadingUseExistingKey.IsOn,
        (ReadingSpeaker.SelectedItem as ReadingVoice)?.Id ?? "", (int)ReadingSpeed.Value,
        _readingHotkeyBinding.Modifiers, _readingHotkeyBinding.VirtualKey, ReadingMode.SelectedIndex == 1,
        ReadingTranslationSpeed.SelectedIndex - 1, ReadingTranslationBackend.SelectedIndex == 1,
        ReadingLocalHost.Text.Trim(), LocalReadingSpeeds[Math.Max(0, ReadingLocalSpeed.SelectedIndex)]);

    private void ReadingLocalSettings_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (!_readingLoaded) return;
        SaveLocalReadingSettings();
        UpdateReadingControls();
    }

    private void ReadingLocalHost_LostFocus(object sender, RoutedEventArgs args)
    {
        if (_readingLoaded) SaveLocalReadingSettings();
    }

    private void SaveLocalReadingSettings()
    {
        try
        {
            var saved = ReadingSettings.Load() with
            {
                UseSelfHostedTranslation = ReadingTranslationBackend.SelectedIndex == 1,
                SelfHostedHost = ReadingLocalHost.Text.Trim(),
                LocalPlaybackSpeed = LocalReadingSpeeds[Math.Max(0, ReadingLocalSpeed.SelectedIndex)],
            };
            ReadingSettings.Save(saved, ReadingSettings.LoadApiKey());
            ReadingStatus.Text = "译读服务设置已保存，下次朗读生效。";
        }
        catch (Exception error) { ReadingStatus.Text = "译读服务设置未保存：" + error.Message; }
    }

    private async void ReadingLocalTest_Click(object sender, RoutedEventArgs args)
    {
        _testingReadingHost = true;
        UpdateReadingControls();
        ReadingStatus.Text = "正在连接 Mac...";
        try
        {
            await SelfHostedReadingClient.TestAsync(ReadingLocalHost.Text, _shutdown.Token);
            if (!_closed) ReadingStatus.Text = "Mac 连接正常，Serena 已准备好。";
        }
        catch (Exception error)
        {
            if (!_closed) ReadingStatus.Text = "Mac 连接失败：" + error.Message;
        }
        finally
        {
            _testingReadingHost = false;
            if (!_closed) UpdateReadingControls();
        }
    }

    private void ReadingTranslationSpeed_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (!_readingLoaded) return;
        try
        {
            var saved = ReadingSettings.Load() with { TranslationSpeechRate = ReadingTranslationSpeed.SelectedIndex - 1 };
            ReadingSettings.Save(saved, ReadingSettings.LoadApiKey());
            ReadingStatus.Text = "中文译读语速已保存，下次朗读生效。";
        }
        catch (Exception error) { ReadingStatus.Text = "中文译读语速未保存：" + error.Message; }
    }

    private void ReadingMode_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (!_readingLoaded) return;
        try
        {
            var saved = ReadingSettings.Load() with { TranslateToChinese = ReadingMode.SelectedIndex == 1 };
            ReadingSettings.Save(saved, ReadingSettings.LoadApiKey());
            ReadingStatus.Text = saved.TranslateToChinese ? "已切换为中文译读。" : "已切换为原文朗读。";
        }
        catch (Exception error) { ReadingStatus.Text = "朗读方式未保存：" + error.Message; }
        UpdateReadingControls();
    }

    private void ReadingTranslationSettings_Click(object sender, RoutedEventArgs args) =>
        SettingsNavigation.SelectedItem = TranslationNavigationItem;

    private void ReadingSpeed_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (ReadingSpeedValue is null) return;
        var value = (int)Math.Round(args.NewValue);
        ReadingSpeedValue.Text = value switch
        {
            < 0 => $"{value}（减慢）",
            > 0 => $"+{value}",
            _ => "0（正常）",
        };
    }

    private async void ReadingShortcut_Click(object sender, RoutedEventArgs args)
    {
        var previous = _readingHotkeyBinding;
        _readingSelectionHotkey?.Dispose();
        _readingSelectionHotkey = null;
        try
        {
            if (await CaptureHotkeyAsync("朗读快捷键", previous) is { } selected)
            {
                if (selected == _hotkeyBinding) throw new ArgumentException("此组合已用于听写，请选择其他快捷键。");
                if (TranslationEnabled.IsOn && selected == _translationHotkeyBinding)
                    throw new ArgumentException("此组合已用于翻译，请选择其他快捷键。");
                _readingHotkeyBinding = selected;
                RegisterReadingHotkeys();
                var saved = ReadingSettings.Load() with { HotkeyModifiers = selected.Modifiers, HotkeyVirtualKey = selected.VirtualKey };
                ReadingSettings.Save(saved, ReadingSettings.LoadApiKey());
                ReadingStatus.Text = $"朗读快捷键已保存：{selected.DisplayText}。";
            }
            else RegisterReadingHotkeys();
        }
        catch (Exception error)
        {
            _readingHotkeyBinding = previous;
            ReadingStatus.Text = "快捷键未更改：" + error.Message;
            try { RegisterReadingHotkeys(); }
            catch (Exception restoreError) { ReadingStatus.Text += " 原快捷键恢复失败：" + restoreError.Message; }
        }
        ReadingShortcutButton.Content = _readingHotkeyBinding.DisplayText;
    }

    private void RegisterReadingHotkeys()
    {
        if (ReadingEnabled.IsOn && _readingHotkeyBinding == _hotkeyBinding)
            throw new ArgumentException("朗读与转录不能使用同一个快捷键，请更换其中一个。");
        if (ReadingEnabled.IsOn && TranslationEnabled.IsOn && _readingHotkeyBinding == _translationHotkeyBinding)
            throw new ArgumentException("朗读与翻译不能使用同一个快捷键，请更换其中一个。");
        _readingSelectionHotkey?.Dispose();
        _readingSelectionHotkey = null;
        if (!ReadingEnabled.IsOn) return;
        try
        {
            _readingSelectionHotkey = new GlobalHotkey(_window, _readingHotkeyBinding, 2);
            _readingSelectionHotkey.Pressed += (_, _) => StartReading("selection");
        }
        catch
        {
            _readingSelectionHotkey?.Dispose();
            _readingSelectionHotkey = null;
            throw;
        }
    }

    private void ReadingSave_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var options = CurrentReadingOptions();
            options.Validate();
            RegisterReadingHotkeys();
            ReadingSettings.Save(options, ReadingApiKey.Password);
            ReadingStatus.Text = options.Enabled ? $"设置已保存。选中文字后按 {_readingHotkeyBinding.DisplayText} 朗读。" : "设置已保存，朗读快捷键已关闭。";
        }
        catch (Exception error) { ReadingStatus.Text = "保存失败：" + error.Message; }
    }

    private void ReadingToggle_Changed(object sender, RoutedEventArgs args)
    {
        if (!_readingLoaded) return;
        if (!ReadingEnabled.IsOn) CloseReading();
        // Disabling takes effect immediately, including registered shortcuts.
        if (!ReadingEnabled.IsOn) RegisterReadingHotkeys();
        UpdateReadingControls();
    }

    private void UpdateReadingControls()
    {
        if (ReadingButton is null) return;
        var active = _readingCancellation is not null;
        var translate = ReadingMode.SelectedIndex == 1;
        var local = ReadingTranslationBackend.SelectedIndex == 1;
        ReadingMode.IsEnabled = !active;
        ReadingTextBox.IsReadOnly = active;
        ReadingButton.Content = translate ? "中文译读" : "原文朗读";
        ReadingModeHint.Text = translate
            ? $"选中文字后按 {_readingHotkeyBinding.DisplayText}，自动识别原文语言并译成中文朗读，支持英文、日文等。"
                + (local ? "翻译和 Serena 语音均在 Mac 上生成。" : "使用实时翻译中的百炼凭据，无需开启实时翻译。")
            : $"选中文字后按 {_readingHotkeyBinding.DisplayText}，使用豆包按原文朗读。再次按快捷键停止。";
        ReadingTranslationBackend.Visibility = translate ? Visibility.Visible : Visibility.Collapsed;
        ReadingTranslationBackend.IsEnabled = !active && !_testingReadingHost;
        ReadingLocalSettings.Visibility = translate && local ? Visibility.Visible : Visibility.Collapsed;
        ReadingLocalHost.IsEnabled = ReadingLocalSpeed.IsEnabled = ReadingLocalTest.IsEnabled = !active && !_testingReadingHost;
        ReadingTranslationSettings.Visibility = translate && !local ? Visibility.Visible : Visibility.Collapsed;
        ReadingTranslationSpeed.Visibility = translate && !local ? Visibility.Visible : Visibility.Collapsed;
        ReadingTranslationSpeed.IsEnabled = !active;
        ReadingTranslatedText.Visibility = translate ? Visibility.Visible : Visibility.Collapsed;
        ReadingButton.IsEnabled = ReadingEnabled.IsOn && CanStartReading;
        ReadingStop.IsEnabled = active;
        ReadingPause.IsEnabled = _readingPlayback is not null;
        ReadingPause.Content = _readingPlayback?.Paused == true ? "继续" : "暂停";
        ReadingEnabled.IsEnabled = !active;
        ReadingShortcutButton.IsEnabled = !active;
        ReadingUseExistingKey.IsEnabled = !active && !translate;
        ReadingSpeaker.IsEnabled = !active && !translate;
        ReadingSpeed.IsEnabled = !active && !translate;
        ReadingSaveButton.IsEnabled = !active;
        ReadingSpeedLabels.Opacity = active || translate ? 0.5 : 1;
        ReadingApiKeyCard.Visibility = ReadingUseExistingKey.IsOn || translate ? Visibility.Collapsed : Visibility.Visible;
        ReadingApiKey.IsEnabled = !active && !translate && !ReadingUseExistingKey.IsOn;
    }

    private void ReadingButton_Click(object sender, RoutedEventArgs args) => StartReading("text");
    private void ReadingStop_Click(object sender, RoutedEventArgs args) => StopReading();
    private void ReadingPause_Click(object sender, RoutedEventArgs args) => ToggleReadingPause();
    private void ToggleReadingPause()
    {
        _readingPlayback?.TogglePause();
        ReadingStatus.Text = _readingPlayback?.Paused == true ? "朗读已暂停。" : "正在朗读。";
        UpdateReadingControls();
    }

    private void StopReading()
    {
        if (_readingCancellation is null) return;
        _keepReadingOverlay = true;
        _readingCancellation.Cancel();
    }

    private void CloseReading()
    {
        _keepReadingOverlay = false;
        _readingCancellation?.Cancel();
        _overlay.Hide();
    }

    private void StartReading(string source)
    {
        if (_capturingHotkey) return;
        if (_readingCancellation is not null) { StopReading(); return; }
        if (!ReadingEnabled.IsOn || !CanStartReading) return;
        var foreground = NativeMethods.GetForegroundWindow();
        _keepReadingOverlay = false;
        var cancellation = _readingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        UpdateModelControls();
        _readingTask = RunReadingAsync(source, foreground, cancellation);
    }

    private async Task RunReadingAsync(string source, IntPtr foreground, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            var options = CurrentReadingOptions();
            options.Validate();
            var translate = options.TranslateToChinese;
            var local = translate && options.UseSelfHostedTranslation;
            var label = translate ? "中文译读" : "原文朗读";
            var key = translate ? TranslationApiKey.Password.Trim()
                : options.UseExistingKey ? _cloudOptions.ApiKey : ReadingApiKey.Password.Trim();
            Uri? endpoint = null;
            if (local)
            {
                endpoint = SelfHostedReadingClient.Endpoint(options.SelfHostedHost);
            }
            else if (translate)
            {
                endpoint = OmniReadingClient.Endpoint(TranslationWorkspace.Text.Trim(), TranslationSettings.Load().Region);
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("请在实时翻译设置中填写百炼 API Key。");
            }
            else if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("请填写语音 API Key，或在转录服务中配置火山引擎语音 API Key。");
            ReadingStatus.Text = source == "selection" ? "正在读取选中文字..." : "正在准备文字...";
            if (translate) ReadingTranslatedText.Text = "";
            string text;
            if (source == "selection")
            {
                text = await ReadSelectedTextAsync(foreground, token);
                if (string.IsNullOrWhiteSpace(text))
                {
                    ReadingStatus.Text = "未获取到选中文字，请重新选择后重试。";
                    await ShowReadingNoticeAsync(ReadingStatus.Text, token);
                    return;
                }
            }
            else text = ReadingTextBox.Text;
            token.ThrowIfCancellationRequested();
            var chunks = ReadingText.Split(text, translate ? OmniReadingClient.PassageBytes : ReadingText.MaximumRequestBytes);
            if (source == "selection") ReadingTextBox.Text = text;
            ReadingSettings.Save(options, ReadingApiKey.Password);
            ReadingStatus.Text = local ? "正在连接 Mac 中文译读..." : translate ? "正在连接千问中文译读..." : "正在连接豆包语音合成...";
            _overlay.BeginReading();
            using var client = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var service = new DoubaoSpeechClient(client);
            using var playback = new ReadingPlayback();
            _readingPlayback = playback;
            UpdateReadingControls();
            var meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            var passage = 0;
            meter.Tick += (_, _) =>
            {
                var state = playback.Paused ? "已暂停" : playback.Buffering ? "正在生成并缓冲音频..." : "正在播放";
                var status = $"{label}：{state}（{passage}/{chunks.Count} 段已请求）";
                ReadingStatus.Text = status;
                _overlay.ReadingAudio(playback.Buffering, playback.Level, status, playback.Paused);
            };
            meter.Start();
            try
            {
                foreach (var chunk in chunks)
                {
                    await playback.WaitForNextPassageAsync(token);
                    passage++;
                    if (translate && passage > 1) ReadingTranslatedText.Text += Environment.NewLine + Environment.NewLine;
                    var length = 0L;
                    Action<string> onText = part =>
                    {
                        // Generated text can lead playback; keep the display explicitly labelled.
                        if (ReadingTranslatedText.Text.Length + part.Length > 60000)
                            throw new IOException("译文过长，请分段选择。");
                        ReadingTranslatedText.Text += part;
                    };
                    var audioStream = local
                        ? SelfHostedReadingClient.ReadAsync(chunk, endpoint!, options.LocalPlaybackSpeed, onText, token)
                        : translate ? new OmniReadingClient(client).ReadAsync(chunk, endpoint!, key, onText, token, options.TranslationSpeechRate)
                        : service.SynthesizeAsync(chunk, options, key, token);
                    await foreach (var audio in audioStream)
                    {
                        length += audio.Length;
                        await playback.WriteAsync(audio, token);
                    }
                    if ((length & 1) != 0) throw new IOException("语音音频格式不完整。");
                }
                await playback.CompleteAsync(token);
            }
            finally { meter.Stop(); }
            ReadingStatus.Text = label + "完成。";
        }
        catch (OperationCanceledException)
        {
            _readingPlayback = null;
            if (!_closed && !_exiting) ReadingStatus.Text = cancellation.IsCancellationRequested ? "朗读已停止。" : "语音服务响应超时，请重试。";
        }
        catch (Exception error)
        {
            _readingPlayback = null;
            if (!_closed && !_exiting)
            {
                UpdateReadingControls();
                ReadingStatus.Text = error is TimeoutException ? "当前应用的选区读取超时，请重新选择文字后重试。" : "朗读失败：" + error.Message;
                // Bring actionable failures into view; successful shortcuts never steal focus.
                if (source != "selection") SettingsNavigation.SelectedItem = ReadingNavigationItem;
                if (source != "selection") ShowSettings();
                else await ShowReadingNoticeAsync(ReadingStatus.Text, cancellation.Token);
            }
        }
        finally
        {
            cancellation.Cancel();
            _readingPlayback = null;
            _readingCancellation = null;
            cancellation.Dispose();
            if (!_closed && !_exiting)
            {
                if (_keepReadingOverlay) _overlay.FinishReading();
                else _overlay.Hide();
                UpdateModelControls();
            }
        }
    }

    private async Task<string> ReadSelectedTextAsync(IntPtr foreground, CancellationToken token)
    {
        // One bounded UIA query at a time; a stalled provider must not block system copy.
        if (_selectionRead is { IsCompleted: false })
            return await Task.Run(() => ClipboardSelection.Capture(foreground, _window, token), token);
        _selectionRead = Task.Run(() => ReadingInput.GetSelection(foreground));
        try
        {
            var selected = await _selectionRead.WaitAsync(TimeSpan.FromMilliseconds(800), token);
            if (!string.IsNullOrWhiteSpace(selected)) return selected;
        }
        catch (TimeoutException) { }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return await Task.Run(() => ClipboardSelection.Capture(foreground, _window, token), token);
    }

    private async Task ShowReadingNoticeAsync(string text, CancellationToken token)
    {
        if (_closed || _exiting) return;
        _overlay.Begin(false, translation: true);
        _overlay.UpdateTranscript(text);
        _overlay.ReadingAudio(false, 0, text);
        try { await Task.Delay(2500, token); }
        catch (OperationCanceledException) { }
    }
}
