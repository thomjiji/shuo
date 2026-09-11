using System.Net;
using System.Net.Sockets;

namespace Shuo.Services;

internal static class CosyVoiceAddress
{
    internal static string ToUrl(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Contains("://")) return value.TrimEnd('/');
        if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"http://[{address}]:18766";
        return (value.Contains(':') ? $"http://{value}" : $"http://{value}:18766").TrimEnd('/');
    }

    internal static string ToDisplay(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Port == 18766 && uri.AbsolutePath == "/"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            return uri.Host.Trim('[', ']');
        return value;
    }

    internal static Uri Endpoint(string value)
    {
        return new Uri(Origin(value) + "/v1/tts");
    }

    internal static Uri Health(string value) => new(Origin(value) + "/health");

    private static string Origin(string value)
    {
        var normalized = ToUrl(value);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("请填写有效的 CosyVoice 服务地址。");
        return uri.ToString().TrimEnd('/');
    }
}
