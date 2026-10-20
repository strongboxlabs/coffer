using System.Net;
using System.Net.Http;
using System.Net.Sockets;

using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;

namespace Coffer.Api.Http;

/// <summary>
/// Where the server may connect when the URL came from a user (FU-082): notification
/// targets (webhook, Healthchecks) and SimpleFIN's claim and access URLs. Singleton.
/// </summary>
/// <remarks>
/// <para>Anyone who could create a ledger could add a webhook target with any URL, set off a
/// delivery, and read the raw exception back; a SimpleFIN setup token decodes to a URL the
/// server POSTs to inside the request, with the transport error in the response. Together,
/// a port scanner over the Docker network, Postgres, link-local metadata and the LAN.</para>
/// <para>So a connection is allowed to a PUBLIC address over HTTPS, and to anything else —
/// loopback, the private ranges, link-local, carrier-grade NAT, Docker's own networks — only
/// inside <c>Api:Outbound:AllowedNetworks</c>, which is empty by default; plain HTTP likewise
/// only there. A self-hosted ntfy on the LAN is allowed by naming its network.</para>
/// <para>Enforced where the connection is made (<see cref="ConnectCallback"/>), on the
/// addresses the name resolved to, which are the addresses connected to: a name that
/// resolves somewhere public when saved and somewhere private when used is caught, as is a
/// redirect (these clients follow none). <see cref="Refusal"/> checks a URL when it is
/// saved, so the person entering it is told at once.</para>
/// </remarks>
public sealed class OutboundPolicy
{
    /// <summary>Every range that is not a public unicast address.</summary>
    internal static readonly IReadOnlyList<IPNetwork> NotPublic =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/3"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    private readonly IReadOnlyList<IPNetwork> _allowed;

    public OutboundPolicy(IOptions<ApiOptions> options)
        : this(ParseAllowed(options.Value.Outbound.AllowedNetworks))
    {
    }

    internal OutboundPolicy(IReadOnlyList<IPNetwork> allowed) => _allowed = allowed;

    /// <summary>True when the server may connect to <paramref name="address"/> for a URL of
    /// the given scheme.</summary>
    public bool Allows(IPAddress address, bool https)
    {
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (_allowed.Any(n => n.Contains(a))) return true;
        return https && !NotPublic.Any(n => n.Contains(a));
    }

    /// <summary>
    /// Why <paramref name="url"/> may not be saved as a destination, or null when it may.
    /// What can be told without resolving the name: the scheme, and an IP address written
    /// into the URL. A name is checked when it is connected to.
    /// </summary>
    public string? Refusal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Enter a full https:// address.";
        var https = uri.Scheme == Uri.UriSchemeHttps;
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal))
            return Allows(literal, https) ? null : RefusedText;
        if (!https && _allowed.Count == 0)
            return "Use an https:// address. Plain http is allowed only to networks an operator "
                   + "has listed in COFFER_OUTBOUND_ALLOWED_NETWORKS.";
        return null;
    }

    internal const string RefusedText =
        "That address is on a private, loopback or link-local network, which this server does "
        + "not connect to for a URL a user entered. An operator can allow a network with "
        + "COFFER_OUTBOUND_ALLOWED_NETWORKS.";

    /// <summary>
    /// A connect callback for <see cref="SocketsHttpHandler.ConnectCallback"/>: resolves the
    /// name, keeps the addresses <see cref="Allows"/> permits, and connects to those only.
    /// </summary>
    /// <exception cref="OutboundRefusedException">No address the name resolved to is allowed.</exception>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectCallback =>
        async (context, cancellationToken) =>
        {
            var https = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttps;
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var permitted = addresses.Where(a => Allows(a, https)).ToArray();
            if (permitted.Length == 0)
                throw new OutboundRefusedException();

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

    /// <summary>The handler a client that fetches user-supplied URLs is built on: the connect
    /// check, no redirects (each hop would be a URL nobody checked when it was saved), and no
    /// proxy, so the address checked is the address connected to.</summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectCallback,
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    internal static IReadOnlyList<IPNetwork> ParseAllowed(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return [];
        var networks = new List<IPNetwork>();
        foreach (var raw in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(raw, out var network))
                networks.Add(network);
            else if (IPAddress.TryParse(raw, out var address))
                networks.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            else
                throw new InvalidOperationException(
                    $"Api:Outbound:AllowedNetworks has '{raw}', which is neither an IP address nor a CIDR "
                    + "range (e.g. 192.168.1.0/24). Fix COFFER_OUTBOUND_ALLOWED_NETWORKS in .env.");
        }
        return networks;
    }
}

/// <summary>A connection <see cref="OutboundPolicy"/> refused.</summary>
public sealed class OutboundRefusedException : Exception
{
    public OutboundRefusedException() : base(OutboundPolicy.RefusedText) { }
}

/// <summary>
/// What a failed request to a user-supplied URL is allowed to say (FU-082): a category,
/// never the exception's text, which names the host, port and what answered there.
/// </summary>
public static class OutboundFailure
{
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OutboundRefusedException) return OutboundPolicy.RefusedText;
            if (e is HttpRequestException { StatusCode: { } status })
                return $"The destination answered HTTP {(int)status}.";
        }
        return ex switch
        {
            TaskCanceledException or TimeoutException => "The destination did not answer in time.",
            HttpRequestException => "Could not connect to the destination.",
            _ => "Delivery failed. The full message is in the server log.",
        };
    }
}
