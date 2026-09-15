using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shuo.Services;

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

    private void SeparateMacHosts_Toggled(object sender, RoutedEventArgs args) => UpdateServiceControls();
    private void ReadingCredential_Changed(object sender, RoutedEventArgs args) => UpdateServiceControls();

    private void UpdateServiceControls()
    {
        if (!_servicesLoaded) return;
        var idle = !_savingServices && !_testingServices && !_installingUpdate && !_dictationActive
            && !_togglePending && !_modelChanging && _readingCancellation is null && _translationCancellation is null && _pendingPastes == 0;
        foreach (var field in new Control[] { SharedMacHost, SeparateMacHosts, SelfHostedUrl, TranslationHost, ReadingLocalHost,
            SelfHostedSpeechModelPicker, CaptionSelfHostedModelPicker, MacTestButton, CloudApiKey, DoubaoModelPicker,
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
                SelfHostedSpeechModel = SelectedSelfHostedSpeechModel() };
            var captions = TranslationSettings.Load() with { Host = hosts.Captions, WorkspaceId = TranslationWorkspace.Text.Trim(),
                SelfHostedAsrModel = SelectedCaptionAsrModel() };
            CloudSettings.Save(cloud);
            ReadingSettings.Save(reading, ReadingApiKey.Password);
            TranslationSettings.Save(captions, TranslationApiKey.Password);
            _cloudOptions = cloud;
            SelfHostedUrl.Text = SelfHostedAddress.ToDisplay(hosts.Recognition);
            TranslationHost.Text = hosts.Captions;
            ReadingLocalHost.Text = hosts.Reading;
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
        UpdateServiceControls();
        MacServiceStatus.Text = "正在检查语音识别、文字翻译和语音合成...";
        try
        {
            var hosts = EditedMacAddresses();
            var inputModel = ReadCloudOptions().SelfHostedModel;
            var captionModel = SelectedCaptionAsrModel();
            var speechModel = SelectedSelfHostedSpeechModel();
            var checks = new[]
            {
                (Name: $"语音输入识别（{inputModel}）", Host: hosts.Recognition, Capability: "asr", Model: inputModel),
                (Name: $"实时字幕识别（{captionModel}）", Host: hosts.Captions, Capability: "asr", Model: captionModel),
                (Name: $"实时字幕翻译（{SelfHostedTranslationModels.Default}）", Host: hosts.Captions, Capability: "translation-model", Model: SelfHostedTranslationModels.Default),
                (Name: $"译读翻译（{SelfHostedTranslationModels.Default}）", Host: hosts.Reading, Capability: "translation-model", Model: SelfHostedTranslationModels.Default),
                (Name: $"语音合成（{speechModel}）", Host: hosts.Reading, Capability: "speech-model", Model: speechModel),
            };
            var results = await Task.WhenAll(checks.Select(async check =>
            {
                try
                {
                    if (check.Capability == "asr") await TestRecognitionHealthAsync(check.Host, check.Model, _shutdown.Token);
                    else if (check.Capability == "speech-model")
                        await LocalServiceHealth.TestSpeechModelAsync(check.Host, check.Model, _shutdown.Token);
                    else if (check.Capability == "translation-model")
                        await LocalServiceHealth.TestTranslationModelAsync(check.Host, _shutdown.Token);
                    else await LocalServiceHealth.TestAsync(check.Host, _shutdown.Token, check.Capability);
                    return $"{check.Name}：ok";
                }
                catch (Exception error) { return $"{check.Name}：fail，{error.Message}"; }
            }));
            if (!_closed) MacServiceStatus.Text = string.Join(Environment.NewLine, results);
        }
        catch (Exception error) { if (!_closed) MacServiceStatus.Text = "连接测试失败：" + error.Message; }
        finally { _testingServices = false; if (!_closed) UpdateServiceControls(); }
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
}
