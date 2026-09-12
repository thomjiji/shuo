using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Shuo.Services;

internal sealed class SelfHostedTranslationSession(TranslationOptions options, Action ready,
    Action<string> transcript, Func<string, CancellationToken, Task<string>>? format = null)
{
    internal static Uri Endpoint(string host) => new UriBuilder(SelfHostedReadingClient.Endpoint(host))
        { Path = "/v1/translation" }.Uri;

    internal async Task RunAsync(IAsyncEnumerable<byte[]> audio, CancellationToken stop, Uri? asrEndpoint = null,
        Uri? translationEndpoint = null)
    {
        var endpoint = translationEndpoint ?? Endpoint(options.Host);
        var asr = asrEndpoint ?? new UriBuilder(endpoint) { Port = 18765, Path = "/v1/asr" }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        using var lifetime = new CancellationTokenSource();
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(asr, connecting.Token);
            await SendAsync(socket, new { type = "start", protocol = 1, sample_rate = 16000,
                format = "pcm_s16le", language = "auto" }, connecting.Token);
            using var message = await ReceiveAsync(socket, connecting.Token);
            if (message.RootElement.GetProperty("type").GetString() != "ready")
                throw new IOException("Mac 语音识别尚未就绪或正被占用，请稍后重试。");
        }
        ready();
        var snapshots = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        var sending = SendAudioAsync();
        var receiving = ReceiveTextAsync();
        var translating = TranslateSnapshotsAsync();
        try
        {
            var first = await Task.WhenAny(sending, receiving, translating);
            await first;
            if (first != sending) throw new IOException("Mac 提前结束了翻译会话，请重新开始。");
            lifetime.CancelAfter(TimeSpan.FromSeconds(60));
            await SendAsync(socket, new { type = "finish" }, lifetime.Token);
            await Task.WhenAll(receiving, translating);
        }
        finally
        {
            lifetime.Cancel();
            capture.Cancel();
            socket.Abort();
            snapshots.Writer.TryComplete();
            try { await Task.WhenAll(sending, receiving, translating); } catch { }
        }

        async Task SendAudioAsync()
        {
            try
            {
                await foreach (var pcm in audio.WithCancellation(capture.Token))
                {
                    capture.Token.ThrowIfCancellationRequested();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(pcm.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (capture.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        }

        async Task ReceiveTextAsync()
        {
            try
            {
                while (true)
                {
                    using var message = await ReceiveAsync(socket, lifetime.Token);
                    var root = message.RootElement;
                    var type = root.GetProperty("type").GetString();
                    if (type is not ("partial" or "final")) throw new IOException("Mac 语音识别失败，请检查服务。");
                    var source = root.GetProperty("text").GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(source)) snapshots.Writer.TryWrite(RecentPassage(source));
                    if (type == "final") return;
                }
            }
            finally { snapshots.Writer.TryComplete(); }
        }

        async Task TranslateSnapshotsAsync()
        {
            string? previous = null;
            await foreach (var source in snapshots.Reader.ReadAllAsync(lifetime.Token))
            {
                if (source == previous) continue;
                var text = await TranslateAsync(source, options.TargetLanguage, endpoint, lifetime.Token);
                transcript(format is null ? text : await format(text, lifetime.Token));
                previous = source;
            }
        }
    }

    // ASR snapshots revise the current phrase. Translate only a bounded recent window and
    // replace the caption; never append a revised translation to the earlier prediction.
    internal static string RecentPassage(string source)
    {
        var start = source.Length;
        var bytes = 0;
        while (start > 0)
        {
            var count = start > 1 && char.IsSurrogatePair(source, start - 2) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(source.AsSpan(start - count, count));
            if (bytes + size > 900) break;
            bytes += size;
            start -= count;
        }
        if (start > 0)
        {
            var boundary = source.IndexOfAny([' ', '\n', '。', '！', '？', '.', '!', '?'], start);
            if (boundary >= start && boundary < source.Length - 1 && boundary - start < 100) start = boundary + 1;
        }
        return source[start..].Trim();
    }

    internal static async Task<string> TranslateAsync(string source, string target, Uri endpoint, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        await socket.ConnectAsync(endpoint, timeout.Token);
        await SendAsync(socket, new { type = "start", protocol = 1, text = source, target }, timeout.Token);
        var result = new StringBuilder();
        var ready = false;
        while (true)
        {
            using var message = await ReceiveAsync(socket, timeout.Token);
            var root = message.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready" when !ready && root.GetProperty("protocol").GetInt32() == 1:
                    ready = true;
                    break;
                case "text" when ready:
                    result.Append(root.GetProperty("text").GetString());
                    if (result.Length > 3000) throw new IOException("Mac 返回的译文过长。");
                    break;
                case "done" when ready && result.Length > 0:
                    return result.ToString();
                default:
                    throw new IOException("Mac 翻译失败或服务正忙，请检查服务版本并稍后重试。");
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, object message, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token);

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        var length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (length == buffer.Length) throw new IOException("Mac 返回的消息过大。");
            result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Mac 连接提前关闭或返回了无效数据。");
            length += result.Count;
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, length));
    }
}
