using System.Net.WebSockets;
using System.Text.Json;

namespace Shuo.Services;

internal static class WebSocketJson
{
    internal static Task SendAsync(WebSocket socket, object message, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, token);

    internal static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken token)
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
