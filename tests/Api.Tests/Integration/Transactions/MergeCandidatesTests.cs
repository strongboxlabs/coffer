using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for slice 2c.6d:
/// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/merge-candidates</c>
/// and the <c>mergeFromHeaderId</c> field on PATCH. Bank-feed-shaped
/// targets are seeded directly via raw SQL (origin='simplefin'); manual
/// candidate rows use the standard <see cref="SyntheticLedger.AddTransactionPairAsync"/>
/// seeder.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MergeCandidatesTests
{
    private readonly PostgresFixture _fixture;

    public MergeCandidatesTests(PostgresFixture fixture)
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

    /// <summary>
    /// Seed a single-posting bank-feed row directly. Used as the
    /// merge target in these tests; the manual candidate(s) come
    /// from the standard pair seeder.
    /// </summary>
    private async Task<Guid> SeedBankFeedTargetAsync(
        SyntheticLedger ledger,
        Guid bankAccountId,
        Guid counterpartyAccountId,
        decimal amount,
        DateTime postedAt,
        string bankPayee,
        bool needsReview = true)
    {
        var headerId = Guid.NewGuid();
        var bankLegId = Guid.NewGuid();
        var counterpartyLegId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        // external_id required for SimpleFIN-origin rows (mig 105
        // CHECK). headerId stringified gives a fixture-unique value.
        // Mig 107: origin is icon-level; SimpleFIN tag in
        // provider_key. needsReview defaults to true (the typical
        // "fresh feed row" shape used as the merge target); pass
        // false when seeding an accepted bank-fed candidate to
        // surface in the candidate set.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers
                (id, ledger_id, origin, provider_key, external_id, payee, posted_at, created_at, needs_review)
            VALUES
                ({headerId}, {ledger.LedgerId}, 'online_import', 'simplefin', {headerId.ToString()}, {bankPayee},
                 {postedAt}, {postedAt}, {needsReview});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES
                ({bankLegId},         {headerId}, {ledger.LedgerId}, {bankAccountId},        0, {amount}),
                ({counterpartyLegId}, {headerId}, {ledger.LedgerId}, {counterpartyAccountId}, 0, {-amount});");
        return headerId;
    }

    private static string CandidatesUrl(Guid ledgerId, Guid headerId) =>
        $"/api/ledgers/{ledgerId}/transactions/{headerId}/merge-candidates";

    private static HttpRequestMessage Patch(
        Guid ledgerId, Guid headerId, PatchTransactionRequest body) =>
        new(HttpMethod.Patch,
            $"/api/ledgers/{ledgerId}/transactions/{headerId}")
        { Content = JsonContent.Create(body) };

    // -- GET merge-candidates -----------------------------------------

    [Fact]
    public async Task Returns_a_single_manual_match_on_amount_account_and_date()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Manual row the user already wrote: 03-05, $9 on bank, category Dining.
        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "Downtown Parking");
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);

        // Bank-feed target: 03-06 (within 7 days), same $9 on same bank.
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "DOWNTOWN PARKING #4250");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var single = Assert.Single(candidates);
        Assert.Equal(manualId, single.HeaderId);
        Assert.Equal("Downtown Parking", single.Payee);
        // Candidate (03-05) is one day older than target (03-06)
        // → signed delta of -1 per the DTO's contract.
        Assert.Equal(-1, single.DaysDelta);
        var posting = Assert.Single(single.Postings);
        Assert.Equal(dining.Id, posting.CounterpartyAccountId);
        Assert.Equal(9m, posting.Amount); // counterparty leg is positive
    }

    [Fact]
    public async Task Excludes_rows_with_a_different_amount()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Manual at -$8 — close but not exact.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -8m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Excludes_rows_outside_the_seven_day_window()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // 10 days before target — outside the ±7-day window.
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 2, 24, 12, 0, 0, DateTimeKind.Utc));
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Excludes_rows_on_a_different_source_account()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Manual on Savings — even though the amount matches, it's
        // not on the target's source (Checking).
        await ledger.AddTransactionPairAsync(
            savings.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var targetId = await SeedBankFeedTargetAsync(
            ledger, checking.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Includes_accepted_bank_feed_rows_as_candidates()
    {
        // Matching is by (account, amount, date) regardless of
        // origin — a previously-ACCEPTED bank-feed row whose
        // amount/account/date matches the target is a valid
        // candidate (e.g., bank double-post, or a row the user
        // already categorized that the new sync re-surfaced).
        // Pending bank-feed twins are NOT candidates — see
        // Excludes_pending_candidates.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var priorBankId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "PRIOR BANK ROW",
            needsReview: false);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var single = Assert.Single(candidates);
        Assert.Equal(priorBankId, single.HeaderId);
    }

    [Fact]
    public async Task Excludes_pending_candidates()
    {
        // A pending (needs_review=true) bank-feed row that matches by
        // (account, amount, date) is NOT a valid merge target —
        // merging into a row the user hasn't curated yet absorbs
        // nothing. Hide-then-pick is the right workflow for two
        // pending duplicates.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "PENDING TWIN",
            needsReview: true);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Includes_merge_winner_candidates()
    {
        // Inverted-merge direction: an accepted row that already
        // absorbed prior loser(s) IS a valid candidate. Folding the
        // editor INTO it just adds another loser pointer; the
        // surviving winner keeps its identity and prior history.
        // This unblocks multi-source rows (MD+, SimpleFIN, OFX) from
        // collapsing into one canonical row.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Accepted candidate that has already won a prior merge.
        var (winnerLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "winner");
        var winnerId = await ledger.ResolveHeaderIdAsync(winnerLegId);
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_merge_winner = true WHERE id = {winnerId};");
        }

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var match = Assert.Single(candidates);
        Assert.Equal(winnerId, match.HeaderId);
    }

    [Fact]
    public async Task Orders_by_date_proximity()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Three manual matches: 1d, 3d, 6d away from the target's 03-10.
        var (oneDayLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc),
            payee: "near");
        var (threeDayLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc),
            payee: "mid");
        var (sixDayLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc),
            payee: "far");
        var nearId = await ledger.ResolveHeaderIdAsync(oneDayLegId);
        var midId = await ledger.ResolveHeaderIdAsync(threeDayLegId);
        var farId = await ledger.ResolveHeaderIdAsync(sixDayLegId);

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Equal(3, candidates.Count);
        Assert.Equal(nearId, candidates[0].HeaderId);
        Assert.Equal(midId, candidates[1].HeaderId);
        Assert.Equal(farId, candidates[2].HeaderId);
    }

    [Fact]
    public async Task Excludes_already_merged_or_hidden_candidates()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (legA, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc));
        var (legB, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var (legC, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc));
        var hiddenId = await ledger.ResolveHeaderIdAsync(legA);
        var mergedId = await ledger.ResolveHeaderIdAsync(legB);
        var liveId = await ledger.ResolveHeaderIdAsync(legC);

        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {hiddenId};
                UPDATE txn_headers SET is_merged_into = {liveId} WHERE id = {mergedId};");
        }

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var single = Assert.Single(candidates);
        Assert.Equal(liveId, single.HeaderId);
    }

    [Fact]
    public async Task Does_not_leak_across_ledgers()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("checking");
        var aliceCat = await alice.AddCategoryAsync("Dining");
        await alice.AddTransactionPairAsync(
            aliceBank.Id, aliceCat.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var bobBank = await bob.AddBankAccountAsync("checking");
        var bobUncategorized = await bob.AddCategoryAsync("Uncategorized");
        var targetId = await SeedBankFeedTargetAsync(
            bob, bobBank.Id, bobUncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var candidates = (await bobClient.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(bob.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Surfaces_a_split_candidate_with_aggregated_amount_on_the_source()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var tip = await ledger.AddCategoryAsync("Tip");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Split manual row: $5 dining + $4 tip on bank = -$9 source aggregate.
        var (_, splitHeaderId) = await ledger.AddMultiSplitAsync(
            primaryAccountId: bank.Id,
            legs: new[] {
                (dining.Id, -5m),
                (tip.Id,    -4m),
            },
            postedAt: new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "Restaurant");

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "RESTAURANT NYC");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var single = Assert.Single(candidates);
        Assert.Equal(splitHeaderId, single.HeaderId);
        // Split structure preserved in the response: two
        // counterparty postings.
        Assert.Equal(2, single.Postings.Count);
        Assert.Equal(dining.Id, single.Postings[0].CounterpartyAccountId);
        Assert.Equal(tip.Id, single.Postings[1].CounterpartyAccountId);
    }

    [Fact]
    public async Task Returns_422_when_ledger_is_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var response = await bobClient.GetAsync(CandidatesUrl(alice.LedgerId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Returns_empty_when_target_is_hidden()
    {
        // Hidden rows aren't visible in the register and shouldn't
        // be addressable through Accept-flow affordances. Defensive
        // server gate; SPA can't reach a hidden row through the UI
        // but the API enforces independently.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {targetId};");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Returns_empty_when_target_is_already_accepted()
    {
        // Merge candidates is an Accept-flow affordance: once the
        // user has cleared needs_review on a row, the panel
        // shouldn't surface candidates for it. Otherwise opening
        // any already-processed row would dredge up noise.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // A manual candidate that WOULD match by (account, amount, ±7d)…
        await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));

        // …and a bank-feed target on the same shape, BUT already
        // accepted (needs_review=false).
        var targetId = Guid.NewGuid();
        var bankLegId = Guid.NewGuid();
        var cpLegId = Guid.NewGuid();
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_headers
                    (id, ledger_id, origin, provider_key, external_id, payee, posted_at, created_at, needs_review)
                VALUES
                    ({targetId}, {ledger.LedgerId}, 'online_import', 'simplefin', {targetId.ToString()}, 'Acme',
                     '2026-03-06', '2026-03-06', false);
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
                VALUES
                    ({bankLegId}, {targetId}, {ledger.LedgerId}, {bank.Id},          0, -9),
                    ({cpLegId},   {targetId}, {ledger.LedgerId}, {uncategorized.Id}, 0,  9);");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    // -- override-aware window + visibility (ADR-0003) ----------------

    [Fact]
    public async Task Offers_a_candidate_whose_overridden_date_is_inside_the_effective_window()
    {
        // The bug this fixes: the candidate's RAW posted_at is 9 days off
        // (outside ±7d), but the user curated its date (override) to the
        // target's day. The register shows it adjacent; the matcher must
        // too — the window reads the EFFECTIVE posted_at, not raw.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Manual candidate: raw 03-11 (9 days before the 03-20 target).
        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc),
            payee: "Curated Payee");
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        // …but its effective posted_at was curated to 03-20 (same day).
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_overrides (header_id, ledger_id, posted_at)
                VALUES ({manualId}, {ledger.LedgerId}, {new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc)});");
        }

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME 0320");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        var single = Assert.Single(candidates);
        Assert.Equal(manualId, single.HeaderId);
        // Effective dates coincide → 0-day delta, and the display date +
        // payee are the effective values.
        Assert.Equal(0, single.DaysDelta);
        Assert.Equal(new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc), single.PostedAt);
    }

    [Fact]
    public async Task Excludes_a_candidate_whose_overridden_date_falls_outside_the_window()
    {
        // Mirror image: raw posted_at is in-window, but the curated
        // (effective) date is far off. The effective value wins both
        // directions, so this must NOT be offered.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc));  // raw in-window
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_overrides (header_id, ledger_id, posted_at)
                VALUES ({manualId}, {ledger.LedgerId}, {new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)});");
        }

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Excludes_a_candidate_hidden_via_override()
    {
        // Candidate is visible on the base row but hidden by override —
        // effective visibility must exclude it.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 19, 12, 0, 0, DateTimeKind.Utc));
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_overrides (header_id, ledger_id, is_hidden)
                VALUES ({manualId}, {ledger.LedgerId}, true);");
        }

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var candidates = (await client.GetFromJsonAsync<List<MergeCandidateDto>>(
            CandidatesUrl(ledger.LedgerId, targetId)))!;
        Assert.Empty(candidates);
    }

    // -- PATCH with mergeFromHeaderId ---------------------------------

    [Fact]
    public async Task Patch_with_mergeFromHeaderId_folds_editor_into_candidate()
    {
        // Inverted-merge direction: the editor row (the needs_review
        // bank target) becomes the LOSER; the manual candidate is
        // the surviving canonical row. The editor's body content
        // (postings reshape, payee edit) is moot — it never applies
        // to the candidate, which stays exactly as it was.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            payee: "Downtown Parking");
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "DOWNTOWN PARKING #4250");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest
            {
                Approve = true,
                MergeFromHeaderId = manualId,
            }));
        // Minimal patch body without account_id / postings → server
        // returns 204 (no surviving-entry resolve needed). Real SPA
        // calls supply account_id and get 200 + resolved entry — see
        // the endpoint test for that path.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var target = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == targetId);
        var manual = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == manualId);
        // Editor (target) is now the loser of the candidate.
        Assert.Equal(manualId, target.IsMergedInto);
        // Approve=true still flips needs_review on the (now-hidden)
        // loser; harmless but keeps state coherent.
        Assert.False(target.NeedsReview);
        // Candidate (manual) is the surviving winner — its identity,
        // postings, and payee remain untouched. (Its posted_at DOES move
        // to the import's date — asserted in the next test.)
        Assert.Null(manual.IsMergedInto);
        Assert.True(manual.IsMergeWinner);
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_survivor_adopts_the_imported_date()
    {
        // ADR-0072 follow-up: the merged survivor takes the IMPORT
        // (editor / loser) row's posted date — the bank/feed date is
        // authoritative for the merged transaction.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var manualDate = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc);
        var importDate = new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc);

        // Manual row (the survivor / winner) with the user's guessed date.
        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m, manualDate, payee: "Downtown Parking");
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        // Fresh bank-feed row (the editor / loser) with the bank date.
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m, importDate,
            bankPayee: "DOWNTOWN PARKING #4250");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest
            {
                Approve = true,
                MergeFromHeaderId = manualId,
            }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The survivor now shows the imported date (03-06), not its own
        // manual date (03-05) — via an effective (override-aware) read.
        await using var db = _fixture.NewDbContext();
        var winnerEffectivePostedAt = await db.ResolvedTransactions
            .AsNoTracking()
            .Where(rt => rt.HeaderId == manualId)
            .Select(rt => rt.PostedAt)
            .FirstAsync();
        Assert.Equal(importDate, winnerEffectivePostedAt);
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_bumps_survivor_to_reconciling()
    {
        // ADR-0082: merging a fresh feed row into an (uncleared) transaction
        // marks the survivor 'reconciling' on the merge account — the bank has
        // acknowledged it, but it isn't the user-affirmed 'cleared'.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), payee: "Downtown Parking");
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "DOWNTOWN PARKING #4250");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest { Approve = true, MergeFromHeaderId = manualId }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var status = await db.ResolvedTransactions.AsNoTracking()
            .Where(rt => rt.HeaderId == manualId && rt.AccountId == bank.Id)
            .Select(rt => rt.Status)
            .FirstAsync();
        Assert.Equal("reconciling", status);
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_accepts_an_accepted_bank_feed_source()
    {
        // Merge candidate (the surviving canonical row) can be any
        // non-hidden, not-already-merged, ACCEPTED row in the
        // ledger — bank-feed twin, manual placeholder, or anything
        // else. The matching contract is (account, amount, date);
        // we don't gate on origin. Pending candidates are rejected —
        // see Patch_mergeFromHeaderId_422_when_source_is_pending.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");
        var otherBankRowId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "A",
            needsReview: false);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "B");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest { MergeFromHeaderId = otherBankRowId }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var db = _fixture.NewDbContext();
        var loser = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == targetId);
        var winner = await db.TxnHeaders.AsNoTracking()
            .SingleAsync(h => h.Id == otherBankRowId);
        // Inverted direction: editor (target) is the loser; the
        // accepted candidate is the surviving winner.
        Assert.Equal(otherBankRowId, loser.IsMergedInto);
        Assert.True(winner.IsMergeWinner);
        Assert.Null(winner.IsMergedInto);
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_source_is_pending()
    {
        // Server mirrors GET /merge-candidates' settled-only filter
        // on the source side. A hand-crafted PATCH that names a
        // pending row as the merge source must be rejected.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");
        var pendingSourceId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "PENDING",
            needsReview: true);
        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "TARGET");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest { MergeFromHeaderId = pendingSourceId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_folds_into_existing_winner()
    {
        // Inverted-merge direction allows multi-source rows to
        // collapse into one canonical winner. A row that already
        // absorbed another (an existing merge winner) IS a valid
        // candidate — folding a third source into it just adds
        // another loser pointer. Graph stays one-hop: every loser
        // points directly to the same surviving winner.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Set up: priorLoser already merged into winner.
        var (winnerLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var winnerId = await ledger.ResolveHeaderIdAsync(winnerLegId);
        var (priorLoserLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var priorLoserId = await ledger.ResolveHeaderIdAsync(priorLoserLegId);
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_merge_winner = true WHERE id = {winnerId};
                UPDATE txn_headers SET is_merged_into = {winnerId} WHERE id = {priorLoserId};");
        }

        var freshTargetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "FRESH-FROM-FEED");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, freshTargetId,
            new PatchTransactionRequest { MergeFromHeaderId = winnerId, Approve = true }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var verifyDb = _fixture.NewDbContext();
        var fresh = await verifyDb.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == freshTargetId);
        var winner = await verifyDb.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == winnerId);
        var priorLoser = await verifyDb.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == priorLoserId);
        // Fresh target is now another loser of winner.
        Assert.Equal(winnerId, fresh.IsMergedInto);
        // Prior loser pointer is unchanged — graph stays one-hop.
        Assert.Equal(winnerId, priorLoser.IsMergedInto);
        // Winner stays the canonical surviving row.
        Assert.Null(winner.IsMergedInto);
        Assert.True(winner.IsMergeWinner);
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_source_belongs_to_another_ledger()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("checking");
        var aliceCat = await alice.AddCategoryAsync("Dining");
        var (aliceLegId, _) = await alice.AddTransactionPairAsync(
            aliceBank.Id, aliceCat.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var aliceManualId = await alice.ResolveHeaderIdAsync(aliceLegId);

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var bobBank = await bob.AddBankAccountAsync("checking");
        var bobUncategorized = await bob.AddCategoryAsync("Uncategorized");
        var bobTargetId = await SeedBankFeedTargetAsync(
            bob, bobBank.Id, bobUncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var response = await bobClient.SendAsync(Patch(bob.LedgerId, bobTargetId,
            new PatchTransactionRequest { MergeFromHeaderId = aliceManualId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_source_equals_target()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var (legId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var headerId = await ledger.ResolveHeaderIdAsync(legId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, headerId,
            new PatchTransactionRequest { MergeFromHeaderId = headerId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_source_already_merged()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (legId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var manualId = await ledger.ResolveHeaderIdAsync(legId);

        var firstTargetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "A");
        // Merge once: stamps manualId.is_merged_into = firstTarget.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_merged_into = {firstTargetId} WHERE id = {manualId};");
        }
        var secondTargetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "B");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, secondTargetId,
            new PatchTransactionRequest { MergeFromHeaderId = manualId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_target_already_accepted()
    {
        // The API layer enforces the same target gate that the GET
        // /merge-candidates endpoint applies — independent of the
        // SPA's UI filtering per the server-side-concurrency
        // principle. A hand-crafted PATCH that tries to merge into
        // an already-accepted row must be rejected.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET needs_review = false WHERE id = {targetId};");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest { MergeFromHeaderId = manualId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_mergeFromHeaderId_422_when_target_is_hidden()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        var (manualLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, dining.Id, -9m,
            new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc));
        var manualId = await ledger.ResolveHeaderIdAsync(manualLegId);

        var targetId = await SeedBankFeedTargetAsync(
            ledger, bank.Id, uncategorized.Id, -9m,
            new DateTime(2026, 3, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "Acme");
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {targetId};");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.SendAsync(Patch(ledger.LedgerId, targetId,
            new PatchTransactionRequest { MergeFromHeaderId = manualId }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("merge-source-invalid",
            doc.RootElement.GetProperty("code").GetString());
    }
}
