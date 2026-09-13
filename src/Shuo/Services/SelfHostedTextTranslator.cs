using System.Net.WebSockets;
using System.Text;
using static Shuo.Services.WebSocketJson;

namespace Shuo.Services;

internal sealed class SelfHostedTextTranslator(Uri endpoint)
{
    internal static Uri Endpoint(string host) => LocalServiceEndpoint.Create(host, 18766, "/v1/translation");

    internal async Task<string> TranslateAsync(string source, string target, CancellationToken token)
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

}
