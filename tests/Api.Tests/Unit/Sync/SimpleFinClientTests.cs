using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Tests.Unit.Sync;

/// <summary>
/// Unit tests for the SimpleFIN HTTP gateway. A canned
/// <see cref="HttpMessageHandler"/> stands in for the network so
/// these run without touching the real SimpleFIN Bridge — the
/// asserts pin protocol shape (POST claim, Basic-auth in header
/// not path, base64url decode, error → typed exception).
/// </summary>
public sealed class SimpleFinClientTests
{
    /// <summary>Build a <see cref="SimpleFinClient"/> backed by an
    /// in-memory <see cref="HttpMessageHandler"/> that calls the
    /// supplied delegate for each request.</summary>
    private static SimpleFinClient ClientFor(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new DelegateHandler(handler));
        return new SimpleFinClient(http);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) { _handler = handler; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }

    private static string SetupTokenFor(string claimUrl)
    {
        // SimpleFIN's setup token is the claim URL base64url-encoded.
        var bytes = Encoding.UTF8.GetBytes(claimUrl);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    [Fact]
    public async Task ExchangeSetupTokenAsync_decodes_token_and_POSTs_claim_url()
    {
        const string claimUrl = "https://bridge.simplefin.org/simplefin/claim/abc123";
        const string accessUrl = "https://user:pass@bridge.simplefin.org/simplefin/access/abc";

        HttpRequestMessage? captured = null;
        var client = ClientFor(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(accessUrl),
            };
        });

        var result = await client.ExchangeSetupTokenAsync(SetupTokenFor(claimUrl));

        Assert.Equal(accessUrl, result);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(claimUrl, captured.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ExchangeSetupTokenAsync_throws_typed_exception_on_malformed_base64()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = await Assert.ThrowsAsync<SimpleFinException>(() =>
            client.ExchangeSetupTokenAsync("not!base64url!!!"));
        Assert.Contains("base64url", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExchangeSetupTokenAsync_throws_on_non_2xx_response()
    {
        // SimpleFIN returns 403 when the token is already-consumed
        // or expired. Surfaced as SimpleFinException so the endpoint
        // maps it to 422 with a user-facing "generate fresh token"
        // message rather than an opaque 500.
        var client = ClientFor(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<SimpleFinException>(() =>
            client.ExchangeSetupTokenAsync(SetupTokenFor("https://bridge.simplefin.org/x")));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task ExchangeSetupTokenAsync_throws_on_empty_response_body()
    {
        var client = ClientFor(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty),
            });
        await Assert.ThrowsAsync<SimpleFinException>(() =>
            client.ExchangeSetupTokenAsync(SetupTokenFor("https://bridge.simplefin.org/x")));
    }

    [Fact]
    public async Task GetInstitutionNameAsync_returns_first_connection_name_and_uses_Basic_auth_header()
    {
        // SimpleFIN access URL: https://user:pass@host/path
        // The probe must use Authorization: Basic, NOT carry the
        // credentials in the URL path — the latter has been a source
        // of leak-via-logs across the .NET HTTP stack historically.
        // v2.0.0: institution name lives in top-level connections[]
        // (the pre-v2 nested account.org object is gone).
        HttpRequestMessage? captured = null;
        var client = ClientFor(req =>
        {
            captured = req;
            const string body = """
                {
                  "connections": [
                    {
                      "conn_id": "c-1",
                      "name": "First National Test Bank",
                      "org_id": "fnb",
                      "sfin_url": "https://sfin/fnb"
                    }
                  ],
                  "errlist": [],
                  "accounts": []
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        });

        var name = await client.GetInstitutionNameAsync(
            "https://probe-user:probe-pass@bridge.simplefin.org/simplefin/access/abc");

        Assert.Equal("First National Test Bank", name);
        Assert.NotNull(captured);
        Assert.Contains("version=2", captured!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-user", captured.RequestUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("probe-pass", captured.RequestUri.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(captured.Headers.Authorization);
        Assert.Equal("Basic", captured.Headers.Authorization!.Scheme);
        var decoded = Encoding.ASCII.GetString(
            Convert.FromBase64String(captured.Headers.Authorization.Parameter!));
        Assert.Equal("probe-user:probe-pass", decoded);
    }

    [Fact]
    public async Task GetInstitutionNameAsync_returns_null_on_probe_failure()
    {
        // Network failure / 500 / malformed JSON — none of these
        // should be fatal at connect time. Wizard renders "SimpleFIN"
        // as the fallback name until first sync fills it in.
        var client = ClientFor(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var name = await client.GetInstitutionNameAsync(
            "https://u:p@bridge.simplefin.org/access/x");
        Assert.Null(name);
    }

    [Fact]
    public void BuildAccountsUri_strips_credentials_from_path_and_builds_Basic_header()
    {
        var result = SimpleFinClient.BuildAccountsUri(
            "https://u:p@bridge.simplefin.org/simplefin/access/abc",
            "?start-date=123");

        Assert.Equal("https://bridge.simplefin.org/simplefin/access/abc/accounts?start-date=123",
            result.NoCredentials.AbsoluteUri);
        Assert.Equal("Basic", result.AuthHeader.Scheme);
        Assert.Equal("u:p",
            Encoding.ASCII.GetString(Convert.FromBase64String(result.AuthHeader.Parameter!)));
    }

    [Fact]
    public void BuildAccountsUri_throws_when_userinfo_is_missing()
    {
        Assert.Throws<SimpleFinException>(() =>
            SimpleFinClient.BuildAccountsUri(
                "https://bridge.simplefin.org/simplefin/access/abc",
                ""));
    }

    [Fact]
    public async Task GetAccountsWithTransactionsAsync_pins_version_2_and_parses_accounts_with_connection_enrichment()
    {
        // v2.0.0 payload shape: three top-level arrays
        // (connections / errlist / accounts). Each account references
        // its connection by conn_id; the client enriches OrgName +
        // OrgKey from the matched connection.
        const string body = """
            {
              "connections": [
                {"conn_id": "c-1", "name": "Test Bank",
                 "org_id": "testbank", "sfin_url": "https://sfin/test"}
              ],
              "errlist": [],
              "accounts": [
                {
                  "id": "sf-acct-1",
                  "conn_id": "c-1",
                  "name": "Checking 4242",
                  "currency": "USD",
                  "balance": "1234.56",
                  "balance-date": 1715000000,
                  "available-balance": "1200.00",
                  "transactions": [
                    {
                      "id": "sf-txn-1",
                      "posted": 1715000000,
                      "transacted_at": 1714900000,
                      "amount": "-12.34",
                      "payee": "Starbucks",
                      "description": "STARBUCKS 12345",
                      "pending": false
                    },
                    {
                      "id": "sf-txn-2",
                      "posted": 1715100000,
                      "amount": "1000.00",
                      "description": "PAYROLL",
                      "pending": true
                    }
                  ]
                },
                {
                  "id": "sf-acct-2",
                  "conn_id": "c-1",
                  "name": "Savings",
                  "currency": "USD",
                  "balance": "5000.00",
                  "transactions": []
                }
              ]
            }
            """;
        HttpRequestMessage? captured = null;
        var client = ClientFor(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        });

        var feed = await client.GetAccountsWithTransactionsAsync(
            "https://u:p@bridge.simplefin.org/access/x", startDate: 0);

        Assert.False(feed.RequiresReauth);
        Assert.Empty(feed.Errors);
        Assert.NotNull(captured);
        Assert.Contains("version=2", captured!.RequestUri!.Query, StringComparison.Ordinal);

        Assert.Equal(2, feed.Accounts.Count);
        var checking = feed.Accounts[0];
        Assert.Equal("sf-acct-1", checking.Id);
        Assert.Equal("c-1", checking.ConnId);
        Assert.Equal("Test Bank", checking.OrgName);
        // OrgKey now comes from connection.org_id, not the deleted
        // org.domain field — that's the v2 fi_id for FITID dedup.
        Assert.Equal("testbank", checking.OrgKey);
        Assert.Equal("Checking 4242", checking.Name);
        Assert.Equal("USD", checking.Currency);
        Assert.Equal(1234.56m, checking.Balance);
        Assert.Equal(1715000000L, checking.BalanceDateUnix);
        Assert.Equal(1200.00m, checking.AvailableBalance);
        Assert.Equal(2, checking.Transactions.Count);

        var firstTxn = checking.Transactions[0];
        Assert.Equal("sf-txn-1", firstTxn.Id);
        Assert.Equal(1715000000, firstTxn.PostedUnix);
        Assert.Equal(1714900000L, firstTxn.TransactedAtUnix);
        Assert.Equal(-12.34m, firstTxn.Amount);
        Assert.Equal("Starbucks", firstTxn.Payee);
        Assert.Equal("STARBUCKS 12345", firstTxn.Description);
        Assert.False(firstTxn.Pending);

        var secondTxn = checking.Transactions[1];
        Assert.Null(secondTxn.TransactedAtUnix);
        Assert.Equal(1000.00m, secondTxn.Amount);
        // No `payee` field in this fixture transaction — Payee
        // tolerates absence (older feeds / non-SimpleFIN sources).
        Assert.Null(secondTxn.Payee);
        Assert.True(secondTxn.Pending);

        // Second account also joins to c-1; no available-balance
        // supplied so it stays null.
        Assert.Equal("Test Bank", feed.Accounts[1].OrgName);
        Assert.Null(feed.Accounts[1].AvailableBalance);
        Assert.Empty(feed.Accounts[1].Transactions);
    }

    [Fact]
    public async Task GetAccountsWithTransactionsAsync_surfaces_403_as_RequiresReauth_not_exception()
    {
        // Defensive-API contract: 403 from SimpleFIN means the
        // access URL is revoked / expired. The client returns a
        // typed RequiresReauth=true envelope so the caller can flip
        // feed_connections.status='needs_reauth' and show the SPA a
        // re-connect CTA. Throwing here would force the endpoint
        // into a generic 422 toast — worse UX, and we'd lose the
        // discriminator the caller needs.
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var feed = await client.GetAccountsWithTransactionsAsync(
            "https://u:p@bridge.simplefin.org/access/x", startDate: 0);
        Assert.True(feed.RequiresReauth);
        Assert.Empty(feed.Accounts);
        Assert.Empty(feed.Errors);
    }

    [Fact]
    public async Task GetAccountsWithTransactionsAsync_throws_typed_exception_on_non_403_non_2xx()
    {
        // 500 / network error / unparseable JSON — anything that
        // isn't 403 or 2xx — still surfaces as SimpleFinException so
        // the endpoint maps it to a 422 with a fix-this message.
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<SimpleFinException>(() =>
            client.GetAccountsWithTransactionsAsync(
                "https://u:p@bridge.simplefin.org/access/x", startDate: 0));
    }

    [Fact]
    public async Task GetAccountsWithTransactionsAsync_parses_errlist_alongside_accounts()
    {
        // v2 partial-failure contract: errlist[] is sibling to
        // accounts[], not a replacement. A clean account list AND
        // a per-connection error message can coexist (e.g. "Bank A
        // OK, Bank B in maintenance"). Surface both verbatim so the
        // SPA shows the partial-failure banner alongside the
        // success counts.
        const string body = """
            {
              "connections": [
                {"conn_id": "c-1", "name": "Test Bank",
                 "org_id": "testbank", "sfin_url": "https://sfin/test"}
              ],
              "errlist": [
                {"code": "fi.maintenance", "msg": "Bank is undergoing maintenance",
                 "conn_id": "c-1"},
                {"code": "auth.mfa_pending", "msg": "User must complete MFA"}
              ],
              "accounts": [
                {"id": "sf-acct-1", "conn_id": "c-1", "name": "Checking",
                 "currency": "USD", "balance": "0.00", "transactions": []}
              ]
            }
            """;
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });

        var feed = await client.GetAccountsWithTransactionsAsync(
            "https://u:p@bridge.simplefin.org/access/x", startDate: 0);

        Assert.False(feed.RequiresReauth);
        Assert.Single(feed.Accounts);
        Assert.Equal(2, feed.Errors.Count);
        Assert.Equal("fi.maintenance", feed.Errors[0].Code);
        Assert.Equal("c-1", feed.Errors[0].ConnId);
        Assert.Null(feed.Errors[0].AccountId);
        Assert.Equal("auth.mfa_pending", feed.Errors[1].Code);
        Assert.Null(feed.Errors[1].ConnId);
    }

    [Fact]
    public async Task GetAccountsWithTransactionsAsync_returns_empty_envelope_when_payload_has_no_accounts()
    {
        var client = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"connections":[],"errlist":[],"accounts":[]}"""),
        });
        var feed = await client.GetAccountsWithTransactionsAsync(
            "https://u:p@bridge.simplefin.org/access/x", startDate: 0);
        Assert.False(feed.RequiresReauth);
        Assert.Empty(feed.Accounts);
        Assert.Empty(feed.Errors);
    }
}
