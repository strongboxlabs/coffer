using Coffer.Api.Auth;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Unit.Auth;

/// <summary>
/// The consent ticket store and the redirect rule behind the 2026-10-12 security audit's
/// H1 and M1 fixes.
/// </summary>
public sealed class OAuthConsentTicketsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();
    private static readonly string[] Scopes = ["coffer.read", "offline_access"];
    private const string Client = "client-1";
    private const string Redirect = "https://example.com/cb";
    private const string Challenge = "challenge";

    [Fact]
    public void A_ticket_approves_exactly_the_request_it_was_minted_for_once()
    {
        var tickets = new OAuthConsentTickets(new TestClock(Start));
        var id = tickets.Mint(User, Client, Redirect, Scopes, Challenge);

        // Scope order and duplicates don't matter; the set does.
        Assert.True(tickets.TryConsume(id, User, Client, Redirect,
            ["offline_access", "coffer.read", "coffer.read"], Challenge));
        Assert.False(tickets.TryConsume(id, User, Client, Redirect, Scopes, Challenge));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("client")]
    [InlineData("redirect")]
    [InlineData("scopes")]
    [InlineData("challenge")]
    public void A_ticket_presented_with_any_other_request_is_refused_and_spent(string differs)
    {
        var tickets = new OAuthConsentTickets(new TestClock(Start));
        var id = tickets.Mint(User, Client, Redirect, Scopes, Challenge);

        var ok = tickets.TryConsume(id,
            differs == "user" ? Guid.NewGuid() : User,
            differs == "client" ? "client-2" : Client,
            differs == "redirect" ? "https://evil.example/cb" : Redirect,
            differs == "scopes" ? ["coffer.read", "coffer.write"] : Scopes,
            differs == "challenge" ? "other-challenge" : Challenge);

        Assert.False(ok);
        // Spent: presenting the right request afterwards doesn't revive it.
        Assert.False(tickets.TryConsume(id, User, Client, Redirect, Scopes, Challenge));
    }

    [Fact]
    public void A_ticket_expires()
    {
        var clock = new TestClock(Start);
        var tickets = new OAuthConsentTickets(clock);
        var id = tickets.Mint(User, Client, Redirect, Scopes, Challenge);

        clock.Now += OAuthConsentTickets.Lifetime;

        Assert.Null(tickets.Peek(id, User));
        Assert.False(tickets.TryConsume(id, User, Client, Redirect, Scopes, Challenge));
    }

    [Fact]
    public void Peek_is_for_the_owner_only_and_never_consumes()
    {
        var tickets = new OAuthConsentTickets(new TestClock(Start));
        var id = tickets.Mint(User, Client, Redirect, Scopes, Challenge);

        Assert.Null(tickets.Peek(id, Guid.NewGuid()));
        Assert.NotNull(tickets.Peek(id, User));
        Assert.True(tickets.TryConsume(id, User, Client, Redirect, Scopes, Challenge));
    }

    [Theory]
    [InlineData("https://example.com/cb", true)]
    [InlineData("http://127.0.0.1:33418/cb", true)]
    [InlineData("http://localhost/cb", true)]
    [InlineData("http://[::1]:8080/cb", true)]
    [InlineData("javascript://localhost/%0Aalert(1)", false)]   // the audit's H1: IsLoopback is true here
    [InlineData("javascript://127.0.0.1/x", false)]
    [InlineData("http://example.com/cb", false)]
    [InlineData("ftp://localhost/cb", false)]
    [InlineData("file://localhost/etc/passwd", false)]
    public void Only_https_or_http_on_localhost_is_an_allowed_redirect(string uri, bool allowed) =>
        Assert.Equal(allowed, OAuthEndpoints.IsAllowedRedirect(new Uri(uri)));
}
