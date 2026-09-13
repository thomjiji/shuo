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

    internal static void Validate(JsonElement root, params string[] capabilities)
    {
        if (root.GetProperty("protocol").GetInt32() != 1)
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
}
