using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Shuo.Services;

internal static class SelfHostedReadingClient
{
    internal static Uri Endpoint(string host) => LocalServiceEndpoint.Create(host, 18766, "/v1/reading");

    internal static async Task TestAsync(string host, string speechModel, string speechVoice, CancellationToken token)
    {
        await LocalServiceHealth.TestAsync(host, token, "translation", "speech");
        await LocalServiceHealth.TestSpeechModelAsync(host, speechModel, token,
            requirePrompt: true, speechVoice: speechVoice);
    }

    internal static async IAsyncEnumerable<byte[]> ReadAsync(string text, Uri endpoint, double speed,
        string speechModel, string speechPrompt, string speechVoice, Action<string> translated,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > ReadingText.LocalRequestBytes)
            throw new ArgumentException("本段文字为空或过长。");
        if (speed is not (0.85 or 1.0 or 1.15 or 1.3)) throw new ArgumentException("无效的播放速度。");
        if (!SelfHostedSpeechModels.IsSupported(speechModel)) throw new ArgumentException("不支持此自托管语音合成模型。");
        if (!SelfHostedSpeechVoices.IsSupported(speechVoice)) throw new ArgumentException("不支持此自托管朗读音色。");
        speechPrompt = SelfHostedSpeechModels.ValidatePrompt(speechPrompt);
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(endpoint, connecting.Token);
        }
        var request = CreateStartRequest(text, speed, speechModel, speechPrompt, speechVoice);
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, token);
        await foreach (var audio in ReadEventsAsync(socket, translated, token, speechModel,
            requirePrompt: speechPrompt.Length > 0, speechVoice: speechVoice)) yield return audio;
    }

    internal static byte[] CreateStartRequest(string text, double speed, string speechModel, string speechPrompt,
        string speechVoice)
    {
        if (!SelfHostedSpeechModels.IsSupported(speechModel)) throw new ArgumentException("不支持此自托管语音合成模型。");
        if (!SelfHostedSpeechVoices.IsSupported(speechVoice)) throw new ArgumentException("不支持此自托管朗读音色。");
        speechPrompt = SelfHostedSpeechModels.ValidatePrompt(speechPrompt);
        string? instruction = speechPrompt.Length == 0 ? null : speechPrompt;
        return JsonSerializer.SerializeToUtf8Bytes(new { type = "start", protocol = 2, text, speed,
            speech_model = speechModel, speech_instruct = instruction, speech_voice = speechVoice });
    }

    internal static async IAsyncEnumerable<byte[]> ReadEventsAsync(WebSocket socket, Action<string> translated,
        [EnumeratorCancellation] CancellationToken token, string speechModel = SelfHostedSpeechModels.Default,
        bool requirePrompt = false, string speechVoice = SelfHostedSpeechVoices.Default)
    {
        var buffer = new byte[16384];
        var ready = false;
        long audioBytes = 0;
        var textLength = 0;
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            WebSocketMessageType? type = null;
            // Time spent accepting audio into a paused player does not consume this timeout.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Mac 译读连接提前结束，本段未完成。");
                    type ??= part.MessageType;
                    if (part.MessageType != type) throw new IOException("Mac 译读消息格式无效。");
                    message.Write(buffer, 0, part.Count);
                    if (message.Length > 65536) throw new IOException("Mac 译读消息过大。");
                } while (!part.EndOfMessage);
            }
            token.ThrowIfCancellationRequested();
            if (type == WebSocketMessageType.Binary)
            {
                if (!ready || message.Length == 0 || (message.Length & 1) != 0)
                    throw new IOException("Mac 译读音频格式无效。");
                audioBytes += message.Length;
                if (audioBytes > 24000 * 2 * 240) throw new IOException("本段音频过长，请缩短选文。");
                yield return message.ToArray();
                await socket.SendAsync("{\"type\":\"ack\"}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, token);
                continue;
            }
            using var document = JsonDocument.Parse(message.ToArray());
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    if (ready) throw new IOException("Mac 重复发送译读就绪消息。");
                    ValidateFormat(root, speechModel, requirePrompt, speechVoice);
                    ready = true;
                    break;
                case "text" when ready:
                    var text = root.GetProperty("text").GetString() ?? "";
                    textLength += text.Length;
                    if (textLength > 3000) throw new IOException("Mac 返回的译文过长。");
                    translated(text);
                    break;
                case "done" when ready:
                    if (audioBytes == 0 || textLength == 0) throw new IOException("Mac 未返回完整的译文和音频。");
                    yield break;
                case "error":
                    var error = root.GetProperty("message").GetString() ?? "Mac 译读失败。";
                    if (error.Contains("协议不兼容", StringComparison.Ordinal))
                        throw new IOException("Mac 服务不支持所选语音合成模型、音色或自定义朗读提示词，请更新服务。");
                    throw new IOException(error);
                default:
                    throw new IOException("Mac 译读协议不兼容，请更新服务。");
            }
        }
    }

    private static void ValidateFormat(JsonElement root, string speechModel, bool requirePrompt, string speechVoice)
    {
        if (!SelfHostedSpeechVoices.IsSupported(speechVoice)) throw new IOException("设置中的自托管朗读音色不受支持。");
        var protocol = root.GetProperty("protocol").GetInt32();
        if (protocol is not (1 or 2) || root.GetProperty("sample_rate").GetInt32() != 24000
            || root.GetProperty("format").GetString() != "pcm_s16le" || root.GetProperty("voice").GetString() != speechVoice)
            throw new IOException("Mac 译读协议或音色不兼容，请更新服务。");
        if ((speechModel != SelfHostedSpeechModels.Default || requirePrompt || speechVoice != SelfHostedSpeechVoices.Default)
            && (protocol < 2 || !root.TryGetProperty("speech_model", out var selected)
                || selected.GetString() != speechModel
                || speechVoice != SelfHostedSpeechVoices.Default
                    && (!root.TryGetProperty("speech_voice", out var selectedVoice)
                        || selectedVoice.GetString() != speechVoice)
                || requirePrompt && (!root.TryGetProperty("speech_instruct", out var prompt)
                    || prompt.ValueKind != JsonValueKind.True)))
            throw new IOException("Mac 服务不支持所选语音合成模型、音色或自定义朗读提示词，请更新服务。");
    }
}
