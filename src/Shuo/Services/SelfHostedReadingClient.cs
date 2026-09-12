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
    internal static Uri Endpoint(string host)
    {
        var value = host.Trim();
        if (value.Length == 0) throw new ArgumentException("请填写 Mac 的 Tailscale 主机 IP。");
        if (!value.Contains("://"))
        {
            if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                value = $"http://[{value}]:18766";
            else value = value.Contains(':') ? "http://" + value : $"http://{value}:18766";
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请填写 Mac 主机 IP，或不含路径的 http/https 服务地址。");
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = "/v1/reading" }.Uri;
    }

    internal static async Task TestAsync(string host, CancellationToken token)
    {
        var endpoint = Endpoint(host);
        var health = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "wss" ? "https" : "http", Path = "/health" }.Uri;
        // This is a direct connection to the user's own Mac, not a cloud API.
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var response = await http.GetAsync(health, token);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = body.RootElement;
        ValidateFormat(root);
        if (!root.GetProperty("ready").GetBoolean()) throw new IOException("Mac 译读模型尚未准备好。");
    }

    internal static async IAsyncEnumerable<byte[]> ReadAsync(string text, Uri endpoint, double speed,
        Action<string> translated, [EnumeratorCancellation] CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > OmniReadingClient.PassageBytes)
            throw new ArgumentException("本段文字为空或过长。");
        if (speed is not (0.85 or 1.0 or 1.15 or 1.3)) throw new ArgumentException("无效的播放速度。");
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(endpoint, connecting.Token);
        }
        var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "start", protocol = 1, text, speed });
        await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, token);
        await foreach (var audio in ReadEventsAsync(socket, translated, token)) yield return audio;
    }

    internal static async IAsyncEnumerable<byte[]> ReadEventsAsync(WebSocket socket, Action<string> translated,
        [EnumeratorCancellation] CancellationToken token)
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
                    ValidateFormat(root);
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
                    throw new IOException(root.GetProperty("message").GetString() ?? "Mac 译读失败。");
                default:
                    throw new IOException("Mac 译读协议不兼容，请更新服务。");
            }
        }
    }

    private static void ValidateFormat(JsonElement root)
    {
        if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("sample_rate").GetInt32() != 24000
            || root.GetProperty("format").GetString() != "pcm_s16le" || root.GetProperty("voice").GetString() != "Serena")
            throw new IOException("Mac 译读协议或音色不兼容，请更新服务。");
    }
}
