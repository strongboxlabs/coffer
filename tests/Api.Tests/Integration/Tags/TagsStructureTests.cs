using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Errors;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// What the Tags tab of Structure needs from the API (ADR-0111): a tag made by name, a count of
/// distinct transactions beside the per-pairing usage, and the lines one tag is on across every
/// account. Each test builds its own ledger; reads are scoped by that ledger's id.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TagsStructureTests : DatabaseTest
{
    public TagsStructureTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookieValue}");
        return client;
    }

    /// <summary>A client signed in as another member of the ledger, with whatever role they were given.</summary>
    private async Task<HttpClient> ClientForMemberAsync(ApiFactory factory, Guid userId)
    {
        var (plaintext, hash) = SessionService.GenerateCookieValue();
        await using (var db = Db.NewServiceDbContext())
        {
            db.AuthSessions.Add(new SessionRow
            {
                Id = Guid.NewGuid(), UserId = userId, SessionHash = hash, ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={plaintext}");
        return client;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage resp)
    {
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Tags(SyntheticLedger l) => $"/api/ledgers/{l.LedgerId}/tags";

    private static async Task<List<TagDto>> ListAsync(HttpClient client, SyntheticLedger l) =>
        (await client.GetFromJsonAsync<List<TagDto>>(Tags(l)))!;

    // ----- create ---------------------------------------------------------------

    [Fact]
    public async Task Create_makes_a_tag_that_nothing_uses_and_the_list_shows_it()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("  Holiday  ", null));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var made = (await resp.Content.ReadFromJsonAsync<TagDto>())!;
        Assert.Equal("Holiday", made.Name);
        Assert.Null(made.Color);
        Assert.Equal(0, made.UsageCount);
        Assert.Equal(0, made.TransactionCount);
        Assert.EndsWith($"/tags/{made.Id}", resp.Headers.Location!.ToString(), StringComparison.Ordinal);

        var listed = Assert.Single(await ListAsync(client, ledger));
        Assert.Equal(made.Id, listed.Id);
    }

    [Fact]
    public async Task Create_stores_a_colour_lower_cased()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("Trip", "#EF4444"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal("#ef4444", Assert.Single(await ListAsync(client, ledger)).Color);
    }

    [Fact]
    public async Task Create_refuses_a_name_another_tag_has_apart_from_case_and_spacing()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("Holiday", null))).StatusCode);

        foreach (var again in new[] { "Holiday", "holiday", "  HOLIDAY ", "HoLiDaY" })
        {
            var resp = await client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest(again, null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Equal(BusinessError.Codes.TagNameExists, await ErrorCodeAsync(resp));
        }

        Assert.Equal("Holiday", Assert.Single(await ListAsync(client, ledger)).Name);
    }

    [Fact]
    public async Task Create_refuses_an_empty_name_an_overlong_name_and_a_malformed_colour()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        async Task<string?> CodeOf(CreateTagRequest body)
        {
            var resp = await client.PostAsJsonAsync(Tags(ledger), body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            return await ErrorCodeAsync(resp);
        }

        Assert.Equal(BusinessError.Codes.TransactionTagEmpty, await CodeOf(new CreateTagRequest(null, null)));
        Assert.Equal(BusinessError.Codes.TransactionTagEmpty, await CodeOf(new CreateTagRequest("   ", null)));
        Assert.Equal(BusinessError.Codes.TransactionTagTooLong,
            await CodeOf(new CreateTagRequest(new string('x', 65), null)));
        Assert.Equal(BusinessError.Codes.TagColorInvalid, await CodeOf(new CreateTagRequest("Trip", "red")));
        Assert.Equal(BusinessError.Codes.TagColorInvalid, await CodeOf(new CreateTagRequest("Trip", "")));

        // A refusal makes nothing, so the name is still free.
        Assert.Empty(await ListAsync(client, ledger));
    }

    [Fact]
    public async Task Create_accepts_the_longest_name_a_tag_may_have()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest(new string('x', 64), null));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Two_creates_of_one_new_name_at_once_make_one_tag_and_refuse_the_other()
    {
        // The name key is unique per ledger, and the context quietly drops a pending tag whose key
        // another writer committed first. Without the check after the save, the loser would report
        // 201 for a tag that was never inserted.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            client.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("Race", null))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r =>
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.Equal("Race", Assert.Single(await ListAsync(client, ledger)).Name);
    }

    [Fact]
    public async Task A_viewer_cannot_create_a_tag_and_an_editor_can()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewerId = await ledger.AddMemberAsync("viewer");
        var editorId = await ledger.AddMemberAsync("editor");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();

        using (var viewer = await ClientForMemberAsync(factory, viewerId))
        {
            var refused = await viewer.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("Nope", null));
            Assert.False(refused.IsSuccessStatusCode, $"a viewer's create returned {refused.StatusCode}");
            Assert.NotEqual(HttpStatusCode.InternalServerError, refused.StatusCode);
            // They may still read the dictionary.
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Tags(ledger))).StatusCode);
        }
        using (var editor = await ClientForMemberAsync(factory, editorId))
        {
            Assert.Equal(HttpStatusCode.Created,
                (await editor.PostAsJsonAsync(Tags(ledger), new CreateTagRequest("Yes", null))).StatusCode);
        }

        await using var db = Db.NewServiceDbContext();
        Assert.Equal(["Yes"], await db.Tags.AsNoTracking().Where(t => t.LedgerId == ledger.LedgerId)
            .Select(t => t.Name).ToListAsync());
    }

    // ----- the count of transactions --------------------------------------------

    [Fact]
    public async Task A_transfer_tagged_on_both_sides_is_two_pairings_and_one_transaction()
    {
        // The per-pairing figure is what makes a tag safe to delete: nothing points at it when it is
        // zero. The transaction figure is what a reader means by "how many". For a transfer tagged
        // from each account's register the two differ, and they have to.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("checking");
        var b = await ledger.AddBankAccountAsync("savings");
        var (headerOrLeg, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, -25m, Utc(3));
        await ledger.SetSideTagsAsync(headerOrLeg, a.Id, ["trip"], a.Id);
        await ledger.SetSideTagsAsync(headerOrLeg, b.Id, ["trip"], a.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var trip = Assert.Single(await ListAsync(client, ledger), t => t.Name == "trip");
        Assert.Equal(2, trip.UsageCount);
        Assert.Equal(1, trip.TransactionCount);
    }

    [Fact]
    public async Task The_count_is_per_tag_and_a_tag_on_no_transaction_counts_zero()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var (h2, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(3));
        await ledger.SetSideTagsAsync(h1, bank.Id, ["work", "home"], bank.Id);
        await ledger.SetSideTagsAsync(h2, bank.Id, ["work"], bank.Id);
        await ledger.AddBareTagAsync("archived");

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var byName = (await ListAsync(client, ledger)).ToDictionary(t => t.Name, t => t.TransactionCount);
        Assert.Equal(2, byName["work"]);
        Assert.Equal(1, byName["home"]);
        Assert.Equal(0, byName["archived"]);
    }

    [Fact]
    public async Task The_count_leaves_out_a_hidden_a_merged_away_and_a_template_transaction()
    {
        // A register shows none of them, so counting them would say "4 transactions" above a list
        // of one. The per-pairing usage still counts them: they are still tied to the tag.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var cat = await ledger.AddCategoryAsync("Dining", "expense");
        var (live, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -10m, Utc(2));
        var (hidden, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -20m, Utc(3));
        var (merged, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -30m, Utc(4));
        var (template, _) = await ledger.AddTransactionPairAsync(bank.Id, cat.Id, -40m, Utc(5));
        foreach (var leg in new[] { live, hidden, merged, template })
            await ledger.SetSideTagsAsync(leg, bank.Id, ["work"], bank.Id);

        await using (var db = Db.NewServiceDbContext())
        {
            async Task<Guid> HeaderOf(Guid leg) =>
                await db.TxnLegs.Where(l => l.Id == leg).Select(l => l.HeaderId).SingleAsync();
            var winner = await HeaderOf(live);
            var hiddenHeader = await HeaderOf(hidden);
            var mergedHeader = await HeaderOf(merged);
            var templateHeader = await HeaderOf(template);
            await db.TxnHeaders.Where(h => h.Id == hiddenHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsHidden, true));
            await db.TxnHeaders.Where(h => h.Id == mergedHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsMergedInto, (Guid?)winner));
            await db.TxnHeaders.Where(h => h.Id == templateHeader)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.IsRecurringTemplate, true));
        }

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var work = Assert.Single(await ListAsync(client, ledger), t => t.Name == "work");
        Assert.Equal(1, work.TransactionCount);
        Assert.Equal(4, work.UsageCount);
    }

    [Fact]
    public async Task A_tag_on_a_splits_header_is_one_transaction_and_its_lines_are_listed()
    {
        // A split keeps its transaction-level tags on the header, not on any posting. The count
        // reads what a register shows, so the header's tag has to reach the lines it is in play on.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        var fuel = await ledger.AddCategoryAsync("Fuel", "expense");
        var (origins, _) = await ledger.AddMultiSplitAsync(
            bank.Id, [(dining.Id, -10m), (fuel.Id, -20m)], Utc(6), payee: "Roadside");
        await ledger.SetSideTagsAsync(origins[0], bank.Id, ["road-trip"], bank.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var trip = Assert.Single(await ListAsync(client, ledger), t => t.Name == "road-trip");
        Assert.Equal(1, trip.TransactionCount);
        var page = (await client.GetFromJsonAsync<TransactionLinesResult>(
            $"{Tags(ledger)}/{trip.Id}/transactions"))!;
        Assert.NotEmpty(page.Lines);
        Assert.Single(page.Lines.Select(l => l.HeaderId).Distinct());
        Assert.All(page.Lines, l => Assert.Contains("road-trip", l.Tags));
    }

    [Fact]
    public async Task The_count_is_the_number_of_transactions_the_tags_own_list_shows()
    {
        // The number above a tag's page and the page have to agree, whatever shape the tagging
        // took. Each tag below is tagged a different way; for every one, the count is the number
        // of distinct transactions in the unfiltered list, and the list is what a register shows.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("checking");
        var b = await ledger.AddBankAccountAsync("savings");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");

        // Tagged from each side of a transfer.
        var (both, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, -25m, Utc(3));
        await ledger.SetSideTagsAsync(both, a.Id, ["both-sides"], a.Id);
        await ledger.SetSideTagsAsync(both, b.Id, ["both-sides"], a.Id);

        // Tagged on the receiving side of a transfer only: that account's register shows it.
        var (inflow, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, -35m, Utc(4));
        await ledger.SetSideTagsAsync(inflow, b.Id, ["receiving-side"], a.Id);

        // A money line with its own tags, and the category side tagged differently: the register
        // shows the line's own, so the category's is behind them.
        var (overridden, _) = await ledger.AddTransactionPairAsync(a.Id, dining.Id, -12m, Utc(5));
        await ledger.SetSideTagsAsync(overridden, a.Id, ["own-tag"], a.Id);
        await ledger.SetSideTagsAsync(overridden, dining.Id, ["category-side"], a.Id);

        // A money line with no tags of its own shows its category side's, so that tag is on it.
        var (inherited, _) = await ledger.AddTransactionPairAsync(a.Id, dining.Id, -7m, Utc(6));
        await ledger.SetSideTagsAsync(inherited, dining.Id, ["category-inherited"], a.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var tags = await ListAsync(client, ledger);
        foreach (var tag in tags)
        {
            var page = (await client.GetFromJsonAsync<TransactionLinesResult>(
                $"{Tags(ledger)}/{tag.Id}/transactions"))!;
            Assert.True(
                tag.TransactionCount == page.Lines.Select(l => l.HeaderId).Distinct().Count(),
                $"'{tag.Name}': the count says {tag.TransactionCount}, the list shows "
                + $"{page.Lines.Select(l => l.HeaderId).Distinct().Count()}");
        }

        int Count(string name) => tags.Single(t => t.Name == name).TransactionCount;
        Assert.Equal(1, Count("both-sides"));
        Assert.Equal(1, Count("receiving-side"));
        Assert.Equal(1, Count("own-tag"));
        // Behind the line's own tags, on a side no list has a line for: not the tag of a line.
        Assert.Equal(0, Count("category-side"));
        // The same side with nothing of the money line's own in front of it: the line shows it.
        Assert.Equal(1, Count("category-inherited"));
    }

    [Fact]
    public async Task Each_side_of_a_transfer_lists_under_the_tags_its_own_register_shows()
    {
        // Each side of a transfer keeps its own tags (ADR-0108), and an account's register shows
        // its own. The page reads each line from its own side, so with different tags on the two
        // sides each tag lists the line in the register that shows it, and not the other's. Read
        // from the sending side for both lines, as the ledger-wide listing does by default, the
        // receiving side's tag would be found on neither.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var a = await ledger.AddBankAccountAsync("checking");
        var b = await ledger.AddBankAccountAsync("savings");
        var (transfer, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, -35m, Utc(4), payee: "Move");
        await ledger.SetSideTagsAsync(transfer, a.Id, ["sending-side"], a.Id);
        await ledger.SetSideTagsAsync(transfer, b.Id, ["receiving-side"], a.Id);

        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var tags = await ListAsync(client, ledger);

        async Task<TransactionLine> OnlyLineOf(string name)
        {
            var tag = Assert.Single(tags, t => t.Name == name);
            Assert.Equal(1, tag.TransactionCount);
            var page = (await client.GetFromJsonAsync<TransactionLinesResult>(
                $"{Tags(ledger)}/{tag.Id}/transactions"))!;
            return Assert.Single(page.Lines);
        }

        var receiving = await OnlyLineOf("receiving-side");
        Assert.Equal(b.Id, receiving.AccountId);
        Assert.Equal(35m, receiving.Amount);
        Assert.Equal(["receiving-side"], receiving.Tags);

        var sending = await OnlyLineOf("sending-side");
        Assert.Equal(a.Id, sending.AccountId);
        Assert.Equal(-35m, sending.Amount);
        Assert.Equal(["sending-side"], sending.Tags);
    }

    // ----- the tag's transactions -----------------------------------------------

    private sealed record Seeded(
        SyntheticLedger Ledger, Guid Trip, Guid Checking, Guid Savings, Guid Dining);

    /// <summary>
    /// Three spending lines in two accounts, two tagged "trip" (one in each account) and one not,
    /// each with its own payee, so a filter can be told from a list that ignores it.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var checking = await ledger.AddBankAccountAsync("checking");
        var savings = await ledger.AddBankAccountAsync("savings");
        var dining = await ledger.AddCategoryAsync("Dining", "expense");
        var (h1, _) = await ledger.AddTransactionPairAsync(checking.Id, dining.Id, -10m, Utc(2), payee: "Bistro");
        var (h2, _) = await ledger.AddTransactionPairAsync(savings.Id, dining.Id, -20m, Utc(5), payee: "Cafe");
        await ledger.AddTransactionPairAsync(checking.Id, dining.Id, -30m, Utc(7), payee: "Deli");
        await ledger.SetSideTagsAsync(h1, checking.Id, ["trip"], checking.Id);
        await ledger.SetSideTagsAsync(h2, savings.Id, ["trip"], savings.Id);
        await using var db = Db.NewServiceDbContext();
        var trip = await db.Tags.AsNoTracking()
            .Where(t => t.LedgerId == ledger.LedgerId && t.Name == "trip").Select(t => t.Id).SingleAsync();
        return new Seeded(ledger, trip, checking.Id, savings.Id, dining.Id);
    }

    private static string TripLines(Seeded s, string query = "") =>
        $"{Tags(s.Ledger)}/{s.Trip}/transactions{query}";

    [Fact]
    public async Task A_tags_transactions_come_from_every_account_newest_first()
    {
        var s = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, s.Ledger);

        var page = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s)))!;

        Assert.Equal(["Cafe", "Bistro"], page.Lines.Select(l => l.Payee!).ToArray());
        Assert.Equal([s.Savings, s.Checking], page.Lines.Select(l => l.AccountId).ToArray());
        Assert.Equal([-20m, -10m], page.Lines.Select(l => l.Amount).ToArray());
        Assert.All(page.Lines, l => Assert.Equal("Dining", l.CounterpartyAccountName));
        Assert.All(page.Lines, l => Assert.Contains("trip", l.Tags));
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task A_tags_transactions_can_be_narrowed_to_one_account_a_range_or_a_word()
    {
        var s = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, s.Ledger);

        async Task<string[]> Payees(string query) =>
            (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, query)))!
                .Lines.Select(l => l.Payee!).ToArray();

        Assert.Equal(["Bistro"], await Payees($"?account_id={s.Checking}"));
        Assert.Equal(["Cafe"], await Payees($"?account_id={s.Savings}"));
        Assert.Equal(["Cafe"], await Payees("?date_from=2026-01-03"));
        Assert.Equal(["Bistro"], await Payees("?date_to=2026-01-04"));
        // Inclusive at both ends: a line on the day it is asked from, and on the day it is asked to.
        Assert.Equal(["Cafe"], await Payees("?date_from=2026-01-05&date_to=2026-01-05"));
        Assert.Equal(["Cafe"], await Payees("?search=caf"));
        // The untagged line is never there, whatever the filter says.
        Assert.Empty(await Payees("?search=Deli"));
    }

    [Fact]
    public async Task A_tags_transactions_are_paged_and_say_whether_another_page_follows()
    {
        var s = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, s.Ledger);

        var first = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, "?limit=1")))!;
        Assert.Equal(["Cafe"], first.Lines.Select(l => l.Payee!).ToArray());
        Assert.True(first.HasMore);

        var second = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, "?limit=1&offset=1")))!;
        Assert.Equal(["Bistro"], second.Lines.Select(l => l.Payee!).ToArray());
        Assert.False(second.HasMore);

        // Nonsense is made sensible, not refused: a negative offset is the start, a zero limit is one.
        var odd = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, "?limit=0&offset=-4")))!;
        Assert.Single(odd.Lines);
        Assert.Equal(1, odd.Limit);
        Assert.Equal(0, odd.Offset);

        // The endpoint's own ceiling is 200, below the 500 the query itself would allow.
        var huge = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, "?limit=100000")))!;
        Assert.Equal(200, huge.Limit);

        // Past the end is an empty page that says nothing follows, not an error.
        var beyond = (await client.GetFromJsonAsync<TransactionLinesResult>(TripLines(s, "?offset=10")))!;
        Assert.Empty(beyond.Lines);
        Assert.False(beyond.HasMore);
    }

    [Fact]
    public async Task A_range_ending_on_the_last_day_there_is_has_no_upper_bound_and_is_not_an_error()
    {
        // "Inclusive of the end day" is the day after as an exclusive bound, and the last day there
        // is has no day after it. It used to throw; it bounds nothing, as no line is later.
        var s = await SeedAsync();
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, s.Ledger);

        var resp = await client.GetAsync(TripLines(s, "?date_from=0001-01-01&date_to=9999-12-31"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<TransactionLinesResult>())!.Lines.Count);
    }

    [Fact]
    public async Task An_unknown_tag_and_another_ledgers_tag_are_both_not_found()
    {
        var s = await SeedAsync();
        var other = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, other);

        // The tag exists, in a ledger this reader has no part in, and is asked for under a ledger
        // they do. Row-level security hides the other ledger's tag from them either way.
        var foreign = await client.GetAsync($"{Tags(other)}/{s.Trip}/transactions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(foreign));

        var unknown = await client.GetAsync($"{Tags(other)}/{Guid.NewGuid()}/transactions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(unknown));
    }

    [Fact]
    public async Task A_member_of_two_ledgers_cannot_read_one_ledgers_tag_under_the_others_url()
    {
        // The case row-level security cannot refuse: a reader who can see both ledgers, and the
        // tag of one asked for through the other. Only the endpoint's own check that the tag
        // belongs to the ledger in the URL stands between them and a 200 with an empty list.
        var s = await SeedAsync();
        var other = await SyntheticLedger.CreateAsync(Db);
        var memberId = await s.Ledger.AddMemberAsync("viewer");
        await using (var db = Db.NewServiceDbContext())
        {
            db.UserLedgerGrants.Add(new UserLedgerGrantRow
            {
                UserId = memberId, LedgerId = other.LedgerId, Role = "viewer",
            });
            await db.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var member = await ClientForMemberAsync(factory, memberId);

        // Both ledgers answer for their own tag...
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(TripLines(s))).StatusCode);
        // ...and neither answers for the other's.
        var crossed = await member.GetAsync($"{Tags(other)}/{s.Trip}/transactions");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossed.StatusCode);
        Assert.Equal(BusinessError.Codes.TagNotFound, await ErrorCodeAsync(crossed));
    }

    [Fact]
    public async Task A_viewer_can_read_a_tags_transactions()
    {
        var s = await SeedAsync();
        var viewerId = await s.Ledger.AddMemberAsync("viewer");
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var viewer = await ClientForMemberAsync(factory, viewerId);

        var resp = await viewer.GetAsync(TripLines(s));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, (await resp.Content.ReadFromJsonAsync<TransactionLinesResult>())!.Lines.Count);
    }

    [Fact]
    public async Task A_stranger_cannot_read_a_ledgers_tag_transactions()
    {
        var s = await SeedAsync();
        var stranger = await SyntheticLedger.CreateAsync(Db);
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, stranger);

        var resp = await client.GetAsync(TripLines(s));

        Assert.False(resp.IsSuccessStatusCode, $"a stranger's read returned {resp.StatusCode}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
    }
}
