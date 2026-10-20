using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Coffer.Api.Auth;

/// <summary>
/// Single-use proof that the consent screen was shown to this user, for this request
/// (2026-10-12 audit, H1 and M1).
/// </summary>
/// <remarks>
/// <para>Before, <c>POST /oauth/authorize</c> issued a code for any request whose
/// <c>decision</c> wasn't "deny". The session cookie is SameSite=Strict, so another
/// site couldn't post it — but a page on a sibling subdomain could, and so got an MCP
/// token for whoever was signed in. And the consent screen read the client's name and
/// redirect from the link, so an attacker chose what it said.</para>
///
/// <para>Now the GET that decides consent is needed mints a ticket and hands it to the
/// consent page. The page shows what the SERVER registered for the client, fetched by
/// the ticket. Allow is honoured only with the ticket, which must match the user, the
/// client, the redirect, the scopes and the PKCE challenge of the request it approves —
/// so it can't be replayed for a different request, or a different code_challenge
/// swapped in after the user looked. Another origin never sees the ticket: it reaches
/// the browser only in a same-origin redirect.</para>
///
/// <para>In memory, not in the database: a ticket lives for minutes and Coffer is one
/// process. A restart loses pending consents; the user starts the connection again.</para>
/// </remarks>
public sealed class OAuthConsentTickets
{
    /// <summary>How long the consent screen stays usable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public OAuthConsentTickets(TimeProvider clock) => _clock = clock;

    /// <summary>What a ticket approves.</summary>
    public sealed record Ticket(
        Guid UserId, string ClientId, string RedirectUri, string Scopes, string CodeChallenge,
        DateTimeOffset ExpiresAt);

    /// <summary>Mint a ticket for this user and request; returns its id.</summary>
    public string Mint(
        Guid userId, string clientId, string redirectUri, IEnumerable<string> scopes, string codeChallenge)
    {
        PruneExpired();
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var id = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tickets[id] = new Ticket(
            userId, clientId, redirectUri, Canonical(scopes), codeChallenge, _clock.GetUtcNow().Add(Lifetime));
        return id;
    }

    /// <summary>The live ticket <paramref name="id"/> if it belongs to <paramref name="userId"/>; never consumes.</summary>
    public Ticket? Peek(string id, Guid userId) =>
        _tickets.TryGetValue(id, out var ticket) && ticket.UserId == userId && !Expired(ticket)
            ? ticket
            : null;

    /// <summary>
    /// Consume ticket <paramref name="id"/> if it approves exactly this request. Removed
    /// whether or not it matches: a ticket presented with the wrong request is spent.
    /// </summary>
    public bool TryConsume(
        string? id, Guid userId, string clientId, string redirectUri, IEnumerable<string> scopes,
        string codeChallenge)
    {
        if (string.IsNullOrEmpty(id) || !_tickets.TryRemove(id, out var ticket))
            return false;
        return !Expired(ticket)
            && ticket.UserId == userId
            && string.Equals(ticket.ClientId, clientId, StringComparison.Ordinal)
            && string.Equals(ticket.RedirectUri, redirectUri, StringComparison.Ordinal)
            && string.Equals(ticket.Scopes, Canonical(scopes), StringComparison.Ordinal)
            && string.Equals(ticket.CodeChallenge, codeChallenge, StringComparison.Ordinal);
    }

    private bool Expired(Ticket ticket) => _clock.GetUtcNow() >= ticket.ExpiresAt;

    private void PruneExpired()
    {
        foreach (var (key, ticket) in _tickets)
            if (Expired(ticket))
                _tickets.TryRemove(key, out _);
    }

    private static string Canonical(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)
                              .Order(StringComparer.Ordinal));
}
