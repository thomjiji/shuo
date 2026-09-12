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
    private bool CanStartReading => !_exiting && !_closed && !_installingUpdate && !_dictationActive
        && !_togglePending && !_modelChanging && _pendingPastes == 0 && _translationCancellation is null
        && _readingCancellation is null;

    private void InitializeReading()
    {
        _overlay.TranslationCloseRequested += StopReading;
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
        _readingHotkeyBinding.Modifiers, _readingHotkeyBinding.VirtualKey);

    private void ReadingSpeed_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if (ReadingSpeedValue is null) return;
        var value = (int)Math.Round(args.NewValue);
        ReadingSpeedValue.Text = value switch
        {
            < 0 => $"当前值：{value}（减慢）",
            > 0 => $"当前值：+{value}（加快）",
            _ => "当前值：0（正常）",
        };
    }

    private async void ReadingShortcut_Click(object sender, RoutedEventArgs args)
    {
        var previous = _readingHotkeyBinding;
        var selected = previous;
        var capture = new Microsoft.UI.Xaml.Controls.TextBox
        {
            IsReadOnly = true, Text = previous.DisplayText,
            Header = "点击输入框，按住修饰键并按另一个键",
        };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "朗读快捷键", Content = capture,
            PrimaryButtonText = "保存", CloseButtonText = "取消",
        };
        capture.KeyDown += (_, key) =>
        {
            if (key.Key == Windows.System.VirtualKey.Escape) return;
            key.Handled = true;
            var binding = new HotkeyBinding(CurrentModifiers(), (uint)key.Key);
            if (!binding.IsValid) return;
            selected = binding;
            capture.Text = binding.DisplayText;
        };
        dialog.Opened += (_, _) => capture.Focus(FocusState.Programmatic);
        _readingSelectionHotkey?.Dispose();
        _readingSelectionHotkey = null;
        try
        {
            if (await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                if (selected == _hotkeyBinding) throw new ArgumentException("此组合已用于听写，请选择其他快捷键。");
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
        if (!ReadingEnabled.IsOn) StopReading();
        // Disabling takes effect immediately, including registered shortcuts.
        if (!ReadingEnabled.IsOn) RegisterReadingHotkeys();
        UpdateReadingControls();
    }

    private void UpdateReadingControls()
    {
        if (ReadingButton is null) return;
        var active = _readingCancellation is not null;
        ReadingButton.IsEnabled = ReadingEnabled.IsOn && CanStartReading;
        ReadingStop.IsEnabled = active;
        ReadingPause.IsEnabled = _readingPlayback is not null;
        ReadingPause.Content = _readingPlayback?.Paused == true ? "继续" : "暂停";
        foreach (var control in ReadingConfiguration.Children.OfType<Microsoft.UI.Xaml.Controls.Control>()) control.IsEnabled = !active;
        ReadingSpeedLabels.Opacity = active ? 0.5 : 1;
        ReadingApiKey.IsEnabled = !active && !ReadingUseExistingKey.IsOn;
    }

    private void ReadingButton_Click(object sender, RoutedEventArgs args) => StartReading("text");
    private void ReadingStop_Click(object sender, RoutedEventArgs args) => StopReading();
    private void ReadingPause_Click(object sender, RoutedEventArgs args)
    {
        _readingPlayback?.TogglePause();
        ReadingStatus.Text = _readingPlayback?.Paused == true ? "朗读已暂停。" : "正在朗读。";
        UpdateReadingControls();
    }

    private void StopReading() => _readingCancellation?.Cancel();

    private void StartReading(string source)
    {
        if (_readingCancellation is not null) { StopReading(); return; }
        if (!ReadingEnabled.IsOn || !CanStartReading) return;
        var foreground = NativeMethods.GetForegroundWindow();
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
            var key = options.UseExistingKey ? _cloudOptions.ApiKey : ReadingApiKey.Password.Trim();
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("请填写语音 API Key，或在转录服务中配置火山引擎语音 API Key。");
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
            var chunks = ReadingText.Split(text);
            ReadingSettings.Save(options, ReadingApiKey.Password);
            ReadingStatus.Text = "正在连接豆包语音合成...";
            _overlay.Begin(true);
            using var client = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var service = new DoubaoSpeechClient(client);
            using var playback = new ReadingPlayback();
            _readingPlayback = playback;
            UpdateReadingControls();
            var meter = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            meter.Tick += (_, _) =>
            {
                var status = playback.Paused ? "朗读已暂停。" : playback.Buffering ? "正在合成并缓冲音频..." : "正在朗读，再次按朗读快捷键可停止。";
                ReadingStatus.Text = status;
                _overlay.ReadingAudio(playback.Buffering, playback.Level, status);
            };
            meter.Start();
            try
            {
                foreach (var chunk in chunks)
                {
                    var length = 0L;
                    await foreach (var audio in service.SynthesizeAsync(chunk, options, key, token))
                    {
                        length += audio.Length;
                        await playback.WriteAsync(audio, token);
                    }
                    if ((length & 1) != 0) throw new IOException("语音音频格式不完整。");
                }
                await playback.CompleteAsync(token);
            }
            finally { meter.Stop(); }
            ReadingStatus.Text = "朗读完成。";
        }
        catch (OperationCanceledException)
        {
            if (!_closed && !_exiting) ReadingStatus.Text = cancellation.IsCancellationRequested ? "朗读已停止。" : "语音服务响应超时，请重试。";
        }
        catch (Exception error)
        {
            if (!_closed && !_exiting)
            {
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
            if (!_closed && !_exiting) { _overlay.Hide(); UpdateModelControls(); }
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
        _overlay.Begin(false);
        _overlay.ReadingAudio(false, 0, text);
        try { await Task.Delay(2500, token); }
        catch (OperationCanceledException) { }
    }
}
