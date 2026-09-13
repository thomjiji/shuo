using System.Net;
using System.Net.Sockets;

namespace Shuo.Services;

internal static class LocalServiceEndpoint
{
    internal static Uri Create(string host, int port, string path)
    {
        var value = host.Trim();
        if (value.Length == 0) throw new ArgumentException("请填写 Mac 的 Tailscale 主机 IP。");
        if (!value.Contains("://"))
        {
            if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                value = $"http://[{value}]:{port}";
            else value = value.Contains(':') ? "http://" + value : $"http://{value}:{port}";
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请填写 Mac 主机 IP，或不含路径的 http/https 服务地址。");
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws", Path = path }.Uri;
    }

}
