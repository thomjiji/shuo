using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Shuo.Services;

internal sealed class DoubaoSpeechClient(HttpClient client)
{
    internal static readonly Uri Endpoint = new("https://openspeech.bytedance.com/api/v3/tts/unidirectional/sse");

    internal async IAsyncEnumerable<byte[]> SynthesizeAsync(string text, ReadingOptions options, string apiKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请配置火山引擎语音 API Key。");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Add("X-Api-Resource-Id", options.ResourceId);
        request.Headers.Add("X-Api-Request-Id", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(new
        {
            user = new { uid = "shuo" },
            req_params = new
            {
                text, speaker = options.Speaker,
                audio_params = new { format = "pcm", sample_rate = 24000, speech_rate = options.SpeechRate },
                additions = JsonSerializer.Serialize(new { disable_markdown_filter = false }),
            },
        });
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectTimeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"语音服务返回 HTTP {(int)response.StatusCode}，请检查 API Key、服务开通和额度。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var bytes in ReadEventsAsync(stream, cancellationToken)) yield return bytes;
    }

    internal static async IAsyncEnumerable<byte[]> ReadEventsAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var payload = new StringBuilder();
        var receivedAudio = false;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is not null && line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (payload.Length > 0) payload.Append('\n');
                    payload.Append(line.AsSpan(5).TrimStart());
                    if (payload.Length > 4 * 1024 * 1024) throw new IOException("语音响应过大。");
                }
                continue;
            }
            if (payload.Length > 0)
            {
                using var message = JsonDocument.Parse(payload.ToString());
                payload.Clear();
                var root = message.RootElement;
                var code = root.TryGetProperty("code", out var value) ? value.GetInt32() : 0;
                if (code is not (0 or 20000000))
                    // Do not surface raw service messages, which may echo submitted text.
                    throw new IOException($"语音服务错误 {code}，请检查音色对应的服务、权限及额度。");
                if (root.TryGetProperty("data", out var audio) && audio.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(audio.GetString()))
                {
                    var bytes = Convert.FromBase64String(audio.GetString()!);
                    if (bytes.Length > 0) { receivedAudio = true; yield return bytes; }
                }
                if (code == 20000000)
                {
                    if (!receivedAudio) throw new IOException("语音服务没有返回音频。");
                    yield break;
                }
            }
            if (line is null) throw new IOException("语音连接提前结束，朗读未完成。");
        }
    }
}
