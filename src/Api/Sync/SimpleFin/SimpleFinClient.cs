using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Sync.SimpleFin;

/// <summary>
/// Typed HTTP gateway to SimpleFIN Bridge (ADR-0006). Pinned to
/// protocol <c>v2.0.0</c> (2026-03-19) — every <c>/accounts</c>
/// request carries <c>?version=2</c> so SimpleFIN returns the
/// flat top-level shape (<c>connections</c>, <c>errlist</c>,
/// <c>accounts</c>) instead of the pre-v2 nested form.
///
/// Three responsibilities today:
///   • <see cref="ExchangeSetupTokenAsync"/> — one-shot: trade the
///     base64url-encoded setup token the user pastes from
///     simplefin.org for the long-lived access URL.
///   • <see cref="GetInstitutionNameAsync"/> — optional follow-up
///     read after exchange, used to populate
///     <c>feed_connections.institution_name</c> for the wizard.
///   • <see cref="GetAccountsWithTransactionsAsync"/> — full sync
///     pull, called from <see cref="SimpleFinSyncService"/>.
/// </summary>
/// <remarks>
/// <para>The <see cref="HttpClient"/> instance is obtained via
/// <c>IHttpClientFactory</c> in Program.cs and injected here scoped;
/// the factory handles connection-pool lifetime correctly without
/// us managing it.</para>
///
/// <para>SimpleFIN encodes Basic-auth credentials directly in the
/// access URL (<c>https://username:password@bridge.simplefin.org/...</c>).
/// We parse them out client-side and put them in an
/// <c>Authorization: Basic</c> header — the embedded credentials in
/// the path are never sent on the wire.</para>
///
/// <para>The plaintext access URL itself never returns to a caller
/// outside this class; the endpoint receives it once on
/// <see cref="ExchangeSetupTokenAsync"/> and seals it before it
/// hits a log line or response body.</para>
///
/// <para>Defensive posture (per project memory): every external API
/// surface here distinguishes 403 (token revoked / expired — caller
/// flips the connection to <c>needs_reauth</c>) from generic
/// non-2xx (typed <see cref="SimpleFinException"/>). On success,
/// the SimpleFIN <c>errlist[]</c> is parsed and returned to the
/// caller alongside the accounts so partial-failure messages
/// surface to the SPA instead of being silently dropped.</para>
/// </remarks>
public sealed class SimpleFinClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SimpleFinClient>? _logger;

    // logger is optional (default null) so the many direct test constructions
    // need no change; DI injects the real logger on the registered typed client.
    public SimpleFinClient(HttpClient http, ILogger<SimpleFinClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Exchange the one-shot setup token for the long-lived access
    /// URL. The token is a base64url-encoded URL string; SimpleFIN's
    /// claim flow is a POST with empty body to that URL, which
    /// returns the access URL in the response body.
    /// </summary>
    /// <param name="setupToken">Base64url-encoded URL string the
    /// user pasted from <c>simplefin.org/setup</c>. One-shot —
    /// SimpleFIN invalidates it on first successful exchange.</param>
    /// <returns>The access URL with Basic-auth credentials
    /// embedded. Caller seals this before it touches storage.</returns>
    /// <exception cref="SimpleFinException">Setup token malformed,
    /// already-consumed, expired, or the network exchange
    /// failed.</exception>
    public async Task<string> ExchangeSetupTokenAsync(
        string setupToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupToken);

        string claimUrl;
        try
        {
            claimUrl = DecodeBase64Url(setupToken);
        }
        catch (FormatException ex)
        {
            throw new SimpleFinException(
                "Setup token is not valid base64url. Confirm it was " +
                "copied in full from simplefin.org/setup.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, claimUrl);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        var response = await _http.SendAsync(request, cancellationToken)
                                  .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SimpleFinException(
                $"SimpleFIN setup-token exchange failed ({(int)response.StatusCode} " +
                $"{response.StatusCode}). The token may be already-consumed " +
                $"or expired — generate a fresh one at simplefin.org/setup.");
        }

        var accessUrl = (await response.Content.ReadAsStringAsync(cancellationToken)
                                              .ConfigureAwait(false))
                        .Trim();
        if (string.IsNullOrEmpty(accessUrl) || !accessUrl.StartsWith("http"))
        {
            throw new SimpleFinException(
                "SimpleFIN returned an unexpected response body. Expected " +
                "a single access URL; got an empty or malformed value.");
        }
        return accessUrl;
    }

    /// <summary>
    /// Probe an access URL: fetch the FI's connection list without
    /// any transactions (<c>?version=2&amp;start-date=...future...</c>)
    /// and return the first <c>connections[].name</c>. Used to
    /// populate <c>feed_connections.institution_name</c> on connect;
    /// failure is non-fatal (institution name stays NULL, sync slice
    /// populates it from the first transaction pull).
    /// </summary>
    /// <returns>Institution display name, or <c>null</c> if the
    /// probe failed, the access URL is revoked, or the feed has no
    /// connections.</returns>
    public async Task<string?> GetInstitutionNameAsync(
        string accessUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessUrl);

        try
        {
            // start-date in the future = no transactions, just the
            // account / connection envelope. Cheaper than a full
            // accounts pull.
            var future = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
            var probeUri = BuildAccountsUri(accessUrl, $"?version=2&start-date={future}");

            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri.NoCredentials);
            request.Headers.Authorization = probeUri.AuthHeader;
            var response = await _http.SendAsync(request, cancellationToken)
                                      .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                                                          .ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                                               .ConfigureAwait(false);

            // v2: connections is a top-level array. Each entry has
            // `name` (institution display name).
            if (json.RootElement.TryGetProperty("connections", out var connections)
                && connections.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in connections.EnumerateArray())
                {
                    if (c.TryGetProperty("name", out var n)
                        && n.ValueKind == JsonValueKind.String)
                    {
                        var name = n.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            // Best-effort enrichment — never block the connect flow on
            // a slow / flaky probe. The wizard renders "SimpleFIN" as
            // the fallback until first sync fills this in. Log at Debug so
            // the swallow is never fully silent (ADR-0086).
            _logger?.LogDebug(ex, "SimpleFIN institution-name probe failed; falling back to default label");
            return null;
        }
    }

    /// <summary>
    /// Fetch every account on the SimpleFIN feed + its transactions
    /// since <paramref name="startDate"/>. The full sync path; called
    /// from <see cref="SimpleFinSyncService"/> on a Sync-now click.
    /// </summary>
    /// <param name="accessUrl">Plaintext SimpleFIN access URL.
    /// Caller has already unwrapped it from the sealed column on
    /// <c>feed_connections</c>; the credentials inside this string
    /// must NOT log.</param>
    /// <param name="startDate">UTC seconds-since-epoch. SimpleFIN
    /// returns transactions <c>posted &gt;= startDate</c>. Pass 0
    /// for the first sync (returns the bank's full available
    /// history — typically 90 days).</param>
    /// <remarks>
    /// Always returns a typed envelope, never throws on 403:
    /// <list type="bullet">
    ///   <item><description><c>RequiresReauth=true</c> when SimpleFIN
    ///   returned 403 — the access URL is revoked or expired.
    ///   Caller flips <c>feed_connections.status='needs_reauth'</c>
    ///   and surfaces this to the SPA.</description></item>
    ///   <item><description><c>Accounts</c> populated with the
    ///   parsed account list, enriched with each account's matched
    ///   <see cref="SimpleFinConnection"/> display fields.</description></item>
    ///   <item><description><c>Errors</c> populated from the
    ///   v2 <c>errlist[]</c> — non-fatal, per-account / per-connection
    ///   problems the SPA should display alongside the success
    ///   summary.</description></item>
    /// </list>
    /// Other non-2xx responses throw <see cref="SimpleFinException"/>
    /// so the endpoint surfaces a typed 422.
    /// </remarks>
    public async Task<SimpleFinSyncResponse> GetAccountsWithTransactionsAsync(
        string accessUrl,
        long startDate,
        IReadOnlyList<string>? accountIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessUrl);

        // SimpleFIN v2 supports per-account filtering via repeated
        // `&account=<id>` query parameters (slice 2c.3). Slice 2c.3
        // uses this for the per-account sync entry point so the
        // bank returns only the requested account(s) — less wire
        // data, faster response. Null / empty list = pull all
        // accounts on the connection (the default per-connection
        // sync path).
        var query = new StringBuilder($"?version=2&start-date={startDate}");
        if (accountIds is { Count: > 0 })
        {
            foreach (var id in accountIds)
            {
                query.Append("&account=");
                query.Append(Uri.EscapeDataString(id));
            }
        }
        var uri = BuildAccountsUri(accessUrl, query.ToString());
        using var request = new HttpRequestMessage(HttpMethod.Get, uri.NoCredentials);
        request.Headers.Authorization = uri.AuthHeader;

        var response = await _http.SendAsync(request, cancellationToken)
                                  .ConfigureAwait(false);

        // Defensive: 403 is a distinct outcome — the access URL is
        // revoked / expired. NOT an exception; caller flips the
        // connection to needs_reauth and tells the user to
        // re-generate a setup token.
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new SimpleFinSyncResponse(
                Array.Empty<SimpleFinAccount>(),
                Array.Empty<SimpleFinError>(),
                RequiresReauth: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SimpleFinException(
                $"SimpleFIN /accounts fetch failed ({(int)response.StatusCode} " +
                $"{response.StatusCode}). The access URL may need re-auth.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                                                       .ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                                           .ConfigureAwait(false);

        // v2 top-level shape: { connections[], errlist[], accounts[] }.
        // Build the connections lookup first so account parsing can
        // enrich each account with its matched connection's display
        // fields (Name → OrgName, OrgId → OrgKey).
        var connections = ParseConnections(json.RootElement);
        var errors = ParseErrors(json.RootElement);
        var accounts = ParseAccounts(json.RootElement, connections);

        return new SimpleFinSyncResponse(accounts, errors, RequiresReauth: false);
    }

    private static IReadOnlyDictionary<string, SimpleFinConnection> ParseConnections(
        JsonElement root)
    {
        var map = new Dictionary<string, SimpleFinConnection>(StringComparer.Ordinal);
        if (!root.TryGetProperty("connections", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return map;
        }
        foreach (var c in arr.EnumerateArray())
        {
            var connId = ReadString(c, "conn_id");
            if (connId is null) continue;
            map[connId] = new SimpleFinConnection(
                ConnId: connId,
                Name: ReadString(c, "name") ?? string.Empty,
                OrgId: ReadString(c, "org_id") ?? string.Empty,
                OrgUrl: ReadString(c, "org_url"),
                SfinUrl: ReadString(c, "sfin_url") ?? string.Empty);
        }
        return map;
    }

    private static IReadOnlyList<SimpleFinError> ParseErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errlist", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SimpleFinError>();
        }
        var errors = new List<SimpleFinError>(arr.GetArrayLength());
        foreach (var e in arr.EnumerateArray())
        {
            var code = ReadString(e, "code");
            var msg = ReadString(e, "msg");
            if (code is null || msg is null) continue;
            errors.Add(new SimpleFinError(
                Code: code,
                Msg: msg,
                ConnId: ReadString(e, "conn_id"),
                AccountId: ReadString(e, "account_id")));
        }
        return errors;
    }

    private static IReadOnlyList<SimpleFinAccount> ParseAccounts(
        JsonElement root,
        IReadOnlyDictionary<string, SimpleFinConnection> connections)
    {
        if (!root.TryGetProperty("accounts", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SimpleFinAccount>();
        }
        var results = new List<SimpleFinAccount>(arr.GetArrayLength());
        foreach (var account in arr.EnumerateArray())
        {
            var parsed = ParseAccount(account, connections);
            if (parsed is not null) results.Add(parsed);
        }
        return results;
    }

    private static SimpleFinAccount? ParseAccount(
        JsonElement account,
        IReadOnlyDictionary<string, SimpleFinConnection> connections)
    {
        var id = ReadString(account, "id");
        if (id is null) return null;

        // v2: conn_id is a top-level scalar on the account (the
        // pre-v2 nested `org` object is gone). Match it against the
        // connections dictionary to enrich display fields; tolerate
        // the join failing (degraded mode — account renders with
        // null OrgName + OrgKey rather than disappearing).
        var connId = ReadString(account, "conn_id") ?? string.Empty;
        string? orgName = null;
        string? orgKey = null;
        if (connections.TryGetValue(connId, out var matched))
        {
            orgName = string.IsNullOrEmpty(matched.Name) ? null : matched.Name;
            orgKey = string.IsNullOrEmpty(matched.OrgId) ? null : matched.OrgId;
        }

        var name = ReadString(account, "name") ?? string.Empty;
        var currency = ReadString(account, "currency");
        var balance = ReadDecimalString(account, "balance");
        // v2 hyphenated keys (not snake_case). `balance-date` is the
        // UTC unix seconds the balance was as-of; `available-balance`
        // is the optional intra-day balance.
        var balanceDate = ReadInt64(account, "balance-date");
        var availableBalance = ReadDecimalString(account, "available-balance");

        var transactions = new List<SimpleFinTransaction>();
        if (account.TryGetProperty("transactions", out var txns)
            && txns.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in txns.EnumerateArray())
            {
                var parsed = ParseTransaction(t);
                if (parsed is not null) transactions.Add(parsed);
            }
        }

        return new SimpleFinAccount(
            Id: id,
            ConnId: connId,
            OrgName: orgName,
            OrgKey: orgKey,
            Name: name,
            Currency: currency,
            Balance: balance,
            BalanceDateUnix: balanceDate,
            AvailableBalance: availableBalance,
            Transactions: transactions,
            // ADR-0031 follow-up: verbatim account JSON, including
            // any holdings[] / institution-specific fields we don't
            // model. The orchestrator stores this on the directory
            // row for diagnostic / future-iteration purposes.
            RawJson: account.GetRawText());
    }

    private static SimpleFinTransaction? ParseTransaction(JsonElement txn)
    {
        var id = ReadString(txn, "id");
        if (id is null) return null;
        var posted = ReadInt64(txn, "posted") ?? 0;
        // v2: `transacted_at` (snake_case) is the optional user-side
        // transaction date — prefer it for the user-visible date
        // when present; fall back to `posted` upstream.
        var transactedAt = ReadInt64(txn, "transacted_at");
        var amount = ReadDecimalString(txn, "amount") ?? 0m;
        // SimpleFIN v2: `payee` is the cleaned merchant / counterparty
        // name; `description` is the raw bank-format text. Capture
        // both — the orchestrator maps payee→txn_headers.payee and
        // description→txn_headers.memo (previously we put description
        // into both, dropping the cleaner payee).
        var payee = ReadString(txn, "payee");
        var description = ReadString(txn, "description");
        var pending = txn.TryGetProperty("pending", out var pd)
            && pd.ValueKind == JsonValueKind.True;
        return new SimpleFinTransaction(
            Id: id,
            PostedUnix: posted,
            TransactedAtUnix: transactedAt,
            Amount: amount,
            Payee: payee,
            Description: description,
            Pending: pending,
            // ADR-0031 follow-up: capture the verbatim transaction
            // JSON before any field projection. This preserves
            // anything SimpleFIN sends that we don't yet model
            // (extra blocks, institution-specific fields, etc.).
            // The orchestrator stores this on the inserted
            // txn_headers row for classifier-iteration debugging.
            RawJson: txn.GetRawText());
    }

    // -----------------------------------------------------------------
    // JSON helpers — uniform null-tolerant reads. Centralised so we
    // don't sprinkle 12 TryGetProperty / ValueKind == String boilerplate
    // blocks across the parsers above.
    // -----------------------------------------------------------------

    private static string? ReadString(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static long? ReadInt64(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt64(out var parsed) ? parsed : null;
    }

    private static decimal? ReadDecimalString(JsonElement el, string property)
    {
        // SimpleFIN v2 serialises money as a JSON string (preserves
        // the bank's exact two-decimal value across the wire — JSON
        // numbers go through float in some clients). We parse with
        // InvariantCulture so a ',' decimal locale doesn't drop the
        // value silently.
        if (!el.TryGetProperty(property, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return decimal.TryParse(
            v.GetString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : null;
    }

    // -----------------------------------------------------------------
    // URL plumbing — split the embedded Basic-auth credentials out of
    // the SimpleFIN access URL into an Authorization header. The URL
    // we hand to HttpClient must not contain credentials in the path
    // (some HTTP stacks strip them, others log them — neither is OK).
    // -----------------------------------------------------------------

    internal record struct AccessUri(Uri NoCredentials, AuthenticationHeaderValue AuthHeader);

    internal static AccessUri BuildAccountsUri(string accessUrl, string querySuffix)
    {
        // SimpleFIN's `/accounts` endpoint hangs off the access URL —
        // e.g. https://user:pass@bridge.simplefin.org/foo/accounts
        // The access URL itself is the base path; we append `/accounts`
        // and any query string.
        var parsed = new Uri(accessUrl, UriKind.Absolute);
        var userInfo = parsed.UserInfo;
        if (string.IsNullOrEmpty(userInfo) || !userInfo.Contains(':', StringComparison.Ordinal))
        {
            throw new SimpleFinException(
                "Access URL is missing the expected Basic-auth credentials " +
                "in its userinfo component.");
        }
        var basicAuth = Convert.ToBase64String(Encoding.ASCII.GetBytes(userInfo));
        var auth = new AuthenticationHeaderValue("Basic", basicAuth);

        var path = parsed.AbsolutePath.TrimEnd('/') + "/accounts";
        var sanitized = new UriBuilder(parsed) { UserName = string.Empty, Password = string.Empty, Path = path };
        var combined = new Uri(sanitized.Uri.AbsoluteUri + querySuffix, UriKind.Absolute);
        return new AccessUri(combined, auth);
    }

    private static string DecodeBase64Url(string base64Url)
    {
        // SimpleFIN's setup token is a base64URL (no padding, '-' and
        // '_' alphabet). Convert to plain base64 before
        // System.Convert can decode it.
        var s = base64Url.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Base64url string length is invalid.");
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
