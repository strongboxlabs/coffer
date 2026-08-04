using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>POST /api/ledgers/{ledgerId}/transactions</c> — manual create
/// of a transaction with one or more postings (ADR-0025). Verifies
/// the happy paths (single posting + multi-split), the per-posting
/// validation (zero amount, self-posting, missing counterparty),
/// the empty-list rejection, and the cross-ledger guards.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PostCreateTransactionTests
{
    private readonly PostgresFixture _fixture;

    public PostCreateTransactionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    private async Task<Seed> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var toiletries = await ledger.AddCategoryAsync("toiletries");
        return new Seed(ledger, bank.Id, groceries.Id, toiletries.Id);
    }

    private sealed record Seed(
        SyntheticLedger Ledger, Guid BankId, Guid GroceriesId, Guid ToiletriesId);

    [Fact]
    public async Task Post_creates_a_single_posting_header()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var postedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = "Whole Foods",
                Memo = "weekly",
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        CounterpartyAccountId = seed.GroceriesId,
                        Amount = -42.50m,
                    },
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();
        Assert.NotEqual(Guid.Empty, headerId);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.Equal("manual", header.Origin);
        Assert.Equal("Whole Foods", header.Payee);
        Assert.Equal(postedAt, header.PostedAt);
        // is_user_defined was dropped in mig 109; the equivalent
        // invariant (this is a Coffer-native manual entry, not an
        // imported row) is asserted via Origin='manual' above plus
        // ExternalId being null below.
        Assert.Null(header.ExternalId);

        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToListAsync();
        Assert.Equal(2, legs.Count);
        var bankLeg = legs.Single(l => l.AccountId == seed.BankId);
        var groceriesLeg = legs.Single(l => l.AccountId == seed.GroceriesId);
        Assert.Equal(-42.50m, bankLeg.Amount);
        Assert.Equal(42.50m, groceriesLeg.Amount);
        Assert.Equal(0, bankLeg.PostingIndex);
        Assert.Equal(0, groceriesLeg.PostingIndex);
        Assert.Equal(0m, bankLeg.Amount + groceriesLeg.Amount);
    }

    [Fact]
    public async Task Post_persists_check_number_on_header()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Plumber",
                CheckNumber = "1042",
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -250m },
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == headerId);
        Assert.Equal("1042", header.CheckNumber);
    }

    [Fact]
    public async Task Post_creates_a_multi_split_with_three_postings()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Bulk Mart",
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -60m, LegMemo = "groceries" },
                    new TransactionPosting { CounterpartyAccountId = seed.ToiletriesId, Amount = -40m, LegMemo = "toiletries" },
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -25m, LegMemo = "snacks" },
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex)
            .ThenBy(l => l.AccountId == seed.BankId ? 0 : 1)
            .ToListAsync();
        Assert.Equal(6, legs.Count);

        // posting_index 0/1/2 each appear twice (one source + one counterparty).
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, legs.Select(l => l.PostingIndex).ToArray());

        // Each posting sums to zero on its (source, counterparty) pair.
        foreach (var byIndex in legs.GroupBy(l => l.PostingIndex))
        {
            Assert.Equal(0m, byIndex.Sum(l => l.Amount));
        }

        // Source-side total = sum of negatives = -125. Net of all 6 legs = 0.
        var bankLegs = legs.Where(l => l.AccountId == seed.BankId).ToList();
        Assert.Equal(3, bankLegs.Count);
        Assert.Equal(-125m, bankLegs.Sum(l => l.Amount));
    }

    [Fact]
    public async Task Post_rejects_empty_postings_list()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BankId,
                Postings = Array.Empty<TransactionPosting>(),
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-postings-empty",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_accepts_zero_amount_posting_in_a_multi_split()
    {
        // Paycheck splits routinely carry $0 line items (Medicare
        // Surtax / 401(k) cap / bonus accrual placeholders) that
        // flicker positive in some pay periods. The DB has no
        // zero-amount constraint and the editor (after the validator
        // was relaxed) allows it; the API must too, otherwise the
        // bank-feed merge into a paycheck target round-trips into a
        // 422.
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                Payee = "Paycheck",
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -60m, LegMemo = "Gross" },
                    new TransactionPosting { CounterpartyAccountId = seed.ToiletriesId, Amount = 0m,  LegMemo = "Surtax placeholder" },
                },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var headerId = doc.RootElement.GetProperty("headerId").GetGuid();

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToListAsync();
        // 2 postings × 2 legs each = 4. The zero-amount posting
        // still emits its source + counterparty pair.
        Assert.Equal(4, legs.Count);
        Assert.Equal(2, legs.Count(l => l.Amount == 0m));
    }

    [Fact]
    public async Task Post_rejects_self_posting()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.BankId, Amount = -10m },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-posting-self",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_rejects_missing_counterparty()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting
                    {
                        // CounterpartyAccountId omitted → Guid.Empty
                        Amount = -10m,
                    },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-posting-counterparty-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_rejects_missing_source_account()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                // SourceAccountId omitted → Guid.Empty
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -10m },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-account-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_rejects_missing_posted_at()
    {
        var seed = await SeedAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                // PostedAt omitted
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -10m },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-posted-at-required",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        var seed = await SeedAsync();
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, otherLedger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -10m },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_returns_422_account_not_in_ledger_when_counterparty_belongs_elsewhere()
    {
        var seed = await SeedAsync();
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);
        var otherCategory = await otherLedger.AddCategoryAsync("foreign-cat");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.PostAsJsonAsync(
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = new DateTime(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc),
                SourceAccountId = seed.BankId,
                Postings = new[]
                {
                    // One posting in this ledger; another with foreign counterparty.
                    new TransactionPosting { CounterpartyAccountId = seed.GroceriesId, Amount = -10m },
                    new TransactionPosting { CounterpartyAccountId = otherCategory.Id, Amount = -5m },
                },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("account-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }
}
