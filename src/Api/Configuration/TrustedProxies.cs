using System.Net;

namespace Coffer.Api.Configuration;

/// <summary>
/// The networks whose <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> are believed
/// (FU-088). Read from <c>Api:TrustedProxies</c>, a comma-separated list of addresses and
/// CIDR ranges; empty means <see cref="Defaults"/>.
/// </summary>
/// <remarks>
/// <para>Forwarded headers used to be believed from any peer, and <c>X-Forwarded-For</c> not
/// at all. So behind a proxy every client had the proxy's address, every rate limit was one
/// bucket shared by everyone — one client could hold the recovery-code login at 429 for all
/// of them, or use up the client-registration cap — and on a published port anyone could
/// set the scheme and host the app builds its own URLs from.</para>
/// <para>The default trusts loopback and the private ranges, which is where a reverse proxy
/// on the host (through Docker's bridge), a cloudflared container or a proxy on the LAN
/// connects from. A client reaching the published port from the internet arrives from its
/// own public address, which is not trusted, so what it claims is ignored. A proxy at a
/// public address is configured explicitly.</para>
/// </remarks>
public static class TrustedProxies
{
    /// <summary>Loopback, RFC 1918, and IPv6 unique-local and link-local.</summary>
    public static readonly IReadOnlyList<IPNetwork> Defaults =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    /// <summary>The configured networks, or <see cref="Defaults"/> when none are.</summary>
    /// <exception cref="InvalidOperationException">An entry is neither an address nor a
    /// CIDR range; the boot stops rather than trust less, or more, than was meant.</exception>
    public static IReadOnlyList<IPNetwork> Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Defaults;
        var networks = new List<IPNetwork>();
        foreach (var raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(raw, out var network))
                networks.Add(network);
            else if (IPAddress.TryParse(raw, out var address))
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else
                throw new InvalidOperationException(
                    $"Api:TrustedProxies has '{raw}', which is neither an IP address nor a CIDR range "
                    + "(e.g. 172.18.0.0/16). Fix COFFER_TRUSTED_PROXIES in .env.");
        }
        return networks;
    }
}
