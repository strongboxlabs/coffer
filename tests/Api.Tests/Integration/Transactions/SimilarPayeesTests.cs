using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// End-to-end checks for
/// <c>GET /api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees</c>
/// — slice 2c.6c Tier 1 recall. Anchors on the current row's raw
/// bank payee; returns prior approved bank-feed rows' chosen
/// <c>(payee, counterparty)</c> pairs aggregated by use count. The
/// counterparty is the prior row's non-money-side leg, so a category
/// and a transfer destination are both recallable.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SimilarPayeesTests
{
    private readonly PostgresFixture _fixture;

    public SimilarPayeesTests(PostgresFixture fixture)
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
    /// Seed a single-posting bank-feed row directly into the
    /// canonical tables. <paramref name="providerKey"/> picks the
    /// recall scope (default <c>simplefin</c>); a non-null
    /// provider_key is what makes the row a Tier 1 anchor /
    /// candidate at all. <c>needs_review</c> defaults to false
    /// (= approved) so prior rows participate, the anchor row sets
    /// it true. <paramref name="origin"/> defaults to
    /// <c>online_import</c> for SimpleFIN; pass <c>file_import</c>
    /// for OFX/CSV. Returns the header id.
    ///
    /// <para><paramref name="counterpartyAccountId"/> is any account
    /// — a category on an ordinary expense row, a second real
    /// account when the row is a transfer.</para>
    /// </summary>
    private async Task<Guid> SeedBankFeedAsync(
        SyntheticLedger ledger,
        Guid bankAccountId,
        Guid counterpartyAccountId,
        decimal amount,
        DateTime postedAt,
        string bankPayee,
        bool needsReview,
        string? overridePayee = null,
        string providerKey = "simplefin",
        string origin = "online_import")
    {
        var headerId = Guid.NewGuid();
        var bankLegId = Guid.NewGuid();
        var counterpartyLegId = Guid.NewGuid();
        await using var db = _fixture.NewDbContext();
        // external_id required for SimpleFIN-origin rows (mig 105 CHECK);
        // file_import rows also keep an external_id for provider dedup.
        // Mig 107: origin/provider_key are two columns; SimilarPayees
        // dedup scopes by the anchor row's provider_key.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers
                (id, ledger_id, origin, provider_key, external_id, payee, posted_at, transacted_at, created_at, needs_review)
            VALUES
                ({headerId}, {ledger.LedgerId}, {origin}, {providerKey}, {headerId.ToString()}, {bankPayee},
                 {postedAt},{postedAt}, {postedAt}, {needsReview});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES
                ({bankLegId},          {headerId}, {ledger.LedgerId}, {bankAccountId},          0, {amount}),
                ({counterpartyLegId},  {headerId}, {ledger.LedgerId}, {counterpartyAccountId},  0, {-amount});");
        if (overridePayee is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_overrides (header_id, ledger_id, payee)
                VALUES ({headerId}, {ledger.LedgerId}, {overridePayee});");
        }
        return headerId;
    }

    private static string Url(Guid ledgerId, Guid headerId) =>
        $"/api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees";

    [Fact]
    public async Task Returns_payee_and_category_from_a_single_prior_approved_match()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Prior approved row: bank payee "STARBUCKS", user
        // overrode to "Starbucks Coffee" and picked Coffee category.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");

        // Current row: same bank payee, awaiting review.
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(Url(ledger.LedgerId, currentId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var suggestions = (await response.Content.ReadFromJsonAsync<List<SimilarPayeeDto>>())!;
        var single = Assert.Single(suggestions);
        Assert.Equal("Starbucks Coffee", single.Payee);
        Assert.Equal(coffee.Id, single.CounterpartyAccountId);
        Assert.Equal("Coffee", single.CounterpartyAccountName);
        Assert.Equal(1, single.UseCount);
    }

    [Fact]
    public async Task Recalls_a_transfer_counterparty_when_prior_rows_have_no_category_leg()
    {
        // Regression: recall used to require the prior row to carry an
        // `account_type = 'category'` leg. A recurring charge the user
        // always settles as a TRANSFER (checking → FSA) has two
        // real-account legs and no category leg at all, so every prior
        // row was filtered out and the chip row silently never
        // rendered — exactly the case where recall is most useful.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var fsa = await ledger.AddBankAccountAsync("PayFlex FSA");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Prior approved row: settled as a transfer to the FSA account.
        await SeedBankFeedAsync(
            ledger, checking.Id, fsa.Id, 242.85m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "INSPIRA-AMERICAN", needsReview: false,
            overridePayee: "Inspira American");

        // Fresh feed row on the same account, still parked on
        // Uncategorized the way ingest leaves it.
        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, 99.98m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "INSPIRA-AMERICAN", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;

        var single = Assert.Single(suggestions);
        Assert.Equal("Inspira American", single.Payee);
        Assert.Equal(fsa.Id, single.CounterpartyAccountId);
        Assert.Equal("PayFlex FSA", single.CounterpartyAccountName);
        Assert.Equal(1, single.UseCount);
    }

    [Fact]
    public async Task Ranks_transfer_and_category_suggestions_together()
    {
        // Transfers and categories are the same kind of suggestion to
        // the editor's AccountCategoryPicker, so they compete in one
        // use-count ordering rather than living in separate tiers.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var fsa = await ledger.AddBankAccountAsync("PayFlex FSA");
        var medical = await ledger.AddCategoryAsync("Medical");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Two transfer settlements, one category settlement.
        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, checking.Id, fsa.Id, 100m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "INSPIRA-AMERICAN", needsReview: false,
                overridePayee: "Inspira American");
        }
        await SeedBankFeedAsync(
            ledger, checking.Id, medical.Id, 50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "INSPIRA-AMERICAN", needsReview: false,
            overridePayee: "Inspira Medical");

        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, 75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "INSPIRA-AMERICAN", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(fsa.Id, suggestions[0].CounterpartyAccountId);
        Assert.Equal(2, suggestions[0].UseCount);
        Assert.Equal(medical.Id, suggestions[1].CounterpartyAccountId);
        Assert.Equal(1, suggestions[1].UseCount);
    }

    [Fact]
    public async Task Excludes_prior_rows_posted_to_a_different_money_side_account()
    {
        // "Counterparty" is only meaningful relative to an account, so
        // recall is scoped to the anchor's money side. A prior row for
        // the same payee on a DIFFERENT account would otherwise
        // suggest a pairing the user never made on this one — and on a
        // transfer it could even suggest the anchor's own account.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var checking = await ledger.AddBankAccountAsync("checking");
        var otherCard = await ledger.AddBankAccountAsync("other card");
        var coffee = await ledger.AddCategoryAsync("Coffee");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        await SeedBankFeedAsync(
            ledger, otherCard.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");

        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Aggregates_use_count_across_multiple_prior_matches()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Three prior approved rows, same (payee, category) pair.
        for (var i = 0; i < 3; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "STARBUCKS", needsReview: false,
                overridePayee: "Starbucks Coffee");
        }
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal(3, single.UseCount);
    }

    [Fact]
    public async Task Orders_by_use_count_descending_then_recency()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");
        var bills = await ledger.AddCategoryAsync("Bills");

        // 2 uses of (Starbucks Coffee, Coffee), 1 use of (Starbucks Subscription, Bills).
        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "STARBUCKS", needsReview: false,
                overridePayee: "Starbucks Coffee");
        }
        await SeedBankFeedAsync(
            ledger, bank.Id, bills.Id, -10m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Subscription");

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Starbucks Coffee", suggestions[0].Payee);
        Assert.Equal(2, suggestions[0].UseCount);
        Assert.Equal("Starbucks Subscription", suggestions[1].Payee);
        Assert.Equal(1, suggestions[1].UseCount);
    }

    [Fact]
    public async Task Excludes_prior_rows_with_needs_review_true()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Prior row with same payee but STILL needs review — not
        // an authoritative user choice; should be excluded.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Returns_empty_for_manual_rows()
    {
        // Manual rows have null provider_key (mig 107 CHECK) and so
        // cannot anchor Tier 1 — recall is a feed-row concern. Manual
        // candidates are likewise excluded by the same scope rule.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Seed two manual transactions with the same payee — they
        // shouldn't surface as suggestions for the third manual
        // row that we'll query against.
        await ledger.AddTransactionPairAsync(bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc), payee: "STARBUCKS");
        await ledger.AddTransactionPairAsync(bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc), payee: "STARBUCKS");
        var (currentLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            payee: "STARBUCKS");
        var currentId = await ledger.ResolveHeaderIdAsync(currentLegId);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Scope_is_anchor_provider_only_cross_provider_rows_dont_leak()
    {
        // Two providers, same ledger, same raw bank payee. The
        // anchor's provider_key bounds the candidate set: an OFX
        // anchor must only surface OFX prior accepts, never SimpleFIN
        // ones (and vice versa). The two feeds clean payees
        // differently in practice, so cross-provider recall would
        // suggest (payee, category) pairs the user never chose in
        // this feed's vocabulary.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // SimpleFIN prior accept.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks SimpleFIN");

        // OFX prior accept on the same raw payee.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks OFX",
            providerKey: "ofx", origin: "file_import");

        // OFX needs-review anchor.
        var ofxAnchor = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true,
            providerKey: "ofx", origin: "file_import");

        // SimpleFIN needs-review anchor.
        var sfAnchor = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ofxSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, ofxAnchor)))!;
        var ofx = Assert.Single(ofxSuggestions);
        Assert.Equal("Starbucks OFX", ofx.Payee);

        var sfSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, sfAnchor)))!;
        var sf = Assert.Single(sfSuggestions);
        Assert.Equal("Starbucks SimpleFIN", sf.Payee);
    }

    [Fact]
    public async Task Returns_empty_for_unknown_or_cross_ledger_header()
    {
        // alice owns the header; bob asks via his own ledger scope.
        // The repo's anchor read filters on (id, ledger_id), so the
        // anchor lookup returns null → empty list. Cross-ledger
        // probes are indistinguishable from "no suggestions."
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("alice");
        var aliceCoffee = await alice.AddCategoryAsync("Coffee");
        var aliceHeaderId = await SeedBankFeedAsync(
            alice, aliceBank.Id, aliceCoffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var suggestions = (await bobClient.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(bob.LedgerId, aliceHeaderId)))!;
        Assert.Empty(suggestions);

        // Random header id under bob's own ledger likewise returns
        // empty, not 404 — the SPA treats absence and miss the same.
        var random = (await bobClient.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(bob.LedgerId, Guid.NewGuid())))!;
        Assert.Empty(random);
    }

    [Fact]
    public async Task Does_not_leak_across_ledgers()
    {
        // Two ledgers have the same "STARBUCKS" online payee. Bob's
        // suggestions must only surface bob's prior choices, never
        // alice's.
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var aliceBank = await alice.AddBankAccountAsync("checking");
        var aliceCoffee = await alice.AddCategoryAsync("Alice Coffee");
        await SeedBankFeedAsync(
            alice, aliceBank.Id, aliceCoffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Alice Starbucks");

        var bob = await SyntheticLedger.CreateAsync(_fixture);
        var bobBank = await bob.AddBankAccountAsync("checking");
        var bobCoffee = await bob.AddCategoryAsync("Bob Coffee");
        // Bob has no prior rows.
        var currentId = await SeedBankFeedAsync(
            bob, bobBank.Id, bobCoffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var suggestions = (await bobClient.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(bob.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Excludes_prior_merged_or_hidden_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Two prior approved bank rows with the same online payee.
        var hiddenId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");
        var mergedId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");
        var winnerId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 3, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");

        // Hide one prior row + merge another into the winner. Only
        // the unhidden, unmerged prior should count toward use_count.
        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {hiddenId};
                UPDATE txn_headers SET is_merged_into = {winnerId} WHERE id = {mergedId};");
        }

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal(1, single.UseCount); // only the winner counts
    }

    [Fact]
    public async Task Returns_422_when_ledger_is_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);
        var bob = await SyntheticLedger.CreateAsync(_fixture);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);

        var response = await bobClient.GetAsync(Url(alice.LedgerId, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Returns_empty_when_target_already_accepted()
    {
        // Similar-payees is an Accept-flow affordance — once the
        // row is accepted there's nothing to apply. API layer
        // enforces independent of the SPA per the
        // server-side-concurrency principle.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks Coffee");

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false);

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Returns_empty_when_target_hidden_or_merged()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: false,
            overridePayee: "Starbucks");

        var hiddenId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);
        var winnerId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);
        var mergedAwayId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true);

        await using (var db = _fixture.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {hiddenId};
                UPDATE txn_headers SET is_merged_into = {winnerId} WHERE id = {mergedAwayId};");
        }

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var hiddenSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, hiddenId)))!;
        Assert.Empty(hiddenSuggestions);
        var mergedSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, mergedAwayId)))!;
        Assert.Empty(mergedSuggestions);
    }

    [Fact]
    public async Task Excludes_suggestion_already_matching_the_current_row()
    {
        // No point suggesting (payee, category) the user has
        // ALREADY applied to the row — there's nothing to change.
        // Two prior approved rows establish ("Starbucks Coffee",
        // Coffee). The current row is opened with that exact pair
        // already saved → the suggestion dedupes against itself and
        // the response is empty.
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "STARBUCKS", needsReview: false,
                overridePayee: "Starbucks Coffee");
        }

        // The "current" row has the SAME (resolved-payee, category)
        // as the prior rows: override → "Starbucks Coffee" and
        // counterparty leg on Coffee. Nothing new to suggest.
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "STARBUCKS", needsReview: true,
            overridePayee: "Starbucks Coffee");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }
}
