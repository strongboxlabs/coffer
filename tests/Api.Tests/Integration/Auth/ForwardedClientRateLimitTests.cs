using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Configuration;
using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// Rate limits partition on the CLIENT's address, as the trusted proxy forwards it, and
/// recovery-code attempts are bounded per account too (FU-088). Behind a proxy every client
/// had the proxy's address, so one bucket held everyone: anyone could keep the
/// recovery-code login at 429 for all.
/// </summary>
/// <remarks>
/// The test server has no TCP peer, so a startup filter sets one from <c>X-Test-Peer</c>
/// before the app's forwarded-headers middleware reads it — the address a real proxy would
/// connect from.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ForwardedClientRateLimitTests : DatabaseTest
{
    public ForwardedClientRateLimitTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Peer"].ToString(), out var peer))
                    context.Connection.RemoteIpAddress = peer;
                await inner();
            });
            next(app);
        };
    }

    private ApiFactory Factory(int perAddress = 100_000, int perAccount = 100_000) =>
        new ApiFactory(Db)
            .WithConfig("Api:Auth:RecoveryRateLimitPerMinute", perAddress.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithConfig("Api:Auth:RecoveryAttemptsPerAccountPerHour", perAccount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithServices(s => s.AddSingleton<IStartupFilter, PeerFromHeader>());

    private static async Task<HttpStatusCode> AttemptAsync(
        HttpClient client, string peer, string? forwardedFor, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login/recovery")
        {
            Content = JsonContent.Create(new RecoveryLoginRequest { Username = username, RecoveryCode = "ABCDE-FGHJK" }),
        };
        request.Headers.Add("X-Test-Peer", peer);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    private static string Nobody() => $"nobody-{Guid.NewGuid():N}";

    private static async Task<HttpStatusCode> RegisterAsync(HttpClient client, string peer, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/register")
        {
            Content = JsonContent.Create(new
            {
                client_name = "Rate Limit Client",
                redirect_uris = new[] { "https://example.com/cb" },
                token_endpoint_auth_method = "none",
            }),
        };
        request.Headers.Add("X-Test-Peer", peer);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    /// <summary>A held request polled each second until it is answered with
    /// <paramref name="permitted"/>: when, after <paramref name="since"/>, the last refused
    /// request was sent and the permitted one was answered; null if it is still refused at
    /// <paramref name="giveUp"/>. Any answer other than a refusal or a permit fails, so
    /// an endpoint that starts failing is not taken for a window that ended.</summary>
    private static async Task<(TimeSpan LastRefusedSent, TimeSpan PermittedAnswered)?> LetThroughAfterAsync(
        System.Diagnostics.Stopwatch since, TimeSpan giveUp, HttpStatusCode permitted,
        Func<Task<HttpStatusCode>> attempt)
    {
        var lastRefusedSent = TimeSpan.Zero;
        while (since.Elapsed < giveUp)
        {
            var sent = since.Elapsed;
            var status = await attempt();
            if (status == permitted) return (lastRefusedSent, since.Elapsed);
            Assert.True(status == HttpStatusCode.TooManyRequests,
                $"A request sent {sent.TotalSeconds:0.0} s after the first was answered {(int)status} {status}, "
                + $"neither a refusal nor the {(int)permitted} {permitted} of a permitted one.");
            lastRefusedSent = sent;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return null;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_has_its_own_bucket()
    {
        await using var factory = Factory(perAddress: 2);
        using var client = factory.CreateClient();
        const string proxy = "172.18.0.2";   // a proxy on Docker's network

        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, proxy, "203.0.113.7", Nobody()));
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, proxy, "203.0.113.7", Nobody()));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, proxy, "203.0.113.7", Nobody()));
        // Another client through the same proxy is not held up by the first.
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, proxy, "198.51.100.4", Nobody()));
    }

    [Fact]
    public async Task A_client_that_is_not_a_trusted_proxy_cannot_choose_its_bucket()
    {
        await using var factory = Factory(perAddress: 2);
        using var client = factory.CreateClient();
        const string direct = "203.0.113.50";   // straight to the published port, from the internet

        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, direct, "198.51.100.1", Nobody()));
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, direct, "198.51.100.2", Nobody()));
        // A third invented X-Forwarded-For is still the same bucket: its own address.
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, direct, "198.51.100.3", Nobody()));
    }

    /// <summary>
    /// Both per-address limits, recovery login and Dynamic Client Registration, are a
    /// one-minute fixed window: a client held at 429 is let through again when its window
    /// ends, not before, and no later than a little after it. Takes about a minute.
    /// </summary>
    [Fact]
    public async Task A_client_held_at_the_limit_is_let_through_when_its_minute_ends()
    {
        await using var factory = Factory(perAddress: 1)
            .WithMcpEnabled()
            .WithConfig("Api:Mcp:DcrRateLimitPerMinute", "1")
            .WithConfig("Api:Mcp:MaxDynamicClients", "1000");
        using var client = factory.CreateClient();
        const string proxy = "172.18.0.2";
        const string held = "203.0.113.9";

        var recoveryWindow = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, proxy, held, Nobody()));
        var registerWindow = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Created, await RegisterAsync(client, proxy, held));

        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, proxy, held, Nobody()));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RegisterAsync(client, proxy, held));
        // Registration is per client address too: another client is not held up.
        Assert.Equal(HttpStatusCode.Created, await RegisterAsync(client, proxy, "198.51.100.4"));

        var giveUp = TimeSpan.FromSeconds(80);
        var recovery = LetThroughAfterAsync(recoveryWindow, giveUp, HttpStatusCode.Unauthorized,
            () => AttemptAsync(client, proxy, held, Nobody()));
        var register = LetThroughAfterAsync(registerWindow, giveUp, HttpStatusCode.Created,
            () => RegisterAsync(client, proxy, held));
        await Task.WhenAll(recovery, register);

        // A window is a minute, measured from the client's first request. A permit is
        // answered no earlier than that, and a refusal is decided before it ends, so the
        // last refused request was SENT before it ends, plus a replenishment tick: bounding
        // when requests were sent, not when they were answered, keeps a slow request from
        // reading as a long window.
        foreach (var (limit, after) in new[] { ("recovery login", await recovery), ("registration", await register) })
        {
            Assert.True(after is not null, $"The {limit} limit still held the client {giveUp.TotalSeconds:0} s after its first request.");
            var (lastRefusedSent, permittedAnswered) = after!.Value;
            Assert.True(permittedAnswered >= TimeSpan.FromSeconds(58),
                $"The {limit} limit let the client through {permittedAnswered.TotalSeconds:0.0} s after its first request, before its minute ended.");
            Assert.True(lastRefusedSent < TimeSpan.FromSeconds(65),
                $"The {limit} limit refused a request sent {lastRefusedSent.TotalSeconds:0.0} s after the client's first: its window is longer than a minute.");
        }
    }

    [Fact]
    public async Task One_account_is_bounded_whatever_addresses_the_guesses_come_from()
    {
        await using var factory = Factory(perAccount: 2);
        using var client = factory.CreateClient();
        var target = Nobody();

        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, "172.18.0.2", "203.0.113.1", target));
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, "172.18.0.2", "203.0.113.2", target));
        Assert.Equal(HttpStatusCode.TooManyRequests, await AttemptAsync(client, "172.18.0.2", "203.0.113.3", target));
        // Not a global limit: another account is unaffected.
        Assert.Equal(HttpStatusCode.Unauthorized, await AttemptAsync(client, "172.18.0.2", "203.0.113.4", Nobody()));
    }
}

