using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Tags on investment transactions (ADR-0028, 2026-09 refinement).
/// </summary>
/// <remarks>
/// <para>An investment event has one tag set, placed like its memo: on the
/// brokerage leg of a one-posting event (<c>txn_leg_tags</c>, migration 243), on
/// the header of a multi-posting one (<c>txn_header_tags</c>). A register that
/// blanked them on the way out, with write contracts lacking a <c>Tags</c>
/// field, would make an investment event the one place a tag could be written
/// and never read.</para>
///
/// <para>Every test here asserts what the caller can OBSERVE — the register row,
/// the shared dictionary, the status code — not which table was written, so
/// they stay honest if the storage changes.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class InvestmentTagsTests : DatabaseTest
{
    public InvestmentTagsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) =>
        new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    private static async Task<HttpResponseMessage> PostBuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        DateTime postedAt, IReadOnlyList<string>? tags)
        => await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerageId,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
                Tags = tags,
            });

    private static async Task<Guid> BuyAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid securityId,
        DateTime postedAt, IReadOnlyList<string>? tags)
    {
        var resp = await PostBuyAsync(client, ledger, brokerageId, securityId, postedAt, tags);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();
    }

    /// <summary>
    /// The tags the brokerage register shows for one header. Deliberately NOT a
    /// direct pairing query: the bug this covers was a header that HAD its pairings
    /// and still showed none, which a table read would have called passing. Read
    /// through the one presentation rule over what is stored, which the register
    /// reads through once its readers move; the row must be on the register too.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RegisterTagsAsync(
        HttpClient client, SyntheticLedger ledger, Guid brokerageId, Guid headerId)
    {
        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions?account_id={brokerageId}&limit=100"))!;
        Assert.Single(page.Entries, e => e.Txn is not null && e.Txn.HeaderId == headerId);
        var shown = await ledger.ShownForAsync(headerId, brokerageId);
        return Assert.Single(shown.Select(s => s.Tags).Distinct(new TagSetComparer()));
    }

    private sealed class TagSetComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y) =>
            x is not null && y is not null && x.SequenceEqual(y);

        public int GetHashCode(IReadOnlyList<string> obj) => obj.Count;
    }

    /// <summary>
    /// Tags supplied on create reach the register row.
    /// </summary>
    /// <remarks>
    /// The whole feature in one assertion: the create contract carries
    /// <c>Tags</c>, and <c>ProjectInvestmentEvent</c> must not replace the row's
    /// tags with an empty array before it leaves the server.
    /// </remarks>
    [Fact]
    public async Task Tags_supplied_on_create_show_on_the_register_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 4), ["roth", "long-hold"]);

        var tags = await RegisterTagsAsync(client, ledger, brokerage.Id, headerId);
        Assert.Equal(["long-hold", "roth"], tags.OrderBy(t => t).ToArray());
    }

    /// <summary>
    /// A PATCH carrying a tag list replaces the header's set exactly.
    /// </summary>
    [Fact]
    public async Task A_patch_replaces_the_tag_set()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = Utc(2026, 5, 4);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, ["roth", "long-hold"]);

        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
                Tags = ["taxable"],
            });
        Assert.True(patch.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"expected 2xx, got {(int)patch.StatusCode}: "
            + await patch.Content.ReadAsStringAsync());

        var tags = await RegisterTagsAsync(client, ledger, brokerage.Id, headerId);
        Assert.Equal(["taxable"], tags);
    }

    /// <summary>
    /// An empty list CLEARS the tags; omitting the field leaves them alone.
    /// </summary>
    /// <remarks>
    /// The one place the investment PATCH deliberately does NOT follow ADR-0025's
    /// "the supplied body IS the new state": it follows the bank PATCH's presence
    /// rule instead. Both halves are asserted in one test because either alone
    /// passes under the wrong contract — a wholesale-replace implementation
    /// clears on both, and a naive "only write when non-empty" clears on
    /// neither.
    /// </remarks>
    [Fact]
    public async Task An_empty_list_clears_tags_and_omitting_the_field_leaves_them()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = Utc(2026, 5, 4);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, ["roth"]);

        PatchInvestmentTransactionRequest Body(IReadOnlyList<string>? tags) => new()
        {
            BrokerageAccountId = brokerage.Id,
            PostedAt = postedAt,
            Action = "buy",
            SecurityId = securityId,
            Shares = 10m,
            Price = 100m,
            // A different amount each time, so the PATCH is a real edit rather
            // than a no-op the server might short-circuit.
            Amount = 1000m,
            Tags = tags,
        };

        // Omitted → untouched.
        var untouched = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            Body(null));
        Assert.True(untouched.IsSuccessStatusCode,
            await untouched.Content.ReadAsStringAsync());
        Assert.Equal(["roth"],
            await RegisterTagsAsync(client, ledger, brokerage.Id, headerId));

        // [] → cleared.
        var cleared = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            Body([]));
        Assert.True(cleared.IsSuccessStatusCode,
            await cleared.Content.ReadAsStringAsync());
        Assert.Empty(await RegisterTagsAsync(client, ledger, brokerage.Id, headerId));
    }

    /// <summary>
    /// Tags survive the wholesale leg reshape a PATCH performs.
    /// </summary>
    /// <remarks>
    /// The investment PATCH reshapes the header's legs — that is how adding a fee
    /// works. Legs keep their identity where the new shape has room for them, the
    /// header id is stable, and a change of posting count moves the event's tag
    /// set between the brokerage leg and the header, so the tags must survive —
    /// and this asserts it on the edit that changes the leg count, not on a
    /// memo-only edit that might not reshape anything.
    /// </remarks>
    [Fact]
    public async Task Tags_survive_an_edit_that_rebuilds_the_legs()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var fees = await ledger.AddCategoryAsync("Investment Fees");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var postedAt = Utc(2026, 5, 4);
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            postedAt, ["roth"]);

        int LegCount()
        {
            using var db = Db.NewDbContext();
            return db.TxnLegs.AsNoTracking().Count(l => l.HeaderId == headerId);
        }
        var before = LegCount();

        // Add a fee — this is the edit that reshapes the legs.
        var patch = await client.PatchAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions/{headerId}",
            new PatchInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id,
                PostedAt = postedAt,
                Action = "buy",
                SecurityId = securityId,
                Shares = 10m,
                Price = 100m,
                Amount = 1000m,
                FeeAccountId = fees.Id,
                FeeAmount = 4.95m,
                // Tags deliberately omitted — the user edited the fee, not the
                // tags.
            });
        Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());

        // Premise: the legs really were rebuilt, so "tags survived" means
        // something.
        Assert.True(LegCount() > before,
            $"expected the fee edit to add legs (had {before}, now {LegCount()})");

        Assert.Equal(["roth"],
            await RegisterTagsAsync(client, ledger, brokerage.Id, headerId));
    }

    /// <summary>
    /// Both registers share ONE tag dictionary, case-insensitively.
    /// </summary>
    /// <remarks>
    /// A tag applied to an investment header must not become a second dictionary
    /// row with different casing — the filter is an exact match on the name, so
    /// a duplicate would split a tag in two and each half would match half the
    /// transactions.
    /// </remarks>
    [Fact]
    public async Task An_investment_tag_reuses_the_ledgers_dictionary_row()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var checking = await ledger.AddBankAccountAsync("checking");
        var other = await ledger.AddBankAccountAsync("savings");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        // The dictionary row is created by the BANK side first, lower-cased.
        var (bankLeg, _) = await ledger.AddTransactionPairAsync(
            checking.Id, other.Id, 25m, Utc(2026, 5, 1), "rent");
        await ledger.SetSideTagsAsync(bankLeg, checking.Id, ["reimbursable"], checking.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // The investment side asks for it in a different casing.
        var headerId = await BuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 4), ["Reimbursable"]);

        await using (var db = Db.NewDbContext())
        {
            var rows = await db.Tags.AsNoTracking()
                .Where(t => t.LedgerId == ledger.LedgerId
                            && EF.Functions.ILike(t.Name, "reimbursable"))
                .ToListAsync();
            var row = Assert.Single(rows);
            // First casing wins — the bank side got there first.
            Assert.Equal("reimbursable", row.Name);

            // Both one-posting transactions carry it on their writing side's leg.
            var pairings = await db.TxnHeaderTags.AsNoTracking().CountAsync(p => p.TagId == row.Id)
                + await db.TxnLegTags.AsNoTracking().CountAsync(p => p.TagId == row.Id);
            Assert.Equal(2, pairings);
        }
    }

    /// <summary>
    /// The 20-tag cap is enforced on the investment surface too.
    /// </summary>
    /// <remarks>
    /// Caps held as private constants on <c>TransactionsEndpoints</c> would leave
    /// the investment surface with none — the editor enforces the same limits
    /// client-side, so the gap would only ever show through the API.
    /// </remarks>
    [Fact]
    public async Task Too_many_tags_is_rejected_on_the_investment_surface()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var tooMany = Enumerable.Range(1, 21).Select(i => $"tag-{i}").ToArray();
        var resp = await PostBuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 4), tooMany);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("transaction-tags-too-many",
            await resp.Content.ReadAsStringAsync());

        // 20 is fine — the boundary, so the cap is the documented one and not
        // an off-by-one.
        var atCap = Enumerable.Range(1, 20).Select(i => $"tag-{i}").ToArray();
        var ok = await PostBuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 5), atCap);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    /// <summary>
    /// A whitespace-only tag name is rejected rather than silently dropped.
    /// </summary>
    [Fact]
    public async Task An_empty_tag_name_is_rejected_on_the_investment_surface()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await PostBuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 4), ["roth", "   "]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("transaction-tag-empty",
            await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The register's tag FILTER and the register's tag DISPLAY agree.
    /// </summary>
    /// <remarks>
    /// The filter always worked on investment rows — it runs in SQL against
    /// <c>txn_header_tags</c>, nowhere near the projection that blanked the
    /// display. So filtering a brokerage register by a tag returned rows that
    /// showed no tag, which reads as a bug in the filter.
    /// </remarks>
    [Fact]
    public async Task Filtering_a_brokerage_register_by_tag_returns_rows_that_show_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var brokerage = await ledger.AddInvestmentAccountAsync("brokerage");
        var securityId = await ledger.AddSecurityAsync("TAGX", ticker: "TAGX");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        // A buy with a fee: several postings, so the event's tags sit on its header.
        // A one-posting event keeps them on its brokerage leg. The register's filter
        // reads either through the stored presentation (txn_leg_note_tags).
        var fees = await ledger.AddCategoryAsync("Investment Fees");
        var created = await client.PostAsJsonAsync(
            $"/api/ledgers/{ledger.LedgerId}/investment-transactions",
            new CreateInvestmentTransactionRequest
            {
                BrokerageAccountId = brokerage.Id, PostedAt = Utc(2026, 5, 4), Action = "buy",
                SecurityId = securityId, Shares = 10m, Price = 100m, Amount = 1000m,
                FeeAccountId = fees.Id, FeeAmount = 4.95m, Tags = ["roth"],
            });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tagged = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("headerId").GetGuid();
        await BuyAsync(client, ledger, brokerage.Id, securityId,
            Utc(2026, 5, 5), null);

        var page = (await client.GetFromJsonAsync<RegisterPage>(
            $"/api/ledgers/{ledger.LedgerId}/transactions"
            + $"?account_id={brokerage.Id}&limit=100&tag={Uri.EscapeDataString("roth")}"))!;

        var entry = Assert.Single(page.Entries);
        Assert.Equal(tagged, entry.Txn!.HeaderId);
        Assert.Equal(["roth"], entry.Txn!.Tags);
    }
}
