using System.Net.Http;
using System.Text.Json;

namespace Shuo.Services;

internal static class LocalServiceHealth
{
    internal static async Task TestAsync(string host, CancellationToken token, params string[] capabilities)
    {
        var endpoint = LocalServiceEndpoint.Create(host, 18766, "/health");
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Validate(body.RootElement, capabilities);
    }

    internal static async Task TestSpeechModelAsync(string host, string speechModel, CancellationToken token)
    {
        var endpoint = LocalServiceEndpoint.Create(host, 18766, "/health");
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        ValidateSpeechModel(body.RootElement, speechModel);
    }

    internal static async Task TestTranslationModelAsync(string host, CancellationToken token)
    {
        var endpoint = LocalServiceEndpoint.Create(host, 18766, "/health");
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http" }.Uri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        ValidateTranslationModel(body.RootElement);
    }

    internal static void Validate(JsonElement root, params string[] capabilities)
    {
        if (root.GetProperty("protocol").GetInt32() is not (1 or 2))
            throw new IOException("Mac 服务协议不兼容，请更新服务。");
        foreach (var name in capabilities)
        {
            var ready = root.TryGetProperty("capabilities", out var states)
                ? states.TryGetProperty(name, out var state) && state.GetProperty("ready").GetBoolean()
                : root.GetProperty("ready").GetBoolean();
            if (!ready) throw new IOException(name == "translation" ? "Mac 文字翻译模型尚未准备好。" : "Mac 语音合成模型尚未准备好。");
        }
        if (capabilities.Contains("speech") && (root.GetProperty("sample_rate").GetInt32() != 24000
            || root.GetProperty("format").GetString() != "pcm_s16le" || root.GetProperty("voice").GetString() != "Serena"))
            throw new IOException("Mac 语音格式或音色不兼容，请更新服务。");
    }

    internal static void ValidateSpeechModel(JsonElement root, string speechModel)
    {
        if (!SelfHostedSpeechModels.IsSupported(speechModel)) throw new IOException("设置中的语音合成模型不受支持。");
        Validate(root, "speech");
        if (speechModel == SelfHostedSpeechModels.Default && root.GetProperty("protocol").GetInt32() == 1) return;
        if (!root.TryGetProperty("speech_models", out var models) || models.ValueKind != JsonValueKind.Array
            || !models.EnumerateArray().Any(model => model.GetString() == speechModel))
            throw new IOException("Mac 服务不支持所选语音合成模型，请更新服务或改用 0.6B。");
    }

    internal static void ValidateTranslationModel(JsonElement root)
    {
        Validate(root, "translation");
        if (root.TryGetProperty("translation_model", out var model)
            && model.GetString() != SelfHostedTranslationModels.Default)
            throw new IOException("Mac 服务使用的文字翻译模型与当前版本不兼容，请更新服务。");
    }
}
