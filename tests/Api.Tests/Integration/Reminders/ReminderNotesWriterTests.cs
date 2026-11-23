using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Reminders;

/// <summary>
/// Reminder templates carry memo and tags placed by the write primitive, keep them
/// through edits, and every posted occurrence starts with them.
/// </summary>
/// <remarks>
/// The reminder readers still read the template header until they move, so these
/// assert the STORED state of the template and of each occurrence.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReminderNotesWriterTests : DatabaseTest
{
    public ReminderNotesWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateOnly Start = new(2026, 1, 1);

    private sealed record World(
        SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client,
        Guid Bank, Guid Rent, Guid Utilities, Guid Brokerage, Guid Dividends, Guid Security) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Checking");
        var rent = await ledger.AddCategoryAsync("Rent");
        var utilities = await ledger.AddCategoryAsync("Utilities");
        var brokerage = await ledger.AddInvestmentAccountAsync("Brokerage");
        var dividends = await ledger.AddCategoryAsync("Dividends", "income");
        var security = await ledger.AddSecurityAsync("Index Fund", ticker: "IDXF");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, factory, client, bank.Id, rent.Id, utilities.Id, brokerage.Id, dividends.Id, security);
    }

    private static async Task<Guid> CreateBankAsync(World w, TransactionNotesRequest? notes, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Start,
            Payee = "Landlord",
            SourceAccountId = w.Bank,
            Postings = [.. postings.Select(p => new TransactionPosting { CounterpartyAccountId = p.Counterparty, Amount = p.Amount })],
            Notes = notes,
        });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
    }

    private async Task<Guid> TemplateOfAsync(Guid reminderId)
    {
        await using var db = Db.NewDbContext();
        return await db.RecurringTransactions.AsNoTracking().Where(r => r.Id == reminderId)
            .Select(r => r.TemplateHeaderId!.Value).SingleAsync();
    }

    private async Task<Guid?> OwnerAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().Where(h => h.Id == headerId).Select(h => h.OriginatingAccountId).SingleAsync();
    }

    private static Notes<string> On(Annotations<string> notes, int posting, Guid account) =>
        notes.For(new LegKey(posting, account));

    private Task AssertInvariantsAsync(World w) => OriginatingAccountCheck.AssertHoldsAsync(Db, w.Ledger.LedgerId);

    [Fact]
    public async Task A_posted_occurrence_starts_with_the_one_split_templates_notes_and_owner()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord", Tags = ["housing"] }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);

        var occurrence = await ReminderCloneFire.PostedAsync(Db, w.Ledger.LedgerId, reminder, Start);

        var notes = await w.Ledger.LoadNotesAsync(occurrence);
        Assert.Equal(await w.Ledger.LoadNotesAsync(template), notes);
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal(new Notes<string>("to landlord", ["housing"]), On(notes, 0, w.Bank));
        Assert.Equal(w.Bank, await OwnerAsync(occurrence));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_posted_occurrence_of_a_split_template_clones_its_header_and_line_notes()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "monthly bills", Tags = ["home"],
                Lines = [new TransactionLineNotesRequest { Index = 1, Memo = "power", Tags = ["energy"] }],
            },
            (w.Rent, -1500m), (w.Utilities, -80m));

        var occurrence = await ReminderCloneFire.PostedAsync(Db, w.Ledger.LedgerId, reminder, Start);

        var notes = await w.Ledger.LoadNotesAsync(occurrence);
        Assert.Equal(new Notes<string>("monthly bills", ["home"]), notes.Header);
        Assert.Equal(new Notes<string>("power", ["energy"]), On(notes, 1, w.Bank));
        Assert.True(On(notes, 0, w.Bank).IsEmpty);
        Assert.Equal(w.Bank, await OwnerAsync(occurrence));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Posting_an_edited_occurrence_writes_its_own_notes()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord" }, (w.Rent, -1500m));

        var fire = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}/fire/bank",
            new FireBankReminderRequest
            {
                OccurrenceDate = Start,
                SourceAccountId = w.Bank,
                Postings = [new TransactionPosting { CounterpartyAccountId = w.Rent, Amount = -1550m }],
                Notes = new TransactionNotesRequest { Side = w.Bank, Memo = "with late fee", Tags = ["late"] },
            });
        Assert.True(fire.IsSuccessStatusCode, await fire.Content.ReadAsStringAsync());
        var occurrence = (await fire.Content.ReadFromJsonAsync<FireReminderResponse>())!.HeaderId;

        Assert.Equal(new Notes<string>("with late fee", ["late"]), On(await w.Ledger.LoadNotesAsync(occurrence), 0, w.Bank));
        Assert.Equal(w.Bank, await OwnerAsync(occurrence));
        await AssertInvariantsAsync(w);
    }

    // ----------------------------------------------------------------------
    // Template lines carry identity: the detail names each leg, an edit names the
    // source leg of each line it keeps, and a line's notes follow that id only.
    // ----------------------------------------------------------------------

    private static async Task<ReminderDetail> DetailAsync(World w, Guid reminder) =>
        (await w.Client.GetFromJsonAsync<ReminderDetail>($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}"))!;

    /// <summary>The id of the leg on the series source of a line, as the detail reports it.</summary>
    private static Guid SourceLegOf(ReminderDetail detail, int posting) =>
        detail.Legs.Single(l => l.PostingIndex == posting && l.AccountId == detail.SourceAccountId).LegId;

    private static TransactionPosting Line(Guid counterparty, decimal amount, Guid? legId = null) =>
        new() { LegId = legId, CounterpartyAccountId = counterparty, Amount = amount };

    private static Task<HttpResponseMessage> EditLinesAsync(
        World w, Guid reminder, Guid source, TransactionNotesRequest? notes, params TransactionPosting[] items) =>
        w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest
            {
                Postings = new PatchReminderPostings { SourceAccountId = source, Items = items },
                Notes = notes,
            });

    /// <summary>The template, the source leg id of each of its lines, and the third line's category.</summary>
    private sealed record ThreeLines(
        Guid Reminder, Guid Template, Guid RentLine, Guid UtilitiesLine, Guid GroceriesLine, Guid Groceries);

    /// <summary>
    /// A three-line template, reached the way a user reaches one: a one-split with its
    /// source's notes and the category side's own memo, split from the source. Line 0
    /// (Rent) keeps both values, line 1 (Utilities) and line 2 (Groceries) get their
    /// own, and the whole transaction gets its own.
    /// </summary>
    private async Task<ThreeLines> ThreeLineTemplateAsync(World w)
    {
        var groceries = await w.Ledger.AddCategoryAsync("Groceries");
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "A", Tags = ["a"] }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);
        // The category side's own memo: an imported one-split template can carry one.
        await w.Ledger.SetSideMemoAsync(template, w.Rent, "flat", w.Bank);

        var split = await EditLinesAsync(w, reminder, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "bills", Tags = ["home"],
                Lines =
                [
                    new TransactionLineNotesRequest { Index = 1, Memo = "B", Tags = ["b"] },
                    new TransactionLineNotesRequest { Index = 2, Memo = "C" },
                ],
            },
            Line(w.Rent, -1500m, SourceLegOf(await DetailAsync(w, reminder), 0)),
            Line(w.Utilities, -80m),
            Line(groceries.Id, -50m));
        Assert.True(split.IsSuccessStatusCode, await split.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(template);
        Assert.Equal(new Notes<string>("bills", ["home"]), notes.Header);
        Assert.Equal(new Notes<string>("A", ["a"]), On(notes, 0, w.Bank));
        Assert.Equal(new Notes<string>("flat"), On(notes, 0, w.Rent));
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 1, w.Bank));
        Assert.Equal(new Notes<string>("C"), On(notes, 2, w.Bank));

        var detail = await DetailAsync(w, reminder);
        return new ThreeLines(reminder, template,
            SourceLegOf(detail, 0), SourceLegOf(detail, 1), SourceLegOf(detail, 2), groceries.Id);
    }

    [Fact]
    public async Task The_detail_names_every_template_leg_and_an_edit_answers_with_the_rebuilt_ones()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord" }, (w.Rent, -1500m), (w.Utilities, -80m));
        var template = await TemplateOfAsync(reminder);

        async Task<HashSet<(Guid, int, Guid)>> StoredLegsAsync()
        {
            await using var db = Db.NewDbContext();
            return (await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == template)
                .Select(l => new { l.Id, l.PostingIndex, l.AccountId }).ToListAsync())
                .Select(l => (l.Id, l.PostingIndex, l.AccountId)).ToHashSet();
        }

        var detail = await DetailAsync(w, reminder);
        Assert.Equal(await StoredLegsAsync(), detail.Legs.Select(l => (l.LegId, l.PostingIndex, l.AccountId)).ToHashSet());

        var edit = await EditLinesAsync(w, reminder, w.Bank, null,
            Line(w.Rent, -1600m, SourceLegOf(detail, 0)), Line(w.Utilities, -80m, SourceLegOf(detail, 1)));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        var answered = (await edit.Content.ReadFromJsonAsync<ReminderDetail>())!;
        Assert.Equal(await StoredLegsAsync(), answered.Legs.Select(l => (l.LegId, l.PostingIndex, l.AccountId)).ToHashSet());
    }

    [Fact]
    public async Task An_edit_that_names_a_one_line_templates_line_keeps_both_sides_notes()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord", Tags = ["housing"] }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);
        await w.Ledger.SetSideMemoAsync(template, w.Rent, "flat 2", w.Bank);

        // An unrelated edit (the payee) that sends the line back as it read it, then a
        // new amount: the legs are dropped and rebuilt both times.
        var payee = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest
            {
                Payee = "New landlord",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = w.Bank,
                    Items = [Line(w.Rent, -1500m, SourceLegOf(await DetailAsync(w, reminder), 0))],
                },
            });
        Assert.True(payee.IsSuccessStatusCode, await payee.Content.ReadAsStringAsync());
        var amount = await EditLinesAsync(w, reminder, w.Bank, null,
            Line(w.Rent, -1600m, SourceLegOf(await DetailAsync(w, reminder), 0)));
        Assert.True(amount.IsSuccessStatusCode, await amount.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(template);
        Assert.Equal(new Notes<string>("to landlord", ["housing"]), On(notes, 0, w.Bank));
        Assert.Equal(new Notes<string>("flat 2"), On(notes, 0, w.Rent));
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal(w.Bank, await OwnerAsync(template));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_line_sent_without_an_id_is_a_new_line_and_starts_blank()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord", Tags = ["housing"] }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);
        await w.Ledger.SetSideMemoAsync(template, w.Rent, "flat 2", w.Bank);

        // Same posting, same accounts, but no id: nothing says it is the stored line.
        var edit = await EditLinesAsync(w, reminder, w.Bank, null, Line(w.Rent, -1500m));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(template);
        Assert.True(On(notes, 0, w.Bank).IsEmpty);
        Assert.True(On(notes, 0, w.Rent).IsEmpty);
        Assert.True(notes.Header.IsEmpty);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Deleting_a_line_keeps_every_other_lines_notes()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);

        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(w.Utilities, -80m, t.UtilitiesLine), Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.Equal(new Notes<string>("bills", ["home"]), notes.Header);
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 0, w.Bank));
        Assert.True(On(notes, 0, w.Utilities).IsEmpty);
        Assert.Equal(new Notes<string>("C"), On(notes, 1, w.Bank));
        Assert.True(On(notes, 1, t.Groceries).IsEmpty);
        Assert.Equal(2 * 2, notes.Legs.Count);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_new_line_in_a_deleted_lines_place_starts_blank()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);

        // Line 0 deleted, and a new line on the same posting and accounts added.
        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(w.Rent, -1400m), Line(w.Utilities, -80m, t.UtilitiesLine), Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.Equal(new Notes<string>("bills", ["home"]), notes.Header);
        Assert.True(On(notes, 0, w.Bank).IsEmpty);
        Assert.True(On(notes, 0, w.Rent).IsEmpty);
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 1, w.Bank));
        Assert.Equal(new Notes<string>("C"), On(notes, 2, w.Bank));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Deleting_down_to_one_line_joins_the_transactions_notes_with_that_lines()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);

        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null, Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal(new Notes<string>("bills" + AnnotationText.CollapseSeparator + "C", ["home"]), On(notes, 0, w.Bank));
        Assert.True(On(notes, 0, t.Groceries).IsEmpty);
        Assert.Equal(w.Bank, await OwnerAsync(t.Template));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Reordering_lines_moves_their_notes_with_them()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);

        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(t.Groceries, -50m, t.GroceriesLine), Line(w.Rent, -1500m, t.RentLine), Line(w.Utilities, -80m, t.UtilitiesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.Equal(new Notes<string>("bills", ["home"]), notes.Header);
        Assert.Equal(new Notes<string>("C"), On(notes, 0, w.Bank));
        Assert.True(On(notes, 0, t.Groceries).IsEmpty);
        Assert.Equal(new Notes<string>("A", ["a"]), On(notes, 1, w.Bank));
        Assert.Equal(new Notes<string>("flat"), On(notes, 1, w.Rent));
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 2, w.Bank));
        Assert.True(On(notes, 2, w.Utilities).IsEmpty);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Changing_a_lines_account_keeps_its_notes_and_its_other_sides_override_follows_the_leg()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);
        var dining = await w.Ledger.AddCategoryAsync("Dining");

        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(dining.Id, -1500m, t.RentLine), Line(w.Utilities, -80m, t.UtilitiesLine), Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.Equal(new Notes<string>("A", ["a"]), On(notes, 0, w.Bank));
        Assert.Equal(new Notes<string>("flat"), On(notes, 0, dining.Id));
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 1, w.Bank));
        Assert.Equal(new Notes<string>("C"), On(notes, 2, w.Bank));
        Assert.DoesNotContain(notes.Legs, l => l.Key.AccountId == w.Rent);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Adding_a_line_starts_it_blank_and_keeps_every_other_lines_notes()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);
        var dining = await w.Ledger.AddCategoryAsync("Dining");

        // The new line goes first, onto the posting a stored line had.
        var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(dining.Id, -20m), Line(w.Rent, -1500m, t.RentLine), Line(w.Utilities, -80m, t.UtilitiesLine),
            Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(t.Template);
        Assert.Equal(new Notes<string>("bills", ["home"]), notes.Header);
        Assert.True(On(notes, 0, w.Bank).IsEmpty);
        Assert.True(On(notes, 0, dining.Id).IsEmpty);
        Assert.Equal(new Notes<string>("A", ["a"]), On(notes, 1, w.Bank));
        Assert.Equal(new Notes<string>("flat"), On(notes, 1, w.Rent));
        Assert.Equal(new Notes<string>("B", ["b"]), On(notes, 2, w.Bank));
        Assert.Equal(new Notes<string>("C"), On(notes, 3, w.Bank));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_line_id_that_is_not_a_source_leg_of_the_template_is_refused_and_nothing_is_written()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);
        var before = await w.Ledger.LoadNotesAsync(t.Template);
        var rentSide = (await DetailAsync(w, t.Reminder)).Legs.Single(l => l.AccountId == w.Rent).LegId;

        foreach (var stranger in new[] { rentSide, Guid.NewGuid() })
        {
            var edit = await EditLinesAsync(w, t.Reminder, w.Bank, null,
                Line(w.Rent, -1500m, stranger), Line(w.Utilities, -80m, t.UtilitiesLine));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
            Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.TransactionPostingLegNotInHeader, await CodeAsync(edit));
        }
        var twice = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(w.Rent, -1500m, t.RentLine), Line(w.Utilities, -80m, t.RentLine));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.TransactionPostingLegIdDuplicated, await CodeAsync(twice));

        Assert.Equal(before, await w.Ledger.LoadNotesAsync(t.Template));
        Assert.Equal(3, (await DetailAsync(w, t.Reminder)).Legs.Select(l => l.PostingIndex).Distinct().Count());
    }

    [Fact]
    public async Task A_line_id_from_before_the_previous_save_is_refused_and_nothing_is_written()
    {
        await using var w = await WorldAsync();
        var t = await ThreeLineTemplateAsync(w);
        // Every save rebuilds the legs, so an id read before it names no leg afterwards.
        var save = await EditLinesAsync(w, t.Reminder, w.Bank, null,
            Line(w.Rent, -1600m, t.RentLine), Line(w.Utilities, -80m, t.UtilitiesLine), Line(t.Groceries, -50m, t.GroceriesLine));
        Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
        var current = await DetailAsync(w, t.Reminder);
        var notesBefore = await w.Ledger.LoadNotesAsync(t.Template);

        var stale = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{t.Reminder}",
            new EditReminderRequest
            {
                Payee = "New landlord",
                Postings = new PatchReminderPostings
                {
                    SourceAccountId = w.Bank,
                    Items =
                    [
                        Line(w.Rent, -1700m, SourceLegOf(current, 0)),
                        Line(w.Utilities, -90m, t.UtilitiesLine),
                        Line(t.Groceries, -50m, SourceLegOf(current, 2)),
                    ],
                },
                Notes = new TransactionNotesRequest { Side = w.Bank, Memo = "changed", Tags = ["changed"] },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.TransactionPostingLegNotInHeader, await CodeAsync(stale));
        Assert.Equal(notesBefore, await w.Ledger.LoadNotesAsync(t.Template));
        var after = await DetailAsync(w, t.Reminder);
        Assert.Equal(current.Payee, after.Payee);
        Assert.Equal(
            current.Legs.Select(l => (l.LegId, l.PostingIndex, l.AccountId, l.Amount)).ToHashSet(),
            after.Legs.Select(l => (l.LegId, l.PostingIndex, l.AccountId, l.Amount)).ToHashSet());
        await using var db = Db.NewDbContext();
        Assert.False(await db.Tags.AnyAsync(g => g.LedgerId == w.Ledger.LedgerId && g.Name == "changed"));
    }

    // ----------------------------------------------------------------------
    // The detail carries each side's own tags beside its memo, so an editor seeded
    // from it sends the stored sets back instead of sets it never showed.
    // ----------------------------------------------------------------------

    private static List<string> Sorted(IEnumerable<string> tags) => [.. tags.Order(StringComparer.Ordinal)];

    /// <summary>
    /// A template with tags on every side that can hold them: the source's own, the
    /// category side's own and, split, the whole transaction's and a second line's,
    /// with a third line that has none.
    /// </summary>
    private async Task<(Guid Reminder, Guid Template)> TaggedTemplateAsync(World w, bool split)
    {
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord", Tags = ["rent", "housing"] }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);
        await w.Ledger.SetSideTagsAsync(template, w.Rent, ["flat"], w.Bank);
        if (!split) return (reminder, template);

        var groceries = await w.Ledger.AddCategoryAsync("Groceries");
        var edit = await EditLinesAsync(w, reminder, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "bills", Tags = ["home"],
                Lines = [new TransactionLineNotesRequest { Index = 1, Memo = "power", Tags = ["energy", "utility"] }],
            },
            Line(w.Rent, -1500m, SourceLegOf(await DetailAsync(w, reminder), 0)),
            Line(w.Utilities, -80m),
            Line(groceries.Id, -50m));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        return (reminder, template);
    }

    /// <summary>The detail's transaction tags are the header's stored set, and each leg's are that leg's.</summary>
    private static void AssertCarriesStoredTags(Annotations<string> stored, ReminderDetail detail)
    {
        Assert.Equal(stored.Header.Tags, Sorted(detail.Tags));
        Assert.All(detail.Legs, l =>
            Assert.Equal(stored.For(new LegKey(l.PostingIndex, l.AccountId)).Tags, Sorted(l.LegTags)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_detail_carries_the_stored_tags_of_the_transaction_and_of_every_leg(bool split)
    {
        await using var w = await WorldAsync();
        var (reminder, template) = await TaggedTemplateAsync(w, split);

        var detail = await DetailAsync(w, reminder);

        AssertCarriesStoredTags(await w.Ledger.LoadNotesAsync(template), detail);
        // Each set is there, not two empty ones agreeing.
        List<string> transaction = split ? ["home"] : [];
        Assert.Equal(transaction, Sorted(detail.Tags));
        Assert.Equal(["housing", "rent"], Sorted(detail.Legs.Single(l => l.PostingIndex == 0 && l.AccountId == w.Bank).LegTags));
        Assert.Equal(["flat"], Sorted(detail.Legs.Single(l => l.AccountId == w.Rent).LegTags));
        if (split)
        {
            Assert.Equal(["energy", "utility"], Sorted(detail.Legs.Single(l => l.PostingIndex == 1 && l.AccountId == w.Bank).LegTags));
            Assert.Empty(detail.Legs.Single(l => l.PostingIndex == 2 && l.AccountId == w.Bank).LegTags);
        }
    }

    [Fact]
    public async Task The_detail_spells_the_tags_beside_the_memos()
    {
        await using var w = await WorldAsync();
        var (reminder, _) = await TaggedTemplateAsync(w, split: false);

        var json = await w.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}");

        Assert.Equal(0, json.GetProperty("tags").GetArrayLength());
        var source = json.GetProperty("legs").EnumerateArray().Single(l => l.GetProperty("accountId").GetGuid() == w.Bank);
        Assert.Equal(["housing", "rent"], Sorted(source.GetProperty("legTags").EnumerateArray().Select(t => t.GetString()!)));
    }

    /// <summary>
    /// What an editor seeded from <paramref name="detail"/> sends when the user changes
    /// nothing: each line by its source leg's id, and the source side's memo and tags as
    /// loaded (a one-split's on its leg, a split's on the transaction and each line).
    /// </summary>
    private static EditReminderRequest UnchangedEditOf(ReminderDetail detail)
    {
        var source = detail.SourceAccountId!.Value;
        var lines = detail.Legs.Where(l => l.AccountId == source).OrderBy(l => l.PostingIndex).ToList();
        var items = lines.Select(l => Line(
            detail.Legs.Single(o => o.PostingIndex == l.PostingIndex && o.LegId != l.LegId).AccountId, l.Amount, l.LegId));
        var notes = lines.Count == 1
            ? new TransactionNotesRequest { Side = source, Memo = lines[0].LegMemo, Tags = lines[0].LegTags }
            : new TransactionNotesRequest
            {
                Side = source, Memo = detail.Memo, Tags = detail.Tags,
                Lines = [.. lines.Select(l => new TransactionLineNotesRequest { Index = l.PostingIndex, Memo = l.LegMemo, Tags = l.LegTags })],
            };
        return new EditReminderRequest
        {
            Postings = new PatchReminderPostings { SourceAccountId = source, Items = [.. items] },
            Notes = notes,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saving_the_loaded_detail_unchanged_keeps_every_stored_tag(bool split)
    {
        await using var w = await WorldAsync();
        var (reminder, template) = await TaggedTemplateAsync(w, split);
        var before = await w.Ledger.LoadNotesAsync(template);

        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            UnchangedEditOf(await DetailAsync(w, reminder)));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        Assert.Equal(before, await w.Ledger.LoadNotesAsync(template));
        AssertCarriesStoredTags(before, (await edit.Content.ReadFromJsonAsync<ReminderDetail>())!);
        await AssertInvariantsAsync(w);
    }

    // ----------------------------------------------------------------------
    // A reminder's source account is set when it is created. An edit that names
    // another one is refused and writes nothing, on every edit route; an edit that
    // names the stored source is an ordinary edit.
    // ----------------------------------------------------------------------

    /// <summary>What a reminder edit can write, read back: the series' source and
    /// schedule, the template's payee and legs, its memo and tags, and its owner.</summary>
    private sealed record Written(
        ReminderDetail Detail, HashSet<(Guid, int, Guid, decimal, Guid?, decimal?)> Legs,
        Annotations<string> Notes, Guid? Owner);

    private async Task<Written> WrittenAsync(World w, Guid reminder)
    {
        var template = await TemplateOfAsync(reminder);
        var detail = await DetailAsync(w, reminder);
        return new Written(detail,
            detail.Legs.Select(l => (l.LegId, l.PostingIndex, l.AccountId, l.Amount, l.SecurityId, l.Quantity)).ToHashSet(),
            await w.Ledger.LoadNotesAsync(template), await OwnerAsync(template));
    }

    private async Task AssertNothingWrittenAsync(World w, Guid reminder, Written before)
    {
        var after = await WrittenAsync(w, reminder);
        Assert.Equal(before.Detail.SourceAccountId, after.Detail.SourceAccountId);
        Assert.Equal(before.Detail.Payee, after.Detail.Payee);
        Assert.Equal(before.Detail.Action, after.Detail.Action);
        Assert.Equal(before.Detail.Rrule, after.Detail.Rrule);
        Assert.Equal(before.Detail.StartDate, after.Detail.StartDate);
        Assert.Equal(before.Detail.NextDueDate, after.Detail.NextDueDate);
        Assert.Equal(before.Legs, after.Legs);
        Assert.Equal(before.Notes, after.Notes);
        Assert.Equal(before.Owner, after.Owner);
        await using var db = Db.NewDbContext();
        Assert.False(await db.Tags.AnyAsync(g => g.LedgerId == w.Ledger.LedgerId && g.Name == "changed"));
        await AssertInvariantsAsync(w);
    }

    /// <summary>
    /// A bank template, one line or split, whose first line is a transfer to Savings, with
    /// notes on the source, on the transfer's other side and, split, on the transaction
    /// and a second line.
    /// </summary>
    private async Task<(Guid Reminder, Guid Savings)> TransferTemplateAsync(World w, bool split)
    {
        var savings = await w.Ledger.AddBankAccountAsync("Savings");
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "X", Tags = ["x"] }, (savings.Id, -100m));
        await w.Ledger.SetSideMemoAsync(await TemplateOfAsync(reminder), savings.Id, "Y", w.Bank);
        if (!split) return (reminder, savings.Id);

        var edit = await EditLinesAsync(w, reminder, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "bills", Tags = ["home"],
                Lines = [new TransactionLineNotesRequest { Index = 1, Memo = "B", Tags = ["b"] }],
            },
            Line(savings.Id, -100m, SourceLegOf(await DetailAsync(w, reminder), 0)),
            Line(w.Rent, -1500m),
            Line(w.Utilities, -80m));
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        return (reminder, savings.Id);
    }

    /// <summary>
    /// An edit naming a source other than the stored one is refused with
    /// reminder-source-immutable, whether the new source is an account the template does
    /// not use or the other side of one of its lines (the line entered from that side),
    /// and whatever else the same edit changes: nothing is written.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_bank_edit_naming_another_source_is_refused_and_writes_nothing(bool split, bool ontoALine)
    {
        await using var w = await WorldAsync();
        var (reminder, savings) = await TransferTemplateAsync(w, split);
        var newSource = ontoALine ? savings : (await w.Ledger.AddBankAccountAsync("Other")).Id;
        var before = await WrittenAsync(w, reminder);
        Assert.Equal(w.Bank, before.Owner);

        // The transfer line as seen from the new source when it is that line's other side.
        List<TransactionPosting> items = [Line(ontoALine ? w.Bank : savings, ontoALine ? 100m : -100m, SourceLegOf(before.Detail, 0))];
        if (split)
        {
            items.Add(Line(w.Rent, -1500m, SourceLegOf(before.Detail, 1)));
            items.Add(Line(w.Utilities, -80m, SourceLegOf(before.Detail, 2)));
        }
        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest
            {
                Payee = "New landlord",
                StartDate = Start.AddMonths(1),
                Postings = new PatchReminderPostings { SourceAccountId = newSource, Items = items },
                Notes = new TransactionNotesRequest { Side = newSource, Memo = "changed", Tags = ["changed"] },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.ReminderSourceImmutable, await CodeAsync(edit));
        await AssertNothingWrittenAsync(w, reminder, before);
    }

    /// <summary>The same refusal on the investment route: a template's brokerage is set
    /// when it is created.</summary>
    [Fact]
    public async Task An_investment_edit_naming_another_brokerage_is_refused_and_writes_nothing()
    {
        await using var w = await WorldAsync();
        var other = await w.Ledger.AddInvestmentAccountAsync("Other brokerage");
        CreateInvestmentTransactionRequest DividendFrom(Guid brokerage, decimal amount, string memo) => new()
        {
            BrokerageAccountId = brokerage, PostedAt = Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Action = "dividend_cash", SecurityId = w.Security, Amount = amount, CategoryAccountId = w.Dividends,
            Payee = memo == "quarterly" ? "Fund" : "New fund", Memo = memo, Tags = [memo],
        };
        var create = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1", StartDate = Start,
                Transaction = DividendFrom(w.Brokerage, 12m, "quarterly"),
            });
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var reminder = (await create.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var before = await WrittenAsync(w, reminder);
        Assert.Equal(w.Brokerage, before.Owner);

        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}/investment",
            new EditInvestmentReminderRequest
            {
                StartDate = Start.AddMonths(1),
                Transaction = DividendFrom(other.Id, 15m, "changed"),
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.ReminderSourceImmutable, await CodeAsync(edit));
        await AssertNothingWrittenAsync(w, reminder, before);
    }

    /// <summary>An edit that names the stored source, as the editor always does, is an
    /// ordinary edit: it writes what it changes, and the template stays with its source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bank_edit_naming_the_stored_source_is_written(bool split)
    {
        await using var w = await WorldAsync();
        var (reminder, savings) = await TransferTemplateAsync(w, split);
        var before = await DetailAsync(w, reminder);

        List<TransactionPosting> items = [Line(savings, -120m, SourceLegOf(before, 0))];
        if (split)
        {
            items.Add(Line(w.Rent, -1500m, SourceLegOf(before, 1)));
            items.Add(Line(w.Utilities, -80m, SourceLegOf(before, 2)));
        }
        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest
            {
                Payee = "New landlord",
                StartDate = Start.AddMonths(1),
                Postings = new PatchReminderPostings { SourceAccountId = w.Bank, Items = items },
                Notes = new TransactionNotesRequest { Side = w.Bank, Memo = "edited" },
            });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        var after = await DetailAsync(w, reminder);
        var template = await TemplateOfAsync(reminder);
        Assert.Equal(w.Bank, after.SourceAccountId);
        Assert.Equal("New landlord", after.Payee);
        Assert.Equal(Start.AddMonths(1), after.StartDate);
        Assert.Equal(-120m, after.Legs.Single(l => l.PostingIndex == 0 && l.AccountId == w.Bank).Amount);
        var notes = await w.Ledger.LoadNotesAsync(template);
        Assert.Equal("edited", split ? notes.Header.Memo : On(notes, 0, w.Bank).Memo);
        Assert.Equal(new Notes<string>("Y"), On(notes, 0, savings));
        Assert.Equal(w.Bank, await OwnerAsync(template));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Creating_a_template_with_notes_from_another_side_than_its_source_is_refused()
    {
        await using var w = await WorldAsync();

        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
            StartDate = Start,
            Payee = "Landlord",
            SourceAccountId = w.Bank,
            Postings = [new TransactionPosting { CounterpartyAccountId = w.Rent, Amount = -1500m }],
            Notes = new TransactionNotesRequest { Side = w.Rent, Memo = "to landlord" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.SideNotOnTransaction, await CodeAsync(resp));
        await using var db = Db.NewDbContext();
        Assert.False(await db.RecurringTransactions.AnyAsync(r => r.LedgerId == w.Ledger.LedgerId));
        Assert.False(await db.TxnHeaders.AnyAsync(h => h.LedgerId == w.Ledger.LedgerId));
    }

    [Fact]
    public async Task A_notes_edit_from_another_side_than_the_series_source_is_refused()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord" }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);
        var before = await w.Ledger.LoadNotesAsync(template);

        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest { Notes = new TransactionNotesRequest { Side = w.Rent, Memo = "flat 2" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, edit.StatusCode);
        Assert.Equal(Coffer.Api.Errors.BusinessError.Codes.SideNotOnTransaction, await CodeAsync(edit));
        Assert.Equal(before, await w.Ledger.LoadNotesAsync(template));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString();

    [Fact]
    public async Task A_blank_memo_clears_a_templates_memo()
    {
        await using var w = await WorldAsync();
        var reminder = await CreateBankAsync(w,
            new TransactionNotesRequest { Side = w.Bank, Memo = "to landlord" }, (w.Rent, -1500m));
        var template = await TemplateOfAsync(reminder);

        var edit = await w.Client.PatchAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/{reminder}",
            new EditReminderRequest { Notes = new TransactionNotesRequest { Side = w.Bank, Memo = "  " } });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());

        Assert.True(On(await w.Ledger.LoadNotesAsync(template), 0, w.Bank).IsEmpty);
    }

    [Fact]
    public async Task An_investment_templates_notes_sit_on_its_brokerage()
    {
        await using var w = await WorldAsync();
        var create = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/reminders/investment",
            new CreateInvestmentReminderRequest
            {
                Rrule = "FREQ=MONTHLY;BYMONTHDAY=1",
                StartDate = Start,
                Transaction = new CreateInvestmentTransactionRequest
                {
                    BrokerageAccountId = w.Brokerage, PostedAt = Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Action = "dividend_cash", SecurityId = w.Security, Amount = 12m, CategoryAccountId = w.Dividends,
                    Memo = "quarterly", Tags = ["income"],
                },
            });
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var reminder = (await create.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        var template = await TemplateOfAsync(reminder);

        var templateNotes = await w.Ledger.LoadNotesAsync(template);
        Assert.True(templateNotes.Header.IsEmpty);
        Assert.Equal(new Notes<string>("quarterly", ["income"]),
            Assert.Single(templateNotes.Legs, l => l.Key.AccountId == w.Brokerage).Value);
        Assert.Equal(w.Brokerage, await OwnerAsync(template));
        // An investment occurrence is posted only by hand, through the investment create,
        // which writes the notes the dialog sends; the clone refuses an investment template.
        await AssertInvariantsAsync(w);
    }

    // The managed loan reminder's owner is covered with the other writers' owners in
    // OriginatingAccountWriterTests; it carries no notes.
}
