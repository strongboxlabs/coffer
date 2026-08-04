using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Investment-side merge (the brokerage equivalent of the bank merge). A fresh,
/// needs-review investment row folds into a settled candidate: the candidate is
/// the surviving winner (stamped is_merge_winner + adopts the loser's date), the
/// loser is stamped is_merged_into and its shares drop out of holdings.
///
/// Matching is by the security-leg's signed principal amount (stable across the
/// share-count rounding that differs between feeds), same holdings-sibling
/// account + security, within ±7 effective days. The holdings drop verifies
/// migration 163 (recompute excludes is_merged_into) + the merge branch's
/// explicit recompute trigger. Atomic per-test ledger.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvestmentMergeTests
{
    private readonly PostgresFixture _fixture;

    public InvestmentMergeTests(PostgresFixture fixture) => _fixture = fixture;

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    private static async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        DateTime postedAt, decimal shares, decimal amount, decimal price)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = shares,
                Price = price,
                Amount = amount,
            });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    private async Task MarkNeedsReviewAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE txn_headers SET needs_review = TRUE WHERE id = {headerId}");
    }

    [Fact]
    public async Task MergeCandidates_match_by_principal_despite_different_shares()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TDLM", ticker: "TDLM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
        // Settled winner: 97.301 sh, $1,293.13.
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.301m, amount: 1293.13m, price: 13.29m);
        // Same principal, DIFFERENT shares (feed-rounding drift), same date.
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.374m, amount: 1293.13m, price: 13.28m);
        // A decoy: same security, DIFFERENT principal — must NOT match.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 50m, amount: 500.00m, price: 10m);
        // A decoy: same principal but 20 days out of the ±7d window — must NOT match.
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            date.AddDays(20), shares: 97.5m, amount: 1293.13m, price: 13.26m);

        await MarkNeedsReviewAsync(loser);

        var candidates = await client.GetFromJsonAsync<List<InvestmentMergeCandidateDto>>(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}/merge-candidates");

        Assert.NotNull(candidates);
        var only = Assert.Single(candidates!);
        Assert.Equal(winner, only.HeaderId);
        Assert.Equal("buy", only.Action);
        Assert.Equal("TDLM", only.SecurityTicker);
        Assert.Equal(1293.13m, only.Amount);
        Assert.Equal(97.301m, only.Shares);
    }

    [Fact]
    public async Task Merge_folds_loser_into_winner_and_drops_its_shares_from_holdings()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TDLM", ticker: "TDLM");
        var holdingsAccountId = brokerage.HoldingsAccountId!.Value;

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.301m, amount: 1293.13m, price: 13.29m);
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.374m, amount: 1293.13m, price: 13.28m);
        await MarkNeedsReviewAsync(loser);

        // Both buys are in holdings before the merge (97.301 + 97.374).
        await using (var db0 = _fixture.NewDbContext())
        {
            var qty0 = await db0.Holdings.AsNoTracking()
                .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
                .Select(h => h.Quantity).SingleAsync();
            Assert.Equal(97.301m + 97.374m, qty0);
        }

        // Fold the loser into the winner (merge-only PATCH; account_id → survivor entry).
        var resp = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}?account_id={brokerage.Id}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = winner });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var db = _fixture.NewDbContext();
        // Loser → merged into winner; winner → merge winner.
        var loserRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == loser);
        var winnerRow = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winner);
        Assert.Equal(winner, loserRow.IsMergedInto);
        Assert.True(winnerRow.IsMergeWinner);

        // Holdings now reflect ONLY the winner's shares — the merged loser dropped out
        // (mig 163 recompute + the merge branch's explicit trigger).
        var qty = await db.Holdings.AsNoTracking()
            .Where(h => h.AccountId == holdingsAccountId && h.SecurityId == securityId)
            .Select(h => h.Quantity).SingleAsync();
        Assert.Equal(97.301m, qty);
    }

    [Fact]
    public async Task Merge_rejects_self_and_settled_editor_with_422()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TDLM", ticker: "TDLM");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var date = new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc);
        var winner = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.301m, amount: 1293.13m, price: 13.29m);
        var loser = await BuyAsync(client, ledger, brokerage.Id, securityId,
            date, shares: 97.374m, amount: 1293.13m, price: 13.28m);
        await MarkNeedsReviewAsync(loser);

        // Self-merge: rejected.
        var self = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{loser}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = loser });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        using (var doc = JsonDocument.Parse(await self.Content.ReadAsStringAsync()))
            Assert.Equal("merge-source-invalid", doc.RootElement.GetProperty("code").GetString());

        // Editor is a SETTLED row (winner, not needs_review) → can't be a loser.
        var settledEditor = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{winner}",
            new PatchInvestmentTransactionRequest { MergeFromHeaderId = loser });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, settledEditor.StatusCode);
    }
}
