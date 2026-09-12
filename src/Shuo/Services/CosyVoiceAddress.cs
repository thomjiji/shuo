using System.Net;
using System.Net.Sockets;

namespace Shuo.Services;

internal static class CosyVoiceAddress
{
    internal static string ToUrl(string value, int defaultPort = 18766)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Contains("://")) return value.TrimEnd('/');
        if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"http://[{address}]:{defaultPort}";
        return (value.Contains(':') ? $"http://{value}" : $"http://{value}:{defaultPort}").TrimEnd('/');
    }

    internal static string ToDisplay(string value, int defaultPort = 18766)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Port == defaultPort && uri.AbsolutePath == "/"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            return uri.Host.Trim('[', ']');
        return value;
    }

    internal static string WithPort(string value, int port)
    {
        var normalized = ToUrl(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return value;
        return new UriBuilder(uri) { Port = port }.Uri.ToString().TrimEnd('/');
    }

    internal static bool UsesPort(string value, int port) =>
        Uri.TryCreate(ToUrl(value), UriKind.Absolute, out var uri) && uri.Port == port;

    internal static Uri Endpoint(string value, int defaultPort = 18766)
    {
        return new Uri(Origin(value, defaultPort) + "/v1/tts");
    }

    internal static Uri Health(string value, int defaultPort = 18766) => new(Origin(value, defaultPort) + "/health");

    private static string Origin(string value, int defaultPort)
    {
        var normalized = ToUrl(value, defaultPort);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请填写有效的自托管语音服务地址。");
        return uri.ToString().TrimEnd('/');
    }
}
