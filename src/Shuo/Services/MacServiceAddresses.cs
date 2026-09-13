using System.Net;

namespace Shuo.Services;

internal sealed record MacServiceAddresses(string Recognition, string Captions, string Reading)
{
    internal string SharedHost => Hosts.FirstOrDefault() ?? "";
    internal bool Separate => Hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
        || Hosts.Any(host => Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown);

    private IEnumerable<string> Hosts => new[] { SelfHostedAddress.ToDisplay(Recognition), Captions, Reading }
        .Select(host => host.Trim()).Where(host => host.Length > 0);

    internal static MacServiceAddresses Shared(string host)
    {
        host = host.Trim().Trim('[', ']');
        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("共用地址请填写 Mac 主机名或 IP；自定义端口请使用“分别指定主机”。");
        return new(SelfHostedAddress.ToUrl(host), host, host);
    }

    internal void Validate()
    {
        foreach (var (host, port) in new[] { (Recognition, 18765), (Captions, 18766), (Reading, 18766) })
            if (!string.IsNullOrWhiteSpace(host)) LocalServiceEndpoint.Create(host, port, "/health");
    }
}