/// <summary>Reading <c>Api:TrustedProxies</c> (FU-088).</summary>
public sealed class TrustedProxiesTests
{
    [Fact]
    public void Unset_means_loopback_and_the_private_ranges()
    {
        var networks = TrustedProxies.Parse(null);

        Assert.Contains(networks, n => n.Contains(IPAddress.Parse("172.17.0.1")));   // Docker's bridge
        Assert.Contains(networks, n => n.Contains(IPAddress.Parse("127.0.0.1")));
        Assert.Contains(networks, n => n.Contains(IPAddress.Parse("192.168.0.25")));
        Assert.DoesNotContain(networks, n => n.Contains(IPAddress.Parse("203.0.113.7")));
    }

    [Fact]
    public void A_list_of_ranges_and_single_addresses_replaces_the_defaults()
    {
        var networks = TrustedProxies.Parse("172.18.0.0/16, 198.51.100.9");

        Assert.Equal(2, networks.Count);
        Assert.Contains(networks, n => n.Contains(IPAddress.Parse("198.51.100.9")));
        Assert.DoesNotContain(networks, n => n.Contains(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void An_entry_that_is_neither_stops_the_boot()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TrustedProxies.Parse("172.18.0.0/16, proxy.local"));
        Assert.Contains("proxy.local", ex.Message);
    }
}
