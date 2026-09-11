using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace Shuo.Services;

internal sealed class CosyVoiceSpeechClient(HttpClient client)
{
    private const long MaximumAudioBytes = 48L * 1024 * 1024;

    internal async IAsyncEnumerable<byte[]> SynthesizeAsync(string text, ReadingOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options.Validate();
        using var request = new HttpRequestMessage(HttpMethod.Post, CosyVoiceAddress.Endpoint(options.CosyVoiceUrl))
        {
            Content = JsonContent.Create(new { protocol = 1, text, voice = options.CosyVoiceVoice }),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "Mac 上没有所选音色，请检查音色 ID。",
                HttpStatusCode.Conflict => "Mac 正在合成另一段语音，请稍后重试。",
                HttpStatusCode.BadRequest => "CosyVoice 服务拒绝了当前设置，请检查地址和音色。",
                HttpStatusCode.GatewayTimeout => "Mac 语音合成超时，请缩短文字后重试。",
                _ => $"CosyVoice 服务返回 HTTP {(int)response.StatusCode}，请检查 Mac 服务日志。",
            };
            throw new HttpRequestException(message);
        }
        if (!response.Headers.TryGetValues("X-Shuo-Protocol", out var protocols) || protocols.SingleOrDefault() != "1"
            || !response.Headers.TryGetValues("X-Shuo-Audio-Format", out var formats) || formats.SingleOrDefault() != "pcm_s16le"
            || !response.Headers.TryGetValues("X-Shuo-Sample-Rate", out var rates) || rates.SingleOrDefault() != "24000")
            throw new IOException("CosyVoice 服务返回了不兼容的音频格式。请更新 Mac 服务。");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var total = 0L;
        while (true)
        {
            var buffer = new byte[8192];
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            total += count;
            if (total > MaximumAudioBytes) throw new IOException("CosyVoice 返回的音频过长。");
            if (count != buffer.Length) Array.Resize(ref buffer, count);
            yield return buffer;
        }
        if (total == 0) throw new IOException("CosyVoice 服务没有返回音频。");
        if ((total & 1) != 0) throw new IOException("CosyVoice 返回的 PCM 音频格式不完整。");
    }
}
