using System.Net;
using System.Net.Sockets;

namespace Shuo.Services;

internal static class SelfHostedAddress
{
    internal static string ToUrl(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Contains("://")) return value;
        if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            return $"http://[{address}]:18765";
        return value.Contains(':') ? $"http://{value}" : $"http://{value}:18765";
    }

    internal static string ToDisplay(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Port == 18765 && uri.AbsolutePath == "/"
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            return uri.Host.Trim('[', ']');
        return value;
    }
}
