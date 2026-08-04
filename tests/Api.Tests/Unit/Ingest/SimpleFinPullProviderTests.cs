using System.Globalization;
using System.Net;
using System.Text;

using Coffer.Api.Crypto;
using Coffer.Api.Db.Entities;
using Coffer.Api.Ingest;
using Coffer.Api.Ingest.SimpleFin;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// Unit tests for the SimpleFIN pull provider translation layer
/// (ADR-0031 Phase 2). The provider is a pure translator — these
/// tests pin down the SimpleFIN-side wire shape → ingest-neutral
/// shape conversion and the smart start-date math without touching
/// the network or the DB.
/// </summary>
public sealed class SimpleFinPullProviderTests
{
    [Fact]
    public void ProviderKey_is_simplefin()
    {
        var sut = NewProvider(_ => OkResponse(EmptyAccountsJson));
        Assert.Equal("simplefin", sut.ProviderKey);
        Assert.Equal(SimpleFinPullProvider.Key, sut.ProviderKey);
    }

    [Fact]
    public async Task PullAsync_requests_full_window_when_no_mapped_accounts()
    {
        long? capturedStartUnix = null;
        var sut = NewProvider(req =>
        {
            capturedStartUnix = ParseStartDate(req);
            return OkResponse(EmptyAccountsJson);
        });

        await sut.PullAsync(
            NewContext(mapped: Array.Empty<MappedAccountWatermark>()),
            CancellationToken.None);

        // Floor = now - 90d + 1d safety margin. Assert within a
        // 5-minute window to absorb the elapsed time between the
        // provider's `DateTime.UtcNow` and the test's clock read.
        var expectedFloor = DateTimeOffset.UtcNow
            .AddDays(-90).AddDays(1).ToUnixTimeSeconds();
        Assert.NotNull(capturedStartUnix);
        Assert.InRange(
            capturedStartUnix!.Value,
            expectedFloor - 300,
            expectedFloor + 300);
    }

    [Fact]
    public async Task PullAsync_widens_to_earliest_watermark_minus_overlap()
    {
        // Two mapped accounts, watermarks 30d and 5d ago. The
        // provider picks MIN(30d - 7d, 5d - 7d) = 30d - 7d = 37d ago.
        long? capturedStartUnix = null;
        var sut = NewProvider(req =>
        {
            capturedStartUnix = ParseStartDate(req);
            return OkResponse(EmptyAccountsJson);
        });

        var now = DateTime.UtcNow;
        var mapped = new[]
        {
            new MappedAccountWatermark(
                LedgerAccountId: Guid.NewGuid(),
                ExternalId: "ext-a",
                LastSyncedAt: now.AddDays(-30)),
            new MappedAccountWatermark(
                LedgerAccountId: Guid.NewGuid(),
                ExternalId: "ext-b",
                LastSyncedAt: now.AddDays(-5)),
        };

        await sut.PullAsync(
            NewContext(mapped: mapped),
            CancellationToken.None);

        var expected = new DateTimeOffset(now.AddDays(-37), TimeSpan.Zero)
            .ToUnixTimeSeconds();
        Assert.NotNull(capturedStartUnix);
        Assert.InRange(
            capturedStartUnix!.Value,
            expected - 300,
            expected + 300);
    }

    [Fact]
    public async Task PullAsync_clamps_old_watermark_to_floor()
    {
        // Watermark 200 days ago + 7-day overlap = 207d, but the
        // 90d-1d floor wins.
        long? capturedStartUnix = null;
        var sut = NewProvider(req =>
        {
            capturedStartUnix = ParseStartDate(req);
            return OkResponse(EmptyAccountsJson);
        });

        var mapped = new[]
        {
            new MappedAccountWatermark(
                LedgerAccountId: Guid.NewGuid(),
                ExternalId: "ext-old",
                LastSyncedAt: DateTime.UtcNow.AddDays(-200)),
        };

        await sut.PullAsync(
            NewContext(mapped: mapped),
            CancellationToken.None);

        var floor = DateTimeOffset.UtcNow
            .AddDays(-90).AddDays(1).ToUnixTimeSeconds();
        Assert.NotNull(capturedStartUnix);
        Assert.InRange(
            capturedStartUnix!.Value,
            floor - 300,
            floor + 300);
    }

