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
/// <c>PATCH /api/ledgers/{ledgerId}/transactions/{headerId}</c>
/// under ADR-0025. The endpoint applies header overrides AND
/// reshapes the postings list in one atomic transaction. Tests
/// pin the postings reshape semantics (reconcile-by-legId,
/// single↔split conversion, reorder, override-wipe-on-reshape)
/// plus the validation surface and cross-ledger guards.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PatchTransactionTests
{
    private readonly PostgresFixture _fixture;

    public PatchTransactionTests(PostgresFixture fixture)
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

    private static HttpRequestMessage Patch(
        Guid ledgerId, Guid headerId, PatchTransactionRequest body) =>
        new(HttpMethod.Patch,
            $"/api/ledgers/{ledgerId}/transactions/{headerId}")
        { Content = JsonContent.Create(body) };

    private async Task<Seed> SeedSingleAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var toiletries = await ledger.AddCategoryAsync("toiletries");
        var (bankLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, groceries.Id, -10m,
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            payee: "Original Payee");
        var headerId = await ledger.ResolveHeaderIdAsync(bankLegId);
        return new Seed(ledger, headerId, bank.Id, groceries.Id, toiletries.Id, bankLegId);
    }

    private sealed record Seed(
        SyntheticLedger Ledger,
        Guid HeaderId,
        Guid BankId,
        Guid GroceriesId,
        Guid ToiletriesId,
        Guid BankLegId);

    // -- Header-only path (unchanged from pre-ADR-0025) ----------------

    [Fact]
    public async Task Patch_header_fields_only_writes_the_override_row()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var newDate = new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc);
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Payee = "Cleaned-up Payee",
                Memo = "lunch with a friend",
                PostedAt = newDate,
            }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaderOverrides.AsNoTracking()
            .SingleAsync(o => o.HeaderId == seed.HeaderId);
        Assert.Equal("Cleaned-up Payee", row.Payee);
        Assert.Equal("lunch with a friend", row.Memo);
        Assert.Equal(newDate, row.PostedAt);
    }

    [Fact]
    public async Task Patch_merges_into_existing_overrides_preserving_unset_fields()
    {
        var seed = await SeedSingleAsync();
        await seed.Ledger.SetHeaderOverrideAsync(seed.BankLegId, memo: "first memo");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Payee = "second payee" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaderOverrides.AsNoTracking()
            .SingleAsync(o => o.HeaderId == seed.HeaderId);
        Assert.Equal("second payee", row.Payee);
        Assert.Equal("first memo", row.Memo);
    }

    [Fact]
    public async Task Patch_returns_resolved_entry_when_account_id_supplied()
    {
        // The SPA passes ?account_id=<viewing-account> on PATCH so
        // the server can return the freshly-resolved entry for
        // in-place row swap (mutateEntries) without a window
        // refresh. Pins the response shape: status 200 + a kind=txn
        // entry whose `id` is the saved leg id (so the SPA can
        // round-trip a re-edit without losing identity).
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}?account_id={seed.BankId}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest
                {
                    Payee = "Renamed Payee",
                }),
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("txn", doc.RootElement.GetProperty("kind").GetString());
        var txn = doc.RootElement.GetProperty("txn");
        Assert.Equal(seed.BankLegId, txn.GetProperty("id").GetGuid());
        Assert.Equal("Renamed Payee", txn.GetProperty("payee").GetString());
    }

    [Fact]
    public async Task Patch_returns_group_entry_when_single_becomes_split()
    {
        // Pin the most important shape-change: when a single-row
        // PATCH adds postings, the response must be a kind=group
        // entry with the new leg ids — without those the SPA's
        // next re-edit can't reference the new legs by id and the
        // server would treat them as new postings.
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/ledgers/{seed.Ledger.LedgerId}/transactions/{seed.HeaderId}?account_id={seed.BankId}")
            {
                Content = JsonContent.Create(new PatchTransactionRequest
                {
                    Postings = new PatchTransactionPostings
                    {
                        SourceAccountId = seed.BankId,
                        Items = new[]
                        {
                            new TransactionPosting
                            {
                                LegId = seed.BankLegId,
                                CounterpartyAccountId = seed.GroceriesId,
                                Amount = -6m,
                            },
                            new TransactionPosting
                            {
                                CounterpartyAccountId = seed.ToiletriesId,
                                Amount = -4m,
                            },
                        },
                    },
                }),
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("group", doc.RootElement.GetProperty("kind").GetString());
        var legs = doc.RootElement.GetProperty("legs");
        Assert.Equal(2, legs.GetArrayLength());
        // Both legs are on the source account (we asked for the
        // bank-account view) and carry real ids the SPA can echo
        // back on the next edit.
        foreach (var leg in legs.EnumerateArray())
        {
            Assert.NotEqual(Guid.Empty, leg.GetProperty("id").GetGuid());
            Assert.Equal(seed.BankId, leg.GetProperty("accountId").GetGuid());
        }
    }

    [Fact]
    public async Task Patch_persists_check_number_to_override_row()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { CheckNumber = "9981" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaderOverrides.AsNoTracking()
            .SingleAsync(o => o.HeaderId == seed.HeaderId);
        Assert.Equal("9981", row.CheckNumber);
    }

    // -- Postings reshape: edit-in-place ------------------------------

    [Fact]
    public async Task Patch_with_postings_updates_amounts_canonically_on_txn_legs()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.BankId,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = seed.BankLegId,
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -12.50m,
                            LegMemo = "updated",
                        },
                    },
                },
            }));
        // PATCH with postings + an inferred account_id (from
        // postings.SourceAccountId) returns the resolved entry so
        // the SPA can patch the row in-place via mutateEntries.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == seed.HeaderId)
            .ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.Equal(-12.50m, legs.Single(l => l.AccountId == seed.BankId).Amount);
        Assert.Equal(12.50m, legs.Single(l => l.AccountId == seed.GroceriesId).Amount);
    }

    [Fact]
    public async Task Patch_converts_single_to_split_by_adding_postings()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        // Convert the original 1-posting transaction into 3 postings
        // by keeping the existing leg + adding two new ones.
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.BankId,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = seed.BankLegId,
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -4m,
                        },
                        new TransactionPosting
                        {
                            CounterpartyAccountId = seed.ToiletriesId,
                            Amount = -3m,
                        },
                        new TransactionPosting
                        {
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -3m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == seed.HeaderId)
            .OrderBy(l => l.PostingIndex)
            .ToListAsync();
        Assert.Equal(6, legs.Count);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, legs.Select(l => l.PostingIndex).ToArray());
        Assert.Equal(0m, legs.Sum(l => l.Amount));
        // Source side still totals -10 (the conversion preserved the
        // original transaction amount via per-posting splits).
        Assert.Equal(-10m, legs.Where(l => l.AccountId == seed.BankId).Sum(l => l.Amount));
    }

    [Fact]
    public async Task Patch_converts_split_to_single_by_dropping_all_but_one_posting()
    {
        // Seed a 3-posting transaction directly.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var toiletries = await ledger.AddCategoryAsync("toiletries");
        var (originIds, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id,
            new[]
            {
                (groceries.Id, -4m),
                (toiletries.Id, -3m),
                (groceries.Id, -3m),
            },
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Keep only the first posting; drop the other two.
        var response = await client.SendAsync(Patch(ledger.LedgerId, headerId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = originIds[0],
                            CounterpartyAccountId = groceries.Id,
                            Amount = -10m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .ToListAsync();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal(0, l.PostingIndex));
        Assert.Equal(-10m, legs.Single(l => l.AccountId == bank.Id).Amount);
    }

    [Fact]
    public async Task Patch_reorders_postings_by_items_order()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var toiletries = await ledger.AddCategoryAsync("toiletries");
        var (originIds, headerId) = await ledger.AddMultiSplitAsync(
            bank.Id,
            new[] { (groceries.Id, -4m), (toiletries.Id, -3m) },
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // Swap the postings: pre-existing index 1 → new index 0,
        // pre-existing index 0 → new index 1.
        var response = await client.SendAsync(Patch(ledger.LedgerId, headerId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = bank.Id,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = originIds[1],
                            CounterpartyAccountId = toiletries.Id,
                            Amount = -3m,
                        },
                        new TransactionPosting
                        {
                            LegId = originIds[0],
                            CounterpartyAccountId = groceries.Id,
                            Amount = -4m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .OrderBy(l => l.PostingIndex)
            .ThenBy(l => l.Id)
            .ToListAsync();
        // Leg originIds[1] should now be at posting_index 0;
        // originIds[0] at posting_index 1.
        Assert.Equal(0, legs.Single(l => l.Id == originIds[1]).PostingIndex);
        Assert.Equal(1, legs.Single(l => l.Id == originIds[0]).PostingIndex);
    }

    [Fact]
    public async Task Patch_with_postings_drops_existing_leg_overrides()
    {
        var seed = await SeedSingleAsync();
        // Pre-seed an existing override on the leg.
        await using (var seedDb = _fixture.NewDbContext())
        {
            // -99 (no `m` suffix) — the `m` is a C# decimal-literal
            // suffix and not valid in SQL. The interpolation
            // parameterises {seed.BankLegId}; the numeric literal
            // is plain SQL.
            var ledgerId = seed.Ledger.LedgerId;
            await seedDb.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_leg_overrides (leg_id, ledger_id, amount, leg_memo)
                VALUES ({seed.BankLegId}, {ledgerId}, -99, 'old override');");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.BankId,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = seed.BankLegId,
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -7m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        // The stale override is gone — canonical value supersedes.
        Assert.False(await db.TxnLegOverrides
            .AnyAsync(o => o.LegId == seed.BankLegId));
        // Canonical leg now carries the new amount.
        var canonical = await db.TxnLegs.AsNoTracking()
            .SingleAsync(l => l.Id == seed.BankLegId);
        Assert.Equal(-7m, canonical.Amount);
    }

    // -- Validation surface ------------------------------------------

    [Fact]
    public async Task Patch_returns_422_when_no_header_fields_and_no_postings()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-patch-empty",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_returns_422_posting_leg_not_in_header()
    {
        var seed = await SeedSingleAsync();
        var foreignLeg = Guid.NewGuid(); // not in any header

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.BankId,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = foreignLeg,
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -1m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-posting-leg-not-in-header",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_returns_422_source_account_mismatch_when_account_has_no_legs_on_header()
    {
        var seed = await SeedSingleAsync();
        // Add a third account in the ledger that has no legs on
        // the test header — supplying it as sourceAccountId means
        // the SPA is trying to move the transaction across accounts
        // via this endpoint, which we explicitly reject.
        var unrelated = await seed.Ledger.AddBankAccountAsync($"unrelated-{Guid.NewGuid():N}");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = unrelated.Id,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            CounterpartyAccountId = seed.BankId,
                            Amount = -1m,
                        },
                    },
                },
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-source-account-mismatch",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_accepts_zero_amount_in_a_postings_reshape()
    {
        // Companion to PostCreateTransactionTests' positive zero-
        // amount test. Reshaping a single-leg row into a 2-posting
        // split where one posting is $0 (the merge-into-paycheck
        // case) must succeed end-to-end — the validator was relaxed
        // because the editor surfaces real-world paycheck splits
        // with zero placeholder lines (Medicare Surtax / 401(k) cap).
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Postings = new PatchTransactionPostings
                {
                    SourceAccountId = seed.BankId,
                    Items = new[]
                    {
                        new TransactionPosting
                        {
                            LegId = seed.BankLegId,
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = -25m,
                        },
                        new TransactionPosting
                        {
                            CounterpartyAccountId = seed.GroceriesId,
                            Amount = 0m,
                            LegMemo = "placeholder",
                        },
                    },
                },
            }));
        // 200 OK (not 204): the endpoint returns the freshly-
        // resolved register entry when the body's Postings carries
        // a SourceAccountId, so the SPA can patch the row in place.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var legs = await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == seed.HeaderId)
            .ToListAsync();
        // 2 postings × 2 legs = 4. Two of them have amount 0.
        Assert.Equal(4, legs.Count);
        Assert.Equal(2, legs.Count(l => l.Amount == 0m));
    }

    [Fact]
    public async Task Patch_returns_422_ledger_not_visible_when_user_has_no_grant()
    {
        var seed = await SeedSingleAsync();
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, otherLedger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Payee = "should be rejected" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ledger-not-visible",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_returns_422_transaction_not_in_ledger_when_header_belongs_elsewhere()
    {
        var seed = await SeedSingleAsync();
        var otherLedger = await SyntheticLedger.CreateAsync(_fixture);
        var otherBank = await otherLedger.AddBankAccountAsync("checking");
        var otherCategory = await otherLedger.AddCategoryAsync("groceries");
        var (otherLegId, _) = await otherLedger.AddTransactionPairAsync(
            otherBank.Id, otherCategory.Id, -1m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var foreignHeaderId = await otherLedger.ResolveHeaderIdAsync(otherLegId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, foreignHeaderId,
            new PatchTransactionRequest { Payee = "should be rejected" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-not-in-ledger",
            doc.RootElement.GetProperty("code").GetString());
    }

    // -- Approve via PATCH (slice 2c.6a) --------------------------------
    // Replaces the prior dedicated POST /approve endpoint. PATCH with
    // `approve: true` clears needs_review in the same atomic Postgres
    // transaction as any header / postings edits in the same body.

    private async Task SetNeedsReviewAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_headers SET needs_review = true WHERE id = {headerId};");
    }

    [Fact]
    public async Task Patch_with_approve_clears_needs_review()
    {
        var seed = await SeedSingleAsync();
        await SetNeedsReviewAsync(seed.HeaderId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Approve = true }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == seed.HeaderId);
        Assert.False(row.NeedsReview);
    }

    [Fact]
    public async Task Patch_with_approve_and_edits_applies_both_atomically()
    {
        // The typical bank-feed flow: user reviews a needs_review row,
        // sets a category override (via payee/memo edits and / or a
        // postings reshape), and approves — all in one round-trip.
        var seed = await SeedSingleAsync();
        await SetNeedsReviewAsync(seed.HeaderId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Payee = "Whole Foods",
                Approve = true,
            }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == seed.HeaderId);
        var ovr = await db.TxnHeaderOverrides.AsNoTracking()
            .SingleAsync(o => o.HeaderId == seed.HeaderId);
        Assert.False(header.NeedsReview);
        Assert.Equal("Whole Foods", ovr.Payee);
    }

    [Fact]
    public async Task Patch_with_approve_is_idempotent_on_already_approved_row()
    {
        var seed = await SeedSingleAsync();
        // Seeded row defaults to needs_review=false. Approving again
        // is a no-op success, not a 422.
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Approve = true }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var row = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == seed.HeaderId);
        Assert.False(row.NeedsReview);
    }

    [Fact]
    public async Task Patch_with_only_approve_false_returns_422_transaction_patch_empty()
    {
        // Empty-body validation still fires when approve is explicitly
        // false (or absent) AND no other field is supplied.
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Approve = false }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-patch-empty",
            doc.RootElement.GetProperty("code").GetString());
    }

    // -- Tags as first-class (slice 2c.6b) ------------------------------
    // PATCH gains a `tags: string[]` body field. Replace semantics:
    // omitted → untouched, [] → all removed, [...] → set matches.
    // Tag dictionary is per-ledger; unknown names are created on
    // first use; matching is case-insensitive with first-use casing
    // preserved.

    private async Task<IReadOnlyList<string>> CurrentTagsForHeaderAsync(Guid headerId)
    {
        await using var db = _fixture.NewDbContext();
        return await (from p in db.TxnHeaderTags.AsNoTracking()
                      where p.HeaderId == headerId
                      join t in db.Tags.AsNoTracking() on p.TagId equals t.Id
                      orderby t.Name
                      select t.Name)
            .ToListAsync();
    }

    [Fact]
    public async Task Patch_with_tags_creates_unknown_tags_and_attaches_them()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "food", "travel" } }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(new[] { "food", "travel" }, await CurrentTagsForHeaderAsync(seed.HeaderId));
    }

    [Fact]
    public async Task Patch_with_tags_reuses_existing_tag_rows_within_the_ledger()
    {
        var seed = await SeedSingleAsync();
        // Pre-create the "food" tag on a different header. The
        // subsequent PATCH should reuse this tag id, not create a
        // second tag with the same name.
        var (otherLegId, _) = await seed.Ledger.AddTransactionPairAsync(
            seed.BankId, seed.GroceriesId, -5m,
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        var existingTagId = await seed.Ledger.AddTagAsync(otherLegId, "food");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "food" } }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        // Exactly one tag row named "food" in this ledger.
        var foodCount = await db.Tags.AsNoTracking()
            .CountAsync(t => t.LedgerId == seed.Ledger.LedgerId && t.Name == "food");
        Assert.Equal(1, foodCount);
        // The PATCHed header's pairing uses the pre-existing id.
        var pairing = await db.TxnHeaderTags.AsNoTracking()
            .SingleAsync(p => p.HeaderId == seed.HeaderId);
        Assert.Equal(existingTagId, pairing.TagId);
    }

    [Fact]
    public async Task Patch_with_tags_is_case_insensitive_within_ledger()
    {
        var seed = await SeedSingleAsync();
        // First PATCH creates "Food" (capitalized).
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "Food" } }));

        // Second PATCH on a different header uses "FOOD". Same tag
        // row reused; the dictionary still has just one entry.
        var (otherLegId, _) = await seed.Ledger.AddTransactionPairAsync(
            seed.BankId, seed.GroceriesId, -5m,
            new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        var otherHeaderId = await seed.Ledger.ResolveHeaderIdAsync(otherLegId);
        await client.SendAsync(Patch(seed.Ledger.LedgerId, otherHeaderId,
            new PatchTransactionRequest { Tags = new[] { "FOOD" } }));

        await using var db = _fixture.NewDbContext();
        var foodRows = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == seed.Ledger.LedgerId)
            .ToListAsync();
        Assert.Single(foodRows);
        Assert.Equal("Food", foodRows[0].Name); // first-use casing wins
    }

    [Fact]
    public async Task Patch_with_empty_tags_array_clears_all_tags()
    {
        var seed = await SeedSingleAsync();
        await seed.Ledger.AddTagAsync(seed.BankLegId, "food");
        await seed.Ledger.AddTagAsync(seed.BankLegId, "travel");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Empty(await CurrentTagsForHeaderAsync(seed.HeaderId));
    }

    [Fact]
    public async Task Patch_with_tags_diffs_existing_pairings()
    {
        var seed = await SeedSingleAsync();
        await seed.Ledger.AddTagAsync(seed.BankLegId, "food");
        await seed.Ledger.AddTagAsync(seed.BankLegId, "travel");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "food", "lunch" } }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(new[] { "food", "lunch" }, await CurrentTagsForHeaderAsync(seed.HeaderId));
    }

    [Fact]
    public async Task Patch_with_omitted_tags_leaves_existing_tags_untouched()
    {
        var seed = await SeedSingleAsync();
        await seed.Ledger.AddTagAsync(seed.BankLegId, "food");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);
        // PATCH touches only payee — tags is null, must be left alone.
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Payee = "Updated Payee" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(new[] { "food" }, await CurrentTagsForHeaderAsync(seed.HeaderId));
    }

    [Fact]
    public async Task Patch_with_duplicate_tag_names_is_deduplicated()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "food", "Food", "FOOD" } }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(new[] { "food" }, await CurrentTagsForHeaderAsync(seed.HeaderId));
    }

    [Fact]
    public async Task Patch_with_empty_tag_name_returns_422_transaction_tag_empty()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { "food", "   " } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-tag-empty",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_with_too_long_tag_name_returns_422_transaction_tag_too_long()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var longName = new string('x', 65);
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = new[] { longName } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-tag-too-long",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_with_too_many_tags_returns_422_transaction_tags_too_many()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var tooMany = Enumerable.Range(0, 21).Select(i => $"tag{i}").ToArray();
        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest { Tags = tooMany }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("transaction-tags-too-many",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_with_tags_and_other_edits_applies_atomically()
    {
        var seed = await SeedSingleAsync();
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, seed.Ledger);

        var response = await client.SendAsync(Patch(seed.Ledger.LedgerId, seed.HeaderId,
            new PatchTransactionRequest
            {
                Payee = "Whole Foods",
                Memo = "weekend grocery run",
                Tags = new[] { "food", "weekly" },
                Approve = true,
            }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var header = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == seed.HeaderId);
        var ovr = await db.TxnHeaderOverrides.AsNoTracking()
            .SingleAsync(o => o.HeaderId == seed.HeaderId);
        Assert.Equal("Whole Foods", ovr.Payee);
        Assert.Equal("weekend grocery run", ovr.Memo);
        Assert.False(header.NeedsReview);
        Assert.Equal(new[] { "food", "weekly" }, await CurrentTagsForHeaderAsync(seed.HeaderId));
    }
}
