using System.Net.WebSockets;
using static Shuo.Services.WebSocketJson;

namespace Shuo.Services;

internal sealed class SelfHostedAsrClient(Uri endpoint)
{
    internal async Task RunAsync(IAsyncEnumerable<byte[]> audio, Action ready, Action<string> snapshot,
        CancellationToken stop, CancellationToken abort = default, Action<string>? committed = null,
        string model = SelfHostedAsrModels.Large)
    {
        if (!SelfHostedAsrModels.IsSupported(model)) throw new ArgumentException("不支持所选实时字幕识别模型。");
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(abort);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(endpoint, connecting.Token);
            await SendAsync(socket, new { type = "start", protocol = 1, sample_rate = 16000,
                format = "pcm_s16le", language = "auto", captions = committed is not null, model }, connecting.Token);
            using var response = await ReceiveAsync(socket, connecting.Token);
            var root = response.RootElement;
            if (root.GetProperty("type").GetString() != "ready")
                throw new IOException("Mac 语音识别尚未就绪或正被占用，请稍后重试。");
            if (root.GetProperty("protocol").GetInt32() != 1 || !root.TryGetProperty("model", out var selected)
                || selected.GetString() != model)
                throw new IOException("Mac 返回的实时字幕识别模型与所选模型不同，请更新 Mac 服务或重新选择模型。");
        }
        ready();
        using var capture = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        var sending = SendAudioAsync();
        var receiving = ReceiveTextAsync();
        try
        {
            var first = await Task.WhenAny(sending, receiving);
            await first;
            if (first != sending) throw new IOException("Mac 提前结束了识别会话，请重新开始。");
            lifetime.CancelAfter(TimeSpan.FromSeconds(60));
            await SendAsync(socket, new { type = "finish" }, lifetime.Token);
            await receiving;
        }
        finally
        {
            lifetime.Cancel();
            capture.Cancel();
            socket.Abort();
            try { await Task.WhenAll(sending, receiving); } catch { }
        }

        async Task SendAudioAsync()
        {
            await using var frames = audio.GetAsyncEnumerator(capture.Token);
            while (true)
            {
                // The source may observe a linked stop before this client's callback runs.
                // Cancellation while reading ends capture; send timeouts still fail the session.
                try
                {
                    if (!await frames.MoveNextAsync() || capture.IsCancellationRequested) return;
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(frames.Current.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
        }

        async Task ReceiveTextAsync()
        {
            var confirmedLength = 0;
            while (true)
            {
                using var message = await ReceiveAsync(socket, lifetime.Token);
                var root = message.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is not ("partial" or "final")) throw new IOException("Mac 语音识别失败，请检查服务。");
                var text = root.GetProperty("text").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text)) snapshot(text);
                var confirmed = type == "final" ? text
                    : root.TryGetProperty("confirmed", out var stable) ? stable.GetString() ?? "" : "";
                if (confirmed.Length > confirmedLength)
                {
                    var segment = confirmed[confirmedLength..].Trim();
                    if (segment.Length > 0) committed?.Invoke(segment);
                    confirmedLength = confirmed.Length;
                }
                if (type == "final") return;
            }
        }
    }
}
