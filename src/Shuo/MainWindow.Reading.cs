using Microsoft.UI.Xaml;
using Shuo.Services;
using System.Text.Json;

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
    private string SelectedReadingProvider => ReadingProviderPicker.SelectedIndex switch
    {
        1 => "cosyvoice",
        2 => "qwen3",
        _ => "doubao",
    };
    private bool CanStartReading => !_exiting && !_closed && !_installingUpdate && !_dictationActive
        && !_togglePending && !_modelChanging && _pendingPastes == 0 && _translationCancellation is null
        && _readingCancellation is null;

    private void InitializeReading()
    {
        _overlay.TranslationCloseRequested += StopReading;
        try
        {
            var options = ReadingSettings.Load();
            var legacyQwen = options.Provider == "cosyvoice"
                && CosyVoiceAddress.UsesPort(options.CosyVoiceUrl, 18767);
            ReadingEnabled.IsOn = options.Enabled;
            ReadingProviderPicker.SelectedIndex = legacyQwen ? 2 : options.Provider switch
            {
                "cosyvoice" => 1,
                "qwen3" => 2,
                _ => 0,
            };
            ReadingUseExistingKey.IsOn = options.UseExistingKey;
            ReadingApiKey.Password = ReadingSettings.LoadApiKey();
            ReadingSpeaker.Text = options.Speaker;
            ReadingSpeed.Value = options.SpeechRate;
            ReadingCosyVoiceUrl.Text = CosyVoiceAddress.ToDisplay(legacyQwen
                ? CosyVoiceAddress.WithPort(options.CosyVoiceUrl, 18766)
                : options.CosyVoiceUrl);
            ReadingCosyVoiceVoice.Text = options.CosyVoiceVoice;
            var qwen3Url = string.IsNullOrWhiteSpace(options.Qwen3Url) && !string.IsNullOrWhiteSpace(options.CosyVoiceUrl)
                ? CosyVoiceAddress.WithPort(options.CosyVoiceUrl, 18767)
                : options.Qwen3Url;
            ReadingQwen3Url.Text = CosyVoiceAddress.ToDisplay(qwen3Url, 18767);
            ReadingQwen3Voice.Text = string.IsNullOrWhiteSpace(options.Qwen3Voice)
                ? options.CosyVoiceVoice
                : options.Qwen3Voice;
            _readingHotkeyBinding = options.Hotkey;
            ReadingShortcutButton.Content = _readingHotkeyBinding.DisplayText;
            RegisterReadingHotkeys();
        }
        catch (Exception error) { ReadingStatus.Text = "无法初始化朗读：" + error.Message; }
        _readingLoaded = true;
        UpdateReadingProviderFields();
        UpdateReadingControls();
    }

    private ReadingOptions CurrentReadingOptions() => new(
        Enabled: ReadingEnabled.IsOn,
        Provider: SelectedReadingProvider,
        UseExistingKey: ReadingUseExistingKey.IsOn,
        Speaker: ReadingSpeaker.Text.Trim(),
        SpeechRate: (int)ReadingSpeed.Value,
        CosyVoiceUrl: CosyVoiceAddress.ToUrl(ReadingCosyVoiceUrl.Text),
        CosyVoiceVoice: ReadingCosyVoiceVoice.Text.Trim(),
        Qwen3Url: CosyVoiceAddress.ToUrl(ReadingQwen3Url.Text, 18767),
        Qwen3Voice: ReadingQwen3Voice.Text.Trim(),
        HotkeyModifiers: _readingHotkeyBinding.Modifiers,
        HotkeyVirtualKey: _readingHotkeyBinding.VirtualKey);

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

    private void ReadingProvider_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs args)
    {
        if (!_readingLoaded) return;
        UpdateReadingProviderFields();
        UpdateReadingControls();
    }

    private void UpdateReadingProviderFields()
    {
        if (ReadingProviderPicker is null) return;
        var cosyVoice = ReadingProviderPicker.SelectedIndex == 1;
        var qwen3 = ReadingProviderPicker.SelectedIndex == 2;
        var doubao = cosyVoice || qwen3 ? Visibility.Collapsed : Visibility.Visible;
        var cosy = cosyVoice ? Visibility.Visible : Visibility.Collapsed;
        var qwen = qwen3 ? Visibility.Visible : Visibility.Collapsed;
        ReadingUseExistingKey.Visibility = doubao;
        ReadingApiKey.Visibility = doubao;
        ReadingDoubaoTitle.Visibility = doubao;
        ReadingSpeaker.Visibility = doubao;
        ReadingDoubaoHint.Visibility = doubao;
        ReadingDoubaoLink.Visibility = doubao;
        ReadingSpeed.Visibility = doubao;
        ReadingCosyVoiceTitle.Visibility = cosy;
        ReadingCosyVoiceUrl.Visibility = cosy;
        ReadingCosyVoiceVoice.Visibility = cosy;
        ReadingCosyVoiceHint.Visibility = cosy;
        ReadingCosyVoiceTest.Visibility = cosy;
        ReadingQwen3Title.Visibility = qwen;
        ReadingQwen3Url.Visibility = qwen;
        ReadingQwen3Voice.Visibility = qwen;
        ReadingQwen3Hint.Visibility = qwen;
        ReadingQwen3Test.Visibility = qwen;
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
        ReadingApiKey.IsEnabled = !active && ReadingProviderPicker.SelectedIndex == 0 && !ReadingUseExistingKey.IsOn;
    }

    private async void ReadingCosyVoiceTest_Click(object sender, RoutedEventArgs args) =>
        await TestSelfHostedReadingAsync("cosyvoice", "CosyVoice");

    private async void ReadingQwen3Test_Click(object sender, RoutedEventArgs args) =>
        await TestSelfHostedReadingAsync("qwen3", "Qwen3-TTS");

    private async Task TestSelfHostedReadingAsync(string expectedEngine, string serviceName)
    {
        if (_readingCancellation is not null) return;
        try
        {
            var options = CurrentReadingOptions();
            options.Validate();
            ReadingStatus.Text = $"正在测试 Mac 上的 {serviceName} 服务...";
            using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(10),
            };
            using var response = await client.GetAsync(
                CosyVoiceAddress.Health(options.SelfHostedUrl, options.SelfHostedPort));
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"服务返回 HTTP {(int)response.StatusCode}。");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var root = document.RootElement;
            if (!root.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
                throw new IOException("服务尚未准备好。");
            if (root.TryGetProperty("engine", out var engine))
            {
                if (engine.GetString() != expectedEngine)
                    throw new IOException($"当前地址运行的不是 {serviceName} 服务。");
            }
            else if (expectedEngine == "qwen3")
                throw new IOException("当前地址没有返回 Qwen3-TTS 引擎标识。");
            var found = root.GetProperty("voices").EnumerateArray()
                .Any(voice => voice.GetProperty("id").GetString() == options.SelfHostedVoice);
            if (!found) throw new IOException("服务已连接，但没有所选音色。");
            ReadingStatus.Text = $"{serviceName} 服务连接正常，音色可用。";
        }
        catch (Exception error) { ReadingStatus.Text = $"{serviceName} 测试失败：" + error.Message; }
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
            var key = options.Provider == "doubao"
                ? options.UseExistingKey ? _cloudOptions.ApiKey : ReadingApiKey.Password.Trim()
                : "";
            if (options.Provider == "doubao" && string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("请填写语音 API Key，或在转录服务中配置火山引擎语音 API Key。");
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
            var chunks = ReadingText.Split(text, options.IsSelfHosted ? 240 : ReadingText.MaximumRequestBytes);
            ReadingSettings.Save(options, ReadingApiKey.Password);
            ReadingStatus.Text = options.IsSelfHosted
                ? $"正在连接 Mac 上的 {options.ServiceName}..."
                : "正在连接豆包语音合成...";
            _overlay.Begin(true);
            using var client = options.IsSelfHosted
                ? new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false })
                : new System.Net.Http.HttpClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var doubao = options.Provider == "doubao" ? new DoubaoSpeechClient(client) : null;
            var selfHosted = options.IsSelfHosted ? new CosyVoiceSpeechClient(client) : null;
            IAsyncEnumerable<byte[]> Synthesize(string chunk) => options.IsSelfHosted
                ? selfHosted!.SynthesizeAsync(chunk, options, token)
                : doubao!.SynthesizeAsync(chunk, options, key, token);
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
                    await foreach (var audio in Synthesize(chunk))
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
