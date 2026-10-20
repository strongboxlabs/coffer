using System.Net;
using System.Net.Sockets;
using System.Text;

using Coffer.Api.Http;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Tests.Unit.Http;

/// <summary>
/// Where the server may connect for a URL a user entered (FU-082). A webhook target or a
/// SimpleFIN setup token was a way to make it reach Postgres, the Docker network, link-local
/// metadata and the LAN, and to read back what answered.
/// </summary>
public sealed class OutboundPolicyTests
{
    private static readonly OutboundPolicy Default = new([]);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.17.0.1")]          // Docker's bridge
    [InlineData("172.18.0.2")]          // a compose network: postgres resolves here
    [InlineData("192.168.1.38")]
    [InlineData("169.254.169.254")]     // cloud metadata
    [InlineData("100.64.0.1")]          // carrier-grade NAT
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]     // IPv4-mapped: the same address in IPv6 clothing
    public void A_non_public_address_is_refused_even_over_https(string address)
    {
        Assert.False(Default.Allows(IPAddress.Parse(address), https: true));
    }

    [Fact]
    public void A_public_address_is_allowed_over_https_only()
    {
        Assert.True(Default.Allows(IPAddress.Parse("140.82.112.3"), https: true));
        Assert.False(Default.Allows(IPAddress.Parse("140.82.112.3"), https: false));
    }

    [Fact]
    public void An_allowed_network_is_reachable_and_over_http_too()
    {
        var lan = new OutboundPolicy(OutboundPolicy.ParseAllowed("192.168.1.50, 10.9.0.0/16"));

        Assert.True(lan.Allows(IPAddress.Parse("192.168.1.50"), https: false));
        Assert.True(lan.Allows(IPAddress.Parse("10.9.4.4"), https: true));
        Assert.False(lan.Allows(IPAddress.Parse("192.168.1.51"), https: true));   // only what is named
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("http://hooks.example.com/x")]     // plain http to the internet
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://[::1]:8080/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public void A_url_that_cannot_be_used_is_refused_when_it_is_saved(string url)
    {
        Assert.NotNull(Default.Refusal(url));
    }

    [Theory]
    [InlineData("https://ntfy.sh/my-topic")]
    [InlineData("https://hc-ping.com/abc-123")]
    [InlineData("https://user:pass@bridge.simplefin.org/simplefin")]
    public void A_public_https_url_is_accepted(string url)
    {
        Assert.Null(Default.Refusal(url));
    }

    [Fact]
    public void An_entry_that_is_neither_an_address_nor_a_range_stops_the_boot()
    {
        Assert.Throws<InvalidOperationException>(() => OutboundPolicy.ParseAllowed("ntfy.lan"));
    }

    // --- where it is enforced: the connection --------------------------------------

    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]   // a NAME that resolves to loopback: what was saved said nothing
    public async Task A_connection_to_a_refused_address_is_never_made(string host)
    {
        var (listener, port) = Listen();
        try
        {
            using var http = new HttpClient(Default.CreateHandler());
            var ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => http.GetAsync($"https://{host}:{port}/"));

            Assert.IsType<OutboundRefusedException>(ex.InnerException);
            Assert.False(listener.Pending(), "the refused address received a connection");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task An_allowed_network_is_connected_to()
    {
        var (listener, port) = Listen();
        try
        {
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            });
            using var http = new HttpClient(new OutboundPolicy(OutboundPolicy.ParseAllowed("127.0.0.1")).CreateHandler());

            var response = await http.GetAsync($"http://127.0.0.1:{port}/");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    // --- what a failure may say ----------------------------------------------------

    [Fact]
    public void A_failure_is_described_by_its_kind_and_never_quotes_the_host()
    {
        var refused = new HttpRequestException("Connection refused (postgres:5432)", new OutboundRefusedException());
        var status = new HttpRequestException("Response status code does not indicate success: 404 (Not Found).",
            null, HttpStatusCode.NotFound);
        var transport = new HttpRequestException("No such host is known. (internal.corp:443)");

        Assert.Equal(OutboundPolicy.RefusedText, OutboundFailure.Describe(refused));
        Assert.Equal("The destination answered HTTP 404.", OutboundFailure.Describe(status));
        Assert.Equal("Could not connect to the destination.", OutboundFailure.Describe(transport));
        Assert.DoesNotContain("postgres", OutboundFailure.Describe(refused));
        Assert.DoesNotContain("internal.corp", OutboundFailure.Describe(transport));
    }

    // --- SimpleFIN -------------------------------------------------------------------

    [Fact]
    public async Task A_setup_token_that_decodes_to_an_internal_address_is_refused_before_any_request()
    {
        var calls = 0;
        var stub = new CountingHandler(() => calls++);
        var client = new SimpleFinClient(new HttpClient(stub), TimeProvider.System, policy: Default);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://172.18.0.2:5432/claim"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.ExchangeSetupTokenAsync(token));

        Assert.Contains("does not connect to", ex.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task An_unreachable_provider_is_reported_without_the_transport_detail()
    {
        var stub = new CountingHandler(() => throw new HttpRequestException("Connection refused (10.0.0.9:6379)"));
        var client = new SimpleFinClient(new HttpClient(stub), TimeProvider.System, policy: Default);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://bridge.simplefin.org/claim/abc"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var ex = await Assert.ThrowsAsync<SimpleFinException>(() => client.ExchangeSetupTokenAsync(token));

        Assert.DoesNotContain("10.0.0.9", ex.Message);
        Assert.DoesNotContain("6379", ex.Message);
        Assert.Contains("Could not connect", ex.Message);
    }

    private sealed class CountingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin") });
        }
    }
}
