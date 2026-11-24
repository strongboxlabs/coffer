using System.Net;

using Coffer.Api.Tests.Integration.Auth;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// The per-address limits are one-minute windows: a held client is let through when its
/// minute ends. A wait on the real clock, about a minute of it, bounded by how long the
/// requests take on the machine, so it runs here, on demand. The every-build tests of the
/// same limits (<c>ForwardedClientRateLimitTests</c>) hold who is refused and who is not.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitWindowTests : DatabaseTest
{
    public RateLimitWindowTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
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

    /// <summary>
    /// Both per-address limits, recovery login and Dynamic Client Registration, are a
    /// one-minute fixed window: a client held at 429 is let through again when its window
    /// ends, not before, and no later than a little after it. Takes about a minute.
    /// </summary>
    [Fact]
    public async Task A_client_held_at_the_limit_is_let_through_when_its_minute_ends()
    {
        await using var factory = ForwardedClientRateLimitTests.Factory(Db, perAddress: 1)
            .WithMcpEnabled()
            .WithConfig("Api:Mcp:DcrRateLimitPerMinute", "1")
            .WithConfig("Api:Mcp:MaxDynamicClients", "1000");
        using var client = factory.CreateClient();
        const string proxy = "172.18.0.2";
        const string held = "203.0.113.9";

        var recoveryWindow = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Unauthorized, await ForwardedClientRateLimitTests.AttemptAsync(client, proxy, held, ForwardedClientRateLimitTests.Nobody()));
        var registerWindow = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.Created, await ForwardedClientRateLimitTests.RegisterAsync(client, proxy, held));

        Assert.Equal(HttpStatusCode.TooManyRequests, await ForwardedClientRateLimitTests.AttemptAsync(client, proxy, held, ForwardedClientRateLimitTests.Nobody()));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ForwardedClientRateLimitTests.RegisterAsync(client, proxy, held));
        // Registration is per client address too: another client is not held up.
        Assert.Equal(HttpStatusCode.Created, await ForwardedClientRateLimitTests.RegisterAsync(client, proxy, "198.51.100.4"));

        var giveUp = TimeSpan.FromSeconds(80);
        var recovery = LetThroughAfterAsync(recoveryWindow, giveUp, HttpStatusCode.Unauthorized,
            () => ForwardedClientRateLimitTests.AttemptAsync(client, proxy, held, ForwardedClientRateLimitTests.Nobody()));
        var register = LetThroughAfterAsync(registerWindow, giveUp, HttpStatusCode.Created,
            () => ForwardedClientRateLimitTests.RegisterAsync(client, proxy, held));
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
}
