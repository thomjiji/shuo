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
            MacTestButton, CloudApiKey, DoubaoModelPicker,
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
            var reading = ReadingSettings.Load() with { SelfHostedHost = hosts.Reading, UseExistingKey = ReadingUseExistingKey.IsOn };
            var captions = TranslationSettings.Load() with { Host = hosts.Captions, WorkspaceId = TranslationWorkspace.Text.Trim() };
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
            var checks = new[]
            {
                (Name: "语音输入识别", Host: hosts.Recognition, Capability: "asr"),
                (Name: "字幕识别", Host: hosts.Captions, Capability: "asr"),
                (Name: "字幕翻译", Host: hosts.Captions, Capability: "translation"),
                (Name: "译读翻译", Host: hosts.Reading, Capability: "translation"),
                (Name: "语音合成", Host: hosts.Reading, Capability: "speech"),
            };
            var results = await Task.WhenAll(checks.Select(async check =>
            {
                try
                {
                    if (check.Capability == "asr") await TestRecognitionHealthAsync(check.Host, _shutdown.Token);
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

    private static async Task TestRecognitionHealthAsync(string host, CancellationToken token)
    {
        var endpoint = LocalServiceEndpoint.Create(host, 18765, "/health");
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (body.RootElement.GetProperty("protocol").GetInt32() != 1 || !body.RootElement.GetProperty("ready").GetBoolean())
            throw new IOException("Mac 语音识别尚未就绪或协议不兼容。");
    }
}
