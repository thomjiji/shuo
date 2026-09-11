using Microsoft.UI.Xaml;
using Shuo.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Shuo;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _readingCancellation;
    private Task? _readingTask;
    private Task<string>? _selectionRead;
    private ReadingPlayback? _readingPlayback;
    private GlobalHotkey? _readingSelectionHotkey;
    private GlobalHotkey? _readingClipboardHotkey;
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
            ReadingSpeaker.Text = options.Speaker;
            ReadingSpeed.Value = options.SpeechRate;
            RegisterReadingHotkeys();
        }
        catch (Exception error) { ReadingStatus.Text = "无法初始化朗读：" + error.Message; }
        _readingLoaded = true;
        UpdateReadingControls();
    }

    private ReadingOptions CurrentReadingOptions() => new(ReadingEnabled.IsOn, ReadingUseExistingKey.IsOn,
        ReadingSpeaker.Text.Trim(), (int)ReadingSpeed.Value);

    private void RegisterReadingHotkeys()
    {
        _readingSelectionHotkey?.Dispose();
        _readingClipboardHotkey?.Dispose();
        _readingSelectionHotkey = _readingClipboardHotkey = null;
        if (!ReadingEnabled.IsOn) return;
        try
        {
            _readingSelectionHotkey = new GlobalHotkey(_window, new(HotkeyBinding.Control | HotkeyBinding.Alt, 0x52), 2);
            _readingSelectionHotkey.Pressed += (_, _) => StartReading("selection");
            _readingClipboardHotkey = new GlobalHotkey(_window, new(HotkeyBinding.Control | HotkeyBinding.Alt | HotkeyBinding.Shift, 0x52), 3);
            _readingClipboardHotkey.Pressed += (_, _) => StartReading("clipboard");
        }
        catch
        {
            _readingSelectionHotkey?.Dispose();
            _readingClipboardHotkey?.Dispose();
            _readingSelectionHotkey = _readingClipboardHotkey = null;
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
            ReadingStatus.Text = options.Enabled ? "设置已保存。选中文字后按 Ctrl+Alt+R 朗读。" : "设置已保存，朗读快捷键已关闭。";
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
        ReadingButton.IsEnabled = ReadingClipboardButton.IsEnabled = ReadingEnabled.IsOn && CanStartReading;
        ReadingStop.IsEnabled = active;
        ReadingPause.IsEnabled = _readingPlayback is not null;
        ReadingPause.Content = _readingPlayback?.Paused == true ? "继续" : "暂停";
        foreach (var control in ReadingConfiguration.Children.OfType<Microsoft.UI.Xaml.Controls.Control>()) control.IsEnabled = !active;
        ReadingApiKey.IsEnabled = !active && !ReadingUseExistingKey.IsOn;
    }

    private void ReadingButton_Click(object sender, RoutedEventArgs args) => StartReading("text");
    private void ReadingClipboard_Click(object sender, RoutedEventArgs args) => StartReading("clipboard");
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
                if (_selectionRead is { IsCompleted: false }) throw new IOException("上一次选区读取仍未返回，请复制文字后使用剪贴板朗读。");
                _selectionRead = Task.Run(() => ReadingInput.GetSelection(foreground));
                text = await _selectionRead.WaitAsync(TimeSpan.FromSeconds(3), token);
                if (string.IsNullOrWhiteSpace(text)) throw new IOException("未获取到选中文字，请复制后按 Ctrl+Alt+Shift+R 朗读剪贴板。");
            }
            else if (source == "clipboard")
            {
                var clipboard = Clipboard.GetContent();
                if (!clipboard.Contains(StandardDataFormats.Text)) throw new IOException("剪贴板中没有文字。");
                text = await clipboard.GetTextAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token);
            }
            else text = ReadingTextBox.Text;
            token.ThrowIfCancellationRequested();
            var chunks = ReadingText.Split(text);
            ReadingSettings.Save(options, ReadingApiKey.Password);
            ReadingStatus.Text = "正在连接豆包语音合成...";
            _overlay.Begin(true, translation: true);
            _overlay.UpdateTranscript("正在准备朗读...");
            using var client = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var service = new DoubaoSpeechClient(client);
            using var playback = new ReadingPlayback();
            _readingPlayback = playback;
            UpdateReadingControls();
            for (var index = 0; index < chunks.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var playing = false;
                await foreach (var bytes in service.SynthesizeAsync(chunks[index], options, key, token))
                {
                    await playback.WriteAsync(bytes, token);
                    if (!playing)
                    {
                        playing = true;
                        ReadingStatus.Text = $"正在朗读第 {index + 1}/{chunks.Count} 段。再次按朗读快捷键可停止。";
                        _overlay.UpdateTranscript(chunks[index]);
                    }
                }
                await playback.DrainAsync(token);
            }
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
                ReadingStatus.Text = error is TimeoutException ? "读取文字超时，请复制后使用剪贴板朗读。" : "朗读失败：" + error.Message;
                // Bring actionable failures into view; successful shortcuts never steal focus.
                SettingsNavigation.SelectedItem = ReadingNavigationItem;
                ShowSettings();
            }
        }
        finally
        {
            _readingPlayback = null;
            _readingCancellation = null;
            cancellation.Dispose();
            if (!_closed && !_exiting) { _overlay.Hide(); UpdateModelControls(); }
        }
    }
}