    [Fact]
    public async Task PullAsync_returns_RequiresReauth_when_sfin_returns_403()
    {
        var sut = NewProvider(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await sut.PullAsync(
            NewContext(mapped: Array.Empty<MappedAccountWatermark>()),
            CancellationToken.None);

        Assert.True(result.RequiresReauth);
        Assert.Empty(result.Accounts);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PullAsync_translates_account_and_transaction_fields()
    {
        var sut = NewProvider(_ => OkResponse(SampleOneAccountJson));

        var result = await sut.PullAsync(
            NewContext(mapped: Array.Empty<MappedAccountWatermark>()),
            CancellationToken.None);

        Assert.False(result.RequiresReauth);
        Assert.Empty(result.Errors);
        var account = Assert.Single(result.Accounts);
        Assert.Equal("acct-1", account.ExternalId);
        Assert.Equal("Checking", account.Name);
        Assert.Equal("Westgate", account.OrgName);
        Assert.Equal("wf-org-id", account.OrgKey);
        Assert.Equal("USD", account.Currency);
        Assert.Equal(1000.50m, account.Balance);

        var txn = Assert.Single(account.Transactions);
        Assert.Equal("fitid-1", txn.ExternalId);
        Assert.Equal(-42.50m, txn.Amount);
        Assert.Equal("Coffee Shop", txn.Description);
        Assert.False(txn.Pending);
        // PostedUnix 1747900800 = 2025-05-22 08:00:00 UTC
        Assert.Equal(2025, txn.PostedAt.Year);
        Assert.Equal(5, txn.PostedAt.Month);
        Assert.Equal(22, txn.PostedAt.Day);
        // transacted_at omitted in JSON → falls back to null
        // because the provider only sets TransactedAt when distinct
        // from PostedUnix.
        Assert.Null(txn.TransactedAt);
    }

    [Fact]
    public async Task PullAsync_defensively_narrows_to_AccountIdFilter()
    {
        // SimpleFIN returned two accounts; we only asked for one
        // via filter. Provider drops the extra.
        var sut = NewProvider(_ => OkResponse(SampleTwoAccountsJson));

        var result = await sut.PullAsync(
            NewContext(
                mapped: Array.Empty<MappedAccountWatermark>(),
                accountIdFilter: "acct-1"),
            CancellationToken.None);

        var account = Assert.Single(result.Accounts);
        Assert.Equal("acct-1", account.ExternalId);
    }

    // ----- helpers -----

    private static SimpleFinPullProvider NewProvider(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var http = new HttpClient(new DelegateHandler(handler));
        var client = new SimpleFinClient(http);
        // Real LedgerKeyService — no need to mock crypto for the
        // provider's behavior; we seal a test plaintext under a
        // test master key in NewContext().
        var masterKey = new MasterKey(new byte[32], id: "v1");
        var keys = new LedgerKeyService(masterKey);
        return new SimpleFinPullProvider(client, keys);
    }

    private static PullContext NewContext(
        IReadOnlyList<MappedAccountWatermark> mapped,
        string? accountIdFilter = null)
    {
        var masterKey = new MasterKey(new byte[32], id: "v1");
        var keys = new LedgerKeyService(masterKey);
        var wrappedLek = keys.CreateWrappedLek();
        // Seal an arbitrary access URL — the test handler ignores
        // it, but the provider goes through the decrypt path.
        var ciphertext = keys.Seal(
            wrappedLek,
            Encoding.UTF8.GetBytes("https://user:pw@bridge.simplefin.org/simplefin"));

        var conn = new FeedConnectionRow
        {
            Id = Guid.NewGuid(),
            LedgerId = Guid.NewGuid(),
            Provider = "simplefin",
            AccessUrlCiphertext = ciphertext,
            CreatedAt = DateTime.UtcNow,
        };

        return new PullContext(
            Connection: conn,
            LedgerWrappedLek: wrappedLek,
            MappedAccounts: mapped,
            AccountIdFilter: accountIdFilter);
    }

    private static HttpResponseMessage OkResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static long ParseStartDate(HttpRequestMessage req)
    {
        var uri = req.RequestUri ?? throw new InvalidOperationException("no uri");
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var startStr = query["start-date"] ?? throw new InvalidOperationException("no start-date");
        // Invariant: a wire-format epoch seconds value, not a localised number.
        return long.Parse(startStr, CultureInfo.InvariantCulture);
    }

    private const string EmptyAccountsJson = """
        {"errlist":[],"connections":[],"accounts":[]}
        """;

    private const string SampleOneAccountJson = """
        {
          "errlist": [],
          "connections": [
            { "conn_id": "conn-1", "name": "Westgate",
              "org_id": "wf-org-id", "org_url": null,
              "sfin_url": "https://bridge.simplefin.org/simplefin/conn-1" }
          ],
          "accounts": [
            {
              "id": "acct-1",
              "conn_id": "conn-1",
              "name": "Checking",
              "currency": "USD",
              "balance": "1000.50",
              "balance-date": null,
              "available-balance": null,
              "transactions": [
                {
                  "id": "fitid-1",
                  "posted": 1747900800,
                  "amount": "-42.50",
                  "description": "Coffee Shop",
                  "pending": false
                }
              ]
            }
          ]
        }
        """;

    private const string SampleTwoAccountsJson = """
        {
          "errlist": [],
          "connections": [
            { "conn_id": "conn-1", "name": "Westgate",
              "org_id": "wf-org-id", "org_url": null,
              "sfin_url": "https://bridge.simplefin.org/simplefin/conn-1" }
          ],
          "accounts": [
            {
              "id": "acct-1",
              "conn_id": "conn-1",
              "name": "Checking",
              "currency": "USD",
              "balance": "100.00",
              "balance-date": null,
              "available-balance": null,
              "transactions": []
            },
            {
              "id": "acct-2",
              "conn_id": "conn-1",
              "name": "Savings",
              "currency": "USD",
              "balance": "200.00",
              "balance-date": null,
              "available-balance": null,
              "transactions": []
            }
          ]
        }
        """;

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
