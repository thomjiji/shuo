using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shuo.Services;
using Windows.UI;

namespace Shuo;

public sealed partial class MainWindow
{
    private bool _servicesLoaded;
    private bool _savingServices;
    private bool _testingServices;

    private void InitializeServiceConnections()
    {
        var hosts = new MacServiceAddresses(SelfHostedUrl.Text, TranslationHost.Text, ReadingLocalHost.Text);
        SharedMacHost.Text = hosts.Separate ? "" : hosts.SharedHost;
        SeparateMacHosts.IsOn = hosts.Separate;
        _servicesLoaded = true;
        UpdateServiceControls();
    }

    private MacServiceAddresses EditedMacAddresses()
    {
        var hosts = SeparateMacHosts.IsOn
            ? new MacServiceAddresses(SelfHostedAddress.ToUrl(SelfHostedUrl.Text), TranslationHost.Text.Trim(), ReadingLocalHost.Text.Trim())
            : MacServiceAddresses.Shared(SharedMacHost.Text);
        hosts.Validate();
        return hosts;
    }

    private void SeparateMacHosts_Toggled(object sender, RoutedEventArgs args)
    {
        if (_servicesLoaded) ResetMacServiceStatusIcons();
        UpdateServiceControls();
    }
    private void MacServiceAddress_Changed(object sender, TextChangedEventArgs args)
    {
        if (_servicesLoaded) ResetMacServiceStatusIcons();
    }
    private void MacServiceSelection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_servicesLoaded) ResetMacServiceStatusIcons();
    }
    private void ReadingCredential_Changed(object sender, RoutedEventArgs args) => UpdateServiceControls();

    private void UpdateServiceControls()
    {
        if (!_servicesLoaded) return;
        var idle = !_savingServices && !_testingServices && !_installingUpdate && !_dictationActive
            && !_togglePending && !_modelChanging && _readingCancellation is null && _translationCancellation is null && _pendingPastes == 0;
        foreach (var field in new Control[] { SharedMacHost, SeparateMacHosts, SelfHostedUrl, TranslationHost, ReadingLocalHost,
            SelfHostedSpeechModelPicker, SelfHostedSpeechPromptInput, CaptionSelfHostedModelPicker, MacTestButton, CloudApiKey, DoubaoModelPicker,
            QwenApiKey, QwenModelPicker, SelfHostedModelPicker, TranslationWorkspace, TranslationApiKey,
            ReadingUseExistingKey, ReadingApiKey, ServicesSaveButton }) field.IsEnabled = idle;
        MacHostOverrides.Visibility = SeparateMacHosts.IsOn ? Visibility.Visible : Visibility.Collapsed;
        SharedMacHostRow.Visibility = SeparateMacHosts.IsOn ? Visibility.Collapsed : Visibility.Visible;
        ReadingApiKeyCard.Visibility = ReadingUseExistingKey.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ServicesSave_Click(object sender, RoutedEventArgs args)
    {
        if (!_servicesLoaded || _savingServices || _testingServices) return;
        _savingServices = true;
        UpdateModelControls();
        try
        {
            var hosts = EditedMacAddresses();
            var cloud = ReadCloudOptions() with { SelfHostedUrl = hosts.Recognition };
            var reading = ReadingSettings.Load() with { SelfHostedHost = hosts.Reading, UseExistingKey = ReadingUseExistingKey.IsOn,
                SelfHostedSpeechModel = SelectedSelfHostedSpeechModel(),
                SelfHostedSpeechPrompt = SelfHostedSpeechModels.ValidatePrompt(SelfHostedSpeechPromptInput.Text) };
            var captions = TranslationSettings.Load() with { Host = hosts.Captions, WorkspaceId = TranslationWorkspace.Text.Trim(),
                SelfHostedAsrModel = SelectedCaptionAsrModel() };
            CloudSettings.Save(cloud);
            ReadingSettings.Save(reading, ReadingApiKey.Password);
            TranslationSettings.Save(captions, TranslationApiKey.Password);
            _cloudOptions = cloud;
            SelfHostedUrl.Text = SelfHostedAddress.ToDisplay(hosts.Recognition);
            TranslationHost.Text = hosts.Captions;
            ReadingLocalHost.Text = hosts.Reading;
            ResetMacServiceStatusIcons();
            ServicesStatus.Text = "服务设置已保存。";
            if (_daemonReady)
            {
                _modelChanging = true;
                _backendConfigured = false;
                await ConfigureBackendAsync();
            }
        }
        catch (Exception error)
        {
            _modelChanging = false;
            ServicesStatus.Text = "未能完成保存：" + error.Message;
        }
        finally
        {
            _savingServices = false;
            if (!_closed) UpdateModelControls();
        }
    }

    private async void MacTest_Click(object sender, RoutedEventArgs args)
    {
        if (_testingServices || _savingServices) return;
        _testingServices = true;
        ResetMacServiceStatusIcons();
        MacTestButton.Content = "正在测试...";
        UpdateServiceControls();
        try
        {
            var hosts = EditedMacAddresses();
            var inputModel = ReadCloudOptions().SelfHostedModel;
            var captionModel = SelectedCaptionAsrModel();
            var speechModel = SelectedSelfHostedSpeechModel();
            var inputTask = CheckMacServiceAsync($"语音输入识别可用：{inputModel}",
                () => TestRecognitionHealthAsync(hosts.Recognition, inputModel, _shutdown.Token));
            var captionTask = CheckMacServiceAsync($"实时字幕识别可用：{captionModel}",
                () => TestRecognitionHealthAsync(hosts.Captions, captionModel, _shutdown.Token));
            var captionTranslationTask = CheckMacServiceAsync("实时字幕翻译可用。",
                () => LocalServiceHealth.TestTranslationModelAsync(hosts.Captions, _shutdown.Token));
            var readingTranslationTask = CheckMacServiceAsync("译读翻译可用。",
                () => LocalServiceHealth.TestTranslationModelAsync(hosts.Reading, _shutdown.Token));
            var speechVoice = SelectedSelfHostedSpeechVoice();
            var speechTask = CheckMacServiceAsync($"语音合成、音色与自定义提示词可用：{speechModel} / {speechVoice}",
                () => LocalServiceHealth.TestSpeechModelAsync(hosts.Reading, speechModel, _shutdown.Token,
                    requirePrompt: true, speechVoice: speechVoice));
            await Task.WhenAll(inputTask, captionTask, captionTranslationTask, readingTranslationTask, speechTask);
            if (_closed) return;
            var input = await inputTask;
            var caption = await captionTask;
            var captionTranslation = await captionTranslationTask;
            var readingTranslation = await readingTranslationTask;
            var speech = await speechTask;
            SetMacServiceStatus(MacInputStatusIcon, input);
            SetMacServiceStatus(MacCaptionStatusIcon, caption);
            var translation = new MacServiceCheck(captionTranslation.Passed && readingTranslation.Passed,
                $"{captionTranslation.Detail}{Environment.NewLine}{readingTranslation.Detail}");
            SetMacServiceStatus(MacTranslationStatusIcon, translation);
            SetMacServiceStatus(MacSpeechStatusIcon, speech);
            var failures = new[] { input, caption, translation, speech }.Where(result => !result.Passed).Select(result => result.Detail).ToArray();
            if (failures.Length > 0) ShowError("部分自托管能力不可用", string.Join(Environment.NewLine, failures));
        }
        catch (Exception error) { if (!_closed) ShowError("连接测试失败", error.Message); }
        finally
        {
            _testingServices = false;
            if (!_closed)
            {
                MacTestButton.Content = "测试连接与可用能力";
                UpdateServiceControls();
            }
        }
    }

    private static async Task<MacServiceCheck> CheckMacServiceAsync(string success, Func<Task> check)
    {
        try
        {
            await check();
            return new(true, success);
        }
        catch (Exception error) { return new(false, error.Message); }
    }

    private void ResetMacServiceStatusIcons()
    {
        if (MacInputStatusIcon is null) return;
        foreach (var icon in new[] { MacInputStatusIcon, MacCaptionStatusIcon, MacTranslationStatusIcon, MacSpeechStatusIcon })
        {
            icon.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(icon, null);
        }
    }

    private static void SetMacServiceStatus(FontIcon icon, MacServiceCheck result)
    {
        icon.Glyph = result.Passed ? "\uE73E" : "\uE783";
        icon.Foreground = new SolidColorBrush(result.Passed
            ? Color.FromArgb(255, 76, 175, 80) : Color.FromArgb(255, 232, 17, 35));
        icon.Visibility = Visibility.Visible;
        ToolTipService.SetToolTip(icon, result.Detail);
    }

    private string SelectedCaptionAsrModel() => CaptionSelfHostedModelPicker.SelectedIndex == 1
        ? SelfHostedAsrModels.Small : SelfHostedAsrModels.Large;

    private static async Task TestRecognitionHealthAsync(string host, string model, CancellationToken token)
    {
        var endpoint = LocalServiceEndpoint.Create(host, 18765, "/health");
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = body.RootElement;
        if (root.GetProperty("protocol").GetInt32() != 1 || !root.GetProperty("ready").GetBoolean())
            throw new IOException("Mac 语音识别尚未就绪或协议不兼容。");
        var available = root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
            ? models.EnumerateArray().Any(item => item.GetString() == model)
            : root.TryGetProperty("model", out var selected) && selected.GetString() == model;
        if (!available) throw new IOException($"Mac 语音识别服务不支持所选模型：{model}。");
    }

    private sealed record MacServiceCheck(bool Passed, string Detail);
}
