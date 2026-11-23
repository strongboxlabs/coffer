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
public sealed class SimilarPayeesTests : DatabaseTest
{
    public SimilarPayeesTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
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
        string? curatedPayee = null,
        string providerKey = "simplefin",
        string origin = "online_import")
    {
        var headerId = Guid.NewGuid();
        var bankLegId = Guid.NewGuid();
        var counterpartyLegId = Guid.NewGuid();
        await using var db = Db.NewDbContext();
        // external_id required for SimpleFIN-origin rows (mig 105 CHECK);
        // file_import rows also keep an external_id for provider dedup.
        // Mig 107: origin/provider_key are two columns; SimilarPayees
        // dedup scopes by the anchor row's provider_key.
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO txn_headers
                (id, ledger_id, origin, provider_key, external_id, payee, posted_at, transacted_at, created_at, needs_review,
                 originating_account_id)
            VALUES
                ({headerId}, {ledger.LedgerId}, {origin}, {providerKey}, {headerId.ToString()}, {curatedPayee ?? bankPayee},
                 {postedAt},{postedAt}, {postedAt}, {needsReview}, {bankAccountId});
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            VALUES
                ({bankLegId},          {headerId}, {ledger.LedgerId}, {bankAccountId},          0, {amount}),
                ({counterpartyLegId},  {headerId}, {ledger.LedgerId}, {counterpartyAccountId},  0, {-amount});");
        if (curatedPayee is not null)
        {
            // An EDITED row, in the shape migration 230 produces: the header
            // carries what the user typed and the sidecar carries what the bank
            // sent. Recall is the one feature that needs both to exist at once —
            // it anchors on the bank's text and suggests the user's — so this
            // fixture is where the flip is easiest to get backwards. Seeding the
            // curated name into BOTH would make every assertion here pass while
            // the endpoint matched on the wrong column.
            // As the first edit's capture writes it, the amount as it stood and no rounded
            // mark (ADR-0114 D15).
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO txn_header_originals
                    (header_id, ledger_id, payee, posted_at, transacted_at, amount, rounded_at_import)
                VALUES ({headerId}, {ledger.LedgerId}, {bankPayee}, {postedAt}, {postedAt}, {amount}, false);");
        }
        // Raw SQL reaches no writer: store the presentation as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(headerId);
        return headerId;
    }

    private static string Url(Guid ledgerId, Guid headerId) =>
        $"/api/ledgers/{ledgerId}/transactions/{headerId}/similar-payees";

    /// <summary>
    /// Recall works on a BROKERAGE row, including when the prior row's
    /// counterparty is the Holdings sibling.
    /// </summary>
    /// <remarks>
    /// <para>A feed row lands bank-shape whatever account it is destined for: two
    /// legs, (real account, Uncategorized), with the investment detail parked in
    /// the ingest_* carriers until the user classifies it. So the anchor side of
    /// recall needed nothing added for brokerages — the investment editor simply
    /// never asked for it, and a dividend the user categorises the same way every
    /// quarter had to be re-categorised by hand every quarter.</para>
    ///
    /// <para>A prior settled BUY of the same security has exactly two legs, one
    /// of them on the brokerage, so it satisfies every clause — and its "other
    /// leg" is the Holdings sub-account. That is structural (ADR-0019), not a
    /// counterparty anyone chooses, and it was briefly excluded here for that
    /// reason. Excluding it dropped the row's PAYEE too, which is the half worth
    /// recalling and can be carried by exactly that buy: with the exclusion,
    /// a curated name sitting on a settled buy left recall silent. So the row is
    /// returned, and the caller decides which half it can apply — the investment
    /// editor takes the payee always and the counterparty only into a category
    /// slot.</para>
    /// </remarks>
    [Fact]
    public async Task Recall_on_a_brokerage_offers_both_the_category_and_the_holdings_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var dividends = await ledger.AddCategoryAsync("Dividend Income", kind: "income");
        // Where a feed row's second leg sits before anyone classifies it.
        var unclassified = await ledger.AddCategoryAsync("Uncategorized");

        // Prior settled row: the same feed payee, categorised as income. This is
        // the pair recall exists to bring back.
        await SeedBankFeedAsync(
            ledger, brokerage.Id, dividends.Id, 120.00m,
            new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME CORP DIVIDEND", needsReview: false);

        // Prior settled row with the SAME feed payee whose other leg is the
        // Holdings sibling — the shape a settled buy leaves behind.
        Assert.NotNull(brokerage.HoldingsAccountId);
        await SeedBankFeedAsync(
            ledger, brokerage.Id, brokerage.HoldingsAccountId!.Value, 120.00m,
            new DateTime(2026, 1, 6, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME CORP DIVIDEND", needsReview: false);

        // The anchor: the same dividend arriving again, awaiting review.
        var anchorId = await SeedBankFeedAsync(
            ledger, brokerage.Id, unclassified.Id, 120.00m,
            new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "ACME CORP DIVIDEND", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var suggestions = await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, anchorId));

        // Both prior rows are offered, and each names its own counterparty —
        // the category on one, the Holdings sibling on the other. Which half a
        // caller can USE is the caller's decision; the repository does not
        // pre-empt it by dropping the row.
        Assert.Equal(2, suggestions!.Count);
        Assert.All(suggestions!, sug => Assert.Equal("ACME CORP DIVIDEND", sug.Payee));
        Assert.Contains(suggestions!, sug => sug.CounterpartyAccountId == dividends.Id);
        Assert.Contains(suggestions!,
            sug => sug.CounterpartyAccountId == brokerage.HoldingsAccountId!.Value);
    }

    [Fact]
    public async Task Returns_payee_and_category_from_a_single_prior_approved_match()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Prior approved row: bank payee "CAFE PURCHASE", user
        // renamed to "Corner Cafe" and picked Coffee category.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        // Current row: same bank payee, awaiting review.
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var response = await client.GetAsync(Url(ledger.LedgerId, currentId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var suggestions = (await response.Content.ReadFromJsonAsync<List<SimilarPayeeDto>>())!;
        var single = Assert.Single(suggestions);
        Assert.Equal("Corner Cafe", single.Payee);
        Assert.Equal(coffee.Id, single.CounterpartyAccountId);
        Assert.Equal("Coffee", single.CounterpartyAccountName);
        Assert.Equal(1, single.UseCount);
    }

    [Fact]
    public async Task Recalls_a_transfer_counterparty_when_prior_rows_have_no_category_leg()
    {
        // Recall must not require the prior row to carry an
        // `account_type = 'category'` leg. A recurring charge the user
        // always settles as a TRANSFER (checking → FSA) has two
        // real-account legs and no category leg at all, so such a filter
        // would drop every prior row and the chip row would silently never
        // render — exactly the case where recall is most useful.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("checking");
        var fsa = await ledger.AddBankAccountAsync("FSA");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Prior approved row: settled as a transfer to the FSA account.
        await SeedBankFeedAsync(
            ledger, checking.Id, fsa.Id, 234.56m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "BENEFITS-ADMIN", needsReview: false,
            curatedPayee: "Benefits Admin");

        // Fresh feed row on the same account, still parked on
        // Uncategorized the way ingest leaves it.
        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, 99.98m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "BENEFITS-ADMIN", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;

        var single = Assert.Single(suggestions);
        Assert.Equal("Benefits Admin", single.Payee);
        Assert.Equal(fsa.Id, single.CounterpartyAccountId);
        Assert.Equal("FSA", single.CounterpartyAccountName);
        Assert.Equal(1, single.UseCount);
    }

    [Fact]
    public async Task Ranks_transfer_and_category_suggestions_together()
    {
        // Transfers and categories are the same kind of suggestion to
        // the editor's AccountCategoryPicker, so they compete in one
        // use-count ordering rather than living in separate tiers.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("checking");
        var fsa = await ledger.AddBankAccountAsync("FSA");
        var medical = await ledger.AddCategoryAsync("Medical");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        // Two transfer settlements, one category settlement.
        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, checking.Id, fsa.Id, 100m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "BENEFITS-ADMIN", needsReview: false,
                curatedPayee: "Benefits Admin");
        }
        await SeedBankFeedAsync(
            ledger, checking.Id, medical.Id, 50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "BENEFITS-ADMIN", needsReview: false,
            curatedPayee: "Benefits Medical");

        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, 75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "BENEFITS-ADMIN", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("checking");
        var otherCard = await ledger.AddBankAccountAsync("other card");
        var coffee = await ledger.AddCategoryAsync("Coffee");
        var uncategorized = await ledger.AddCategoryAsync("Uncategorized");

        await SeedBankFeedAsync(
            ledger, otherCard.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        var currentId = await SeedBankFeedAsync(
            ledger, checking.Id, uncategorized.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Aggregates_use_count_across_multiple_prior_matches()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Three prior approved rows, same (payee, category) pair.
        for (var i = 0; i < 3; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "CAFE PURCHASE", needsReview: false,
                curatedPayee: "Corner Cafe");
        }
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal(3, single.UseCount);
    }

    [Fact]
    public async Task Orders_by_use_count_descending_then_recency()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");
        var bills = await ledger.AddCategoryAsync("Bills");

        // 2 uses of (Corner Cafe, Coffee), 1 use of (Corner Cafe Subscription, Bills).
        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "CAFE PURCHASE", needsReview: false,
                curatedPayee: "Corner Cafe");
        }
        await SeedBankFeedAsync(
            ledger, bank.Id, bills.Id, -10m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe Subscription");

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Corner Cafe", suggestions[0].Payee);
        Assert.Equal(2, suggestions[0].UseCount);
        Assert.Equal("Corner Cafe Subscription", suggestions[1].Payee);
        Assert.Equal(1, suggestions[1].UseCount);
    }

    [Fact]
    public async Task Excludes_prior_rows_with_needs_review_true()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Prior row with same payee but STILL needs review — not
        // an authoritative user choice; should be excluded.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Seed two manual transactions with the same payee — they
        // shouldn't surface as suggestions for the third manual
        // row that we'll query against.
        await ledger.AddTransactionPairAsync(bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc), payee: "CAFE PURCHASE");
        await ledger.AddTransactionPairAsync(bank.Id, coffee.Id, -4m,
            new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc), payee: "CAFE PURCHASE");
        var (currentLegId, _) = await ledger.AddTransactionPairAsync(
            bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            payee: "CAFE PURCHASE");
        var currentId = await ledger.ResolveHeaderIdAsync(currentLegId);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // SimpleFIN prior accept.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe SimpleFIN");

        // OFX prior accept on the same raw payee.
        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe OFX",
            providerKey: "ofx", origin: "file_import");

        // OFX needs-review anchor.
        var ofxAnchor = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true,
            providerKey: "ofx", origin: "file_import");

        // SimpleFIN needs-review anchor.
        var sfAnchor = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var ofxSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, ofxAnchor)))!;
        var ofx = Assert.Single(ofxSuggestions);
        Assert.Equal("Corner Cafe OFX", ofx.Payee);

        var sfSuggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, sfAnchor)))!;
        var sf = Assert.Single(sfSuggestions);
        Assert.Equal("Corner Cafe SimpleFIN", sf.Payee);
    }

    [Fact]
    public async Task Returns_empty_for_unknown_or_cross_ledger_header()
    {
        // alice owns the header; bob asks via his own ledger scope.
        // The repo's anchor read filters on (id, ledger_id), so the
        // anchor lookup returns null → empty list. Cross-ledger
        // probes are indistinguishable from "no suggestions."
        var alice = await SyntheticLedger.CreateAsync(Db);
        var aliceBank = await alice.AddBankAccountAsync("alice");
        var aliceCoffee = await alice.AddCategoryAsync("Coffee");
        var aliceHeaderId = await SeedBankFeedAsync(
            alice, aliceBank.Id, aliceCoffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        var bob = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        // Two ledgers have the same "CAFE PURCHASE" online payee. Bob's
        // suggestions must only surface bob's prior choices, never
        // alice's.
        var alice = await SyntheticLedger.CreateAsync(Db);
        var aliceBank = await alice.AddBankAccountAsync("checking");
        var aliceCoffee = await alice.AddCategoryAsync("Alice Coffee");
        await SeedBankFeedAsync(
            alice, aliceBank.Id, aliceCoffee.Id, -4m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Alice Corner Cafe");

        var bob = await SyntheticLedger.CreateAsync(Db);
        var bobBank = await bob.AddBankAccountAsync("checking");
        var bobCoffee = await bob.AddCategoryAsync("Bob Coffee");
        // Bob has no prior rows.
        var currentId = await SeedBankFeedAsync(
            bob, bobBank.Id, bobCoffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var bobClient = await AuthedClientAsync(factory, bob);
        var suggestions = (await bobClient.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(bob.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Excludes_prior_merged_or_hidden_rows()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        // Two prior approved bank rows with the same online payee.
        var hiddenId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");
        var mergedId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");
        var winnerId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 3, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        // Hide one prior row + merge another into the winner. Only
        // the unhidden, unmerged prior should count toward use_count.
        await using (var db = Db.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {hiddenId};
                UPDATE txn_headers SET is_merged_into = {winnerId} WHERE id = {mergedId};");
        }

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal(1, single.UseCount); // only the winner counts
    }

    [Fact]
    public async Task Returns_422_when_ledger_is_not_visible_to_caller()
    {
        var alice = await SyntheticLedger.CreateAsync(Db);
        var bob = await SyntheticLedger.CreateAsync(Db);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task Returns_empty_when_target_hidden_or_merged()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Cafe Purchase");

        var hiddenId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);
        var winnerId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 2, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);
        var mergedAwayId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);

        await using (var db = Db.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE txn_headers SET is_hidden = true WHERE id = {hiddenId};
                UPDATE txn_headers SET is_merged_into = {winnerId} WHERE id = {mergedAwayId};");
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
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
        // Two prior approved rows establish ("Corner Cafe",
        // Coffee). The current row is opened with that exact pair
        // already saved → the suggestion dedupes against itself and
        // the response is empty.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        for (var i = 0; i < 2; i++)
        {
            await SeedBankFeedAsync(
                ledger, bank.Id, coffee.Id, -4m,
                new DateTime(2026, 3, i + 1, 12, 0, 0, DateTimeKind.Utc),
                bankPayee: "CAFE PURCHASE", needsReview: false,
                curatedPayee: "Corner Cafe");
        }

        // The "current" row has the SAME (resolved-payee, category)
        // as the prior rows: override → "Corner Cafe" and
        // counterparty leg on Coffee. Nothing new to suggest.
        var currentId = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true,
            curatedPayee: "Corner Cafe");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, currentId)))!;
        Assert.Empty(suggestions);
    }

    /// <summary>
    /// Recall anchors on the BANK's payee even when the anchor row has itself
    /// been renamed — including renamed to nothing.
    /// </summary>
    /// <remarks>
    /// <para>Migration 230 put the curated name on <c>txn_headers.payee</c> and
    /// moved the feed's to <c>txn_header_originals</c>. Recall is the one feature
    /// that reads both, in opposite directions: the bank's text is the KEY it
    /// searches by, the user's is the ANSWER it suggests. Keying on the curated
    /// name instead would match only rows nobody renamed, and the panel would go
    /// quietly empty rather than fail.</para>
    ///
    /// <para>The cleared case is the sharper one: a gate that asks "does this row
    /// have a payee to search by" must not read only the header's column. Since
    /// migration 230 a cleared payee makes that column NULL while the bank's text
    /// sits in the sidecar with recall
    /// perfectly possible. Reachable through MCP or a direct PATCH — the SPA
    /// always pairs a save with approve, which takes the row out of scope.</para>
    /// </remarks>
    [Fact]
    public async Task Anchors_on_the_bank_payee_when_the_anchor_row_was_itself_renamed()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        // The anchor: same bank payee, still awaiting review, but already renamed
        // to something that matches NO prior row.
        var renamed = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true,
            curatedPayee: "a name nothing else uses");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, renamed)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal("Corner Cafe", single.Payee);
        Assert.Equal(coffee.Id, single.CounterpartyAccountId);
    }

    [Fact]
    public async Task Anchors_on_the_bank_payee_when_the_anchor_rows_payee_was_cleared()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");

        await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.50m,
            new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: false,
            curatedPayee: "Corner Cafe");

        var cleared = await SeedBankFeedAsync(
            ledger, bank.Id, coffee.Id, -4.75m,
            new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            bankPayee: "CAFE PURCHASE", needsReview: true);
        // Clear it through the endpoint, which is the behaviour migration 230
        // made possible in the first place — presence, not nullness.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var clear = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/transactions/{cleared}")
        {
            Content = new StringContent(
                "{\"payee\": null}", System.Text.Encoding.UTF8, "application/json"),
        });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        var suggestions = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, cleared)))!;
        var single = Assert.Single(suggestions);
        Assert.Equal("Corner Cafe", single.Payee);
    }

    /// <summary>
    /// A feed row synced before the feed sent payees, recategorized while it awaited
    /// review, and then given its payee by the SimpleFIN pre-split backfill: its originals
    /// are restated with the new payee (ADR-0114 D15), so recall answers for it as it does
    /// for a row synced with the payee and never edited.
    /// </summary>
    /// <remarks>Every row is written by the sync and the PATCH endpoint.</remarks>
    [Fact]
    public async Task A_row_the_pre_split_backfill_restated_recalls_as_a_row_with_no_originals()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var keys = Db.NewLedgerKeyService();
        await using (var seed = Db.NewDbContext())
        {
            var row = await seed.Ledgers.SingleAsync(l => l.Id == ledger.LedgerId);
            row.WrappedLek = keys.CreateWrappedLek();
            row.LekKekId = keys.CurrentKekId;
            row.LekCreatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }
        var withPayees = false;
        string Transaction(string id, int posted, string description) => withPayees
            ? $$"""{"id":"{{id}}","posted":{{posted}},"amount":"-4.50","payee":"Corner Cafe","description":"{{description}}"}"""
            : $$"""{"id":"{{id}}","posted":{{posted}},"amount":"-4.50","description":"{{description}}"}""";
        string Body() => $$"""
            {"connections":[
              {"conn_id":"c-S","name":"Bank S","org_id":"banks","sfin_url":"https://sfin/banks"}
            ],"errlist":[],"accounts":[
              {"id":"sf-s","conn_id":"c-S","name":"Checking","currency":"USD","balance":"0","transactions":[
                {{string.Join(",", withPayees
                    ? new[] { Transaction("t-1", 1715000000, "CAFE RAW 1"), Transaction("t-2", 1715100000, "CAFE RAW 2"),
                              Transaction("t-3", 1715200000, "CAFE RAW 3") }
                    : new[] { Transaction("t-1", 1715000000, "CAFE RAW 1") })}}
              ]}
            ]}
            """;
        await using var factory = new ApiFactory(Db).WithoutDevAuth()
            .WithService<Coffer.Api.Sync.SimpleFin.SimpleFinClient>(_ => new Coffer.Api.Sync.SimpleFin.SimpleFinClient(
                new HttpClient(new StubHandler(req => req.Method == HttpMethod.Post
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("https://u:p@bridge.simplefin.org/simplefin/access/abc") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body()) })),
                TimeProvider.System));
        using var client = await AuthedClientAsync(factory, ledger);
        var connection = (await (await client.PostAsJsonAsync(
                $"/api/ledgers/{ledger.LedgerId}/feed-connections",
                new CreateFeedConnectionRequest { SetupToken = SetupToken("https://x/c") }))
            .Content.ReadFromJsonAsync<FeedConnectionSummary>())!;
        var bank = await ledger.AddBankAccountAsync("checking");
        var coffee = await ledger.AddCategoryAsync("Coffee");
        var snacks = await ledger.AddCategoryAsync("Snacks");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/ledgers/{ledger.LedgerId}/accounts/{bank.Id}/feed-mapping")
        {
            Content = JsonContent.Create(new PatchAccountFeedMappingRequest
            {
                FeedConnectionId = connection.Id,
                SimpleFinAccountId = "sf-s",
            }),
        })).StatusCode);
        var syncUrl = $"/api/ledgers/{ledger.LedgerId}/feed-connections/{connection.Id}/sync";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(syncUrl, content: null)).StatusCode);

        async Task<(Guid Header, Guid BankLeg)> RowAsync(string externalId)
        {
            await using var db = Db.NewDbContext();
            var header = await db.TxnHeaders.AsNoTracking()
                .SingleAsync(h => h.LedgerId == ledger.LedgerId && h.ExternalId == externalId);
            var leg = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.HeaderId == header.Id && l.AccountId == bank.Id);
            return (header.Id, leg.Id);
        }
        async Task CategorizeAsync(string externalId, Guid category, bool approve)
        {
            var (header, bankLeg) = await RowAsync(externalId);
            var resp = await client.PatchAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions/{header}",
                new PatchTransactionRequest
                {
                    Approve = approve ? true : null,
                    Postings = new PatchTransactionPostings
                    {
                        SourceAccountId = bank.Id,
                        Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = category, Amount = -4.50m }],
                    },
                });
            Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }

        // Edited before the payee arrives: its originals keep the description as payee.
        await CategorizeAsync("t-1", snacks.Id, approve: false);
        withPayees = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(syncUrl, content: null)).StatusCode);
        // A prior row, synced with the payee, approved under a category.
        await CategorizeAsync("t-2", coffee.Id, approve: true);

        var restated = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, (await RowAsync("t-1")).Header)))!;
        var neverEdited = (await client.GetFromJsonAsync<List<SimilarPayeeDto>>(
            Url(ledger.LedgerId, (await RowAsync("t-3")).Header)))!;

        var single = Assert.Single(neverEdited);
        Assert.Equal(("Corner Cafe", coffee.Id), (single.Payee, single.CounterpartyAccountId));
        Assert.Equal(
            neverEdited.Select(s => (s.Payee, s.CounterpartyAccountId)),
            restated.Select(s => (s.Payee, s.CounterpartyAccountId)));
    }

    private static string SetupToken(string claimUrl) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(claimUrl))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_impl(request));
    }
}
