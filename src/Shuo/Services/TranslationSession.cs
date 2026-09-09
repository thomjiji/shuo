using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shuo.Services;

internal sealed record TranslationOptions(string Region = "cn-beijing", string WorkspaceId = "", string TargetLanguage = "zh");

internal sealed class TranslationSession(TranslationOptions options, string apiKey,
    Action ready, Action<string> transcript, Action<double> audioLevel,
    Func<string, CancellationToken, Task<string>>? format = null,
    int silenceDurationMs = 500, Action<JsonElement>? received = null)
{
    internal const string Model = "qwen3.5-livetranslate-flash-realtime";

    internal static Uri Endpoint(TranslationOptions options)
    {
        if (options.Region is not ("cn-beijing" or "ap-southeast-1"))
            throw new ArgumentException("请选择百炼服务地域。");
        if (!Regex.IsMatch(options.WorkspaceId, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,62}\z"))
            throw new ArgumentException("请填写百炼业务空间 ID（Workspace ID）。");
        if (options.TargetLanguage is not ("zh" or "en"))
            throw new ArgumentException("请选择字幕语言。");
        return new Uri($"wss://{options.WorkspaceId}.{options.Region}.maas.aliyuncs.com/api-ws/v1/realtime?model={Model}");
    }

    internal async Task RunAsync(CancellationToken stop, IAsyncEnumerable<byte[]>? audio = null, Uri? testEndpoint = null)
    {
        var endpoint = Endpoint(options);
        if (silenceDurationMs is < 200 or > 6000) throw new ArgumentOutOfRangeException(nameof(silenceDurationMs));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请填写百炼 API Key，或先在转录服务中保存百炼凭据。");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using var lifetime = new CancellationTokenSource();
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(testEndpoint ?? endpoint, connecting.Token);
            using var created = await ReceiveAsync(socket, connecting.Token);
            ThrowIfError(created.RootElement);
            if (Type(created.RootElement) != "session.created") throw new IOException("百炼未返回会话创建消息。");
            await SendAsync(socket, new
            {
                event_id = EventId(), type = "session.update",
                session = new
                {
                    modalities = new[] { "text" }, sample_rate = 16000, input_audio_format = "pcm",
                    input_audio_transcription = new { model = (string?)null },
                    translation = new { language = options.TargetLanguage },
                    turn_detection = new { type = "server_vad", threshold = 0.2, silence_duration_ms = silenceDurationMs },
                },
            }, connecting.Token);
            while (true)
            {
                using var updated = await ReceiveAsync(socket, connecting.Token);
                ThrowIfError(updated.RootElement);
                if (Type(updated.RootElement) == "session.updated") break;
            }
        }
        stop.ThrowIfCancellationRequested();
        ready();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        var sending = SendAudioAsync(socket, audio ?? SystemAudioSource.ReadAsync(audioLevel, capture.Token), capture.Token, lifetime.Token);
        var receiving = ReceiveTextAsync(socket, lifetime.Token);
        try
        {
            var first = await Task.WhenAny(sending, receiving);
            if (first == receiving)
            {
                await receiving;
                throw new IOException("百炼提前结束了翻译会话，请重新开始。");
            }
            await sending;
            // Audio has stopped; keep receiving until the service translates the final buffered speech.
            lifetime.CancelAfter(TimeSpan.FromSeconds(20));
            await SendAsync(socket, new { event_id = EventId(), type = "session.finish" }, lifetime.Token);
            await receiving;
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Finished", lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            throw new IOException("等待百炼返回最后一段译文超时，翻译连接已关闭。");
        }
        finally
        {
            lifetime.Cancel();
            capture.Cancel();
            socket.Abort();
            try { await Task.WhenAll(sending, receiving); }
            catch { /* Propagate the first failure after stopping both tasks. */ }
        }
    }

    private static async Task SendAudioAsync(ClientWebSocket socket, IAsyncEnumerable<byte[]> audio,
        CancellationToken capture, CancellationToken lifetime)
    {
        try
        {
            await foreach (var pcm in audio.WithCancellation(capture))
            {
                capture.ThrowIfCancellationRequested();
                if (pcm.Length == 0) continue;
                if (pcm.Length > 6400 || pcm.Length % 2 != 0) throw new IOException("音频数据格式无效。");
                // Finish an in-flight send before session.finish; cancelling SendAsync aborts the socket.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await SendAsync(socket, new { event_id = EventId(), type = "input_audio_buffer.append",
                    audio = Convert.ToBase64String(pcm) }, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (capture.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
    }

    private async Task ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var captions = new TranslationCaptions();
        while (true)
        {
            using var message = await ReceiveAsync(socket, cancellationToken);
            var value = message.RootElement;
            received?.Invoke(value);
            ThrowIfError(value);
            if (Type(value) == "session.finished") return;
            if (captions.Update(value) is { } text)
            {
                // Format complete confirmed snapshots in receive order. Never feed rendered text back into the model state.
                var display = format is null ? text : await format(text, cancellationToken);
                transcript(display);
            }
        }
    }

    private static string EventId() => "event_" + Guid.NewGuid().ToString("N");
    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();

    private static void ThrowIfError(JsonElement message)
    {
        if (Type(message) == "error")
        {
            var error = message.GetProperty("error");
            var code = error.TryGetProperty("code", out var value) ? value.GetString() : "unknown";
            // Do not display arbitrary service messages that could echo credentials or audio.
            throw new IOException("百炼翻译请求失败（" + code + "），请检查地域、业务空间、API Key 和模型权限。");
        }
        if (Type(message) == "response.done" && message.TryGetProperty("response", out var response)
            && response.TryGetProperty("status", out var status) && status.GetString() is "failed" or "incomplete" or "cancelled")
            throw new IOException("百炼未完成本段翻译，请重新开始。");
    }

    private static Task SendAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (length == buffer.Length) throw new IOException("百炼返回的消息过大。");
            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("百炼翻译连接已关闭或返回了无效数据。");
            length += result.Count;
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, length));
    }
}

// Display confirmed text only. Prediction (stash) is withheld until the service confirms it.
internal sealed class TranslationCaptions
{
    private readonly List<(string Id, string Text, bool Done)> _items = [];
    private string _lastCaption = "";
    internal string? Update(JsonElement message)
    {
        var type = message.GetProperty("type").GetString();
        if (type is not ("response.text.text" or "response.text.done")) return null;
        var id = message.GetProperty("item_id").GetString() ?? throw new IOException("译文缺少消息 ID。");
        var text = message.GetProperty("text").GetString() ?? "";

        if (text.Length > 1200) text = text[^1200..];
        var index = _items.FindIndex(item => item.Id == id);
        if (index >= 0 && _items[index].Done) return null;
        if (index < 0) _items.Add((id, text, type == "response.text.done"));
        else _items[index] = (id, text, type == "response.text.done");
        while (_items.Count > 20) _items.RemoveAt(0);
        var result = string.Join(" ", _items.Select(item => item.Text).Where(value => value.Length > 0));
        if (result.Length > 1200) result = result[^1200..];
        if (result == _lastCaption) return null;
        _lastCaption = result;
        return result;
    }
}

