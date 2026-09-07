using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using Velopack.Sources;

namespace Shuo;

[SupportedOSPlatform("windows")]
internal sealed class SystemProxyDownloader : HttpClientFileDownloader
{
    protected override HttpClient CreateHttpClient(IDictionary<string, string>? headers, double timeout)
    {
        // Use Windows Internet Settings (including PAC and bypass rules), independent of environment proxies.
        var handler = new WinHttpHandler
        {
            WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy,
            AutomaticRedirection = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(timeout) };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        foreach (var header in headers ?? new Dictionary<string, string>())
            client.DefaultRequestHeaders.Add(header.Key, header.Value);
        return client;
    }
}
