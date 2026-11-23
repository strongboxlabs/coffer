using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// The bank create and PATCH write memo and tags through the write primitive: per
/// side on a one-split, the whole transaction's and each line's on a split, only from
/// the originating account on a split, and ownership handed to the side that splits.
/// </summary>
/// <remarks>
/// <para>The register still reads through the old view until the readers move, so
/// these assert the STORED state, and what each account's register shows under the
/// one presentation rule (<see cref="SyntheticLedger.ShownForAsync"/>), which is what
/// the readers compose.</para>
/// <para>Every test ends with the originating-account invariants.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BankNotesWriterTests : DatabaseTest
{
    public BankNotesWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static readonly DateTime Posted = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        SyntheticLedger Ledger, ApiFactory Factory, HttpClient Client,
        Guid Bank, Guid Savings, Guid Groceries, Guid Fuel) : IAsyncDisposable
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
        var savings = await ledger.AddBankAccountAsync("Savings");
        var groceries = await ledger.AddCategoryAsync("Groceries");
        var fuel = await ledger.AddCategoryAsync("Fuel");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, factory, client, bank.Id, savings.Id, groceries.Id, fuel.Id);
    }

    private static async Task<Guid> CreateAsync(World w, Guid source, TransactionNotesRequest? notes, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                Payee = "Shop",
                SourceAccountId = source,
                Postings = [.. postings.Select(p => new TransactionPosting { CounterpartyAccountId = p.Counterparty, Amount = p.Amount })],
                Notes = notes,
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static Task<HttpResponseMessage> PatchAsync(World w, Guid headerId, PatchTransactionRequest body) =>
        w.Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/ledgers/{w.Ledger.LedgerId}/transactions/{headerId}")
        {
            Content = JsonContent.Create(body),
        });

    private static async Task<string?> CodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private async Task<Guid?> OwnerAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaders.AsNoTracking().Where(h => h.Id == headerId).Select(h => h.OriginatingAccountId).SingleAsync();
    }

    private async Task<List<(Guid Id, int Posting, Guid Account)>> LegsAsync(Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == headerId)
                .OrderBy(l => l.PostingIndex).Select(l => new { l.Id, l.PostingIndex, l.AccountId }).ToListAsync())
            .Select(l => (l.Id, l.PostingIndex, l.AccountId)).ToList();
    }

    private static Notes<string> LegNotes(Annotations<string> notes, int posting, Guid account) =>
        notes.For(new LegKey(posting, account));

    private Task AssertInvariantsAsync(World w) => OriginatingAccountCheck.AssertHoldsAsync(Db, w.Ledger.LedgerId);

    // ----- one-split ---------------------------------------------------------

    [Fact]
    public async Task Each_side_of_a_one_split_writes_its_own_and_the_other_inherits()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "weekly", Tags = ["food"] },
            (w.Groceries, -40m));

        // The category writes its own memo; the bank's own stays, and its tags
        // still show on the category side by inheritance.
        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Groceries, Memo = "household" },
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal(new Notes<string>("weekly", ["food"]), LegNotes(notes, 0, w.Bank));
        Assert.Equal(new Notes<string>("household"), LegNotes(notes, 0, w.Groceries));
        Assert.Equal(w.Bank, await OwnerAsync(id));

        var fromBank = Assert.Single(await w.Ledger.ShownForAsync(id, w.Bank));
        Assert.Equal(("weekly", false), (fromBank.Memo, fromBank.Presentation.MemoInherited));
        var fromCategory = Assert.Single(await w.Ledger.ShownForAsync(id, w.Groceries));
        Assert.Equal(("household", false), (fromCategory.Memo, fromCategory.Presentation.MemoInherited));
        Assert.Equal(["food"], fromCategory.Tags);
        Assert.True(fromCategory.Presentation.TagsInherited);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task An_untouched_save_from_the_inheriting_side_writes_nothing()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "weekly", Tags = ["food"] },
            (w.Groceries, -40m));
        var before = await w.Ledger.LoadNotesAsync(id);

        // The category register saves without touching the notes: its notes body
        // names the side and no field, and the payee edit rides along.
        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Payee = "Shop renamed",
            Notes = new TransactionNotesRequest { Side = w.Groceries },
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        Assert.Equal(before, await w.Ledger.LoadNotesAsync(id));
        var fromCategory = Assert.Single(await w.Ledger.ShownForAsync(id, w.Groceries));
        Assert.Equal(("weekly", true), (fromCategory.Memo, fromCategory.Presentation.MemoInherited));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Blank_memos_are_stored_as_none_and_memos_are_trimmed()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "  padded\t" },
            (w.Groceries, -40m));
        Assert.Equal("padded", LegNotes(await w.Ledger.LoadNotesAsync(id), 0, w.Bank).Memo);

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Memo = " \t " },
        });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        await using var db = Db.NewDbContext();
        Assert.All(await db.TxnLegs.AsNoTracking().Where(l => l.HeaderId == id).Select(l => l.LegMemo).ToListAsync(),
            Assert.Null);
        Assert.Null(await db.TxnHeaders.AsNoTracking().Where(h => h.Id == id).Select(h => h.Memo).SingleAsync());
    }

    [Fact]
    public async Task The_notes_must_be_written_from_the_postings_source()
    {
        await using var w = await WorldAsync();
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                SourceAccountId = w.Bank,
                Postings = [new TransactionPosting { CounterpartyAccountId = w.Groceries, Amount = -5m }],
                Notes = new TransactionNotesRequest { Side = w.Groceries, Memo = "x" },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessErrorCodes.SideNotOnTransaction, await CodeAsync(resp));

        await using var db = Db.NewDbContext();
        Assert.False(await db.TxnHeaders.AnyAsync(h => h.LedgerId == w.Ledger.LedgerId));
    }

    public static TheoryData<int[]> InvalidLines => new()
    {
        new[] { 2 },        // past the last line
        new[] { -1 },       // before the first
        new[] { 1, 1 },     // the same line twice
    };

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public async Task Line_notes_that_name_no_line_or_one_line_twice_are_refused_on_create(int[] indexes)
    {
        await using var w = await WorldAsync();
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                SourceAccountId = w.Bank,
                Postings =
                [
                    new TransactionPosting { CounterpartyAccountId = w.Groceries, Amount = -5m },
                    new TransactionPosting { CounterpartyAccountId = w.Fuel, Amount = -7m },
                ],
                Notes = new TransactionNotesRequest
                {
                    Side = w.Bank,
                    Lines = [.. indexes.Select(i => new TransactionLineNotesRequest { Index = i, Memo = "line" })],
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessErrorCodes.LineNotesInvalid, await CodeAsync(resp));
        await using var db = Db.NewDbContext();
        Assert.False(await db.TxnHeaders.AnyAsync(h => h.LedgerId == w.Ledger.LedgerId));
    }

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public async Task Line_notes_that_name_no_line_or_one_line_twice_are_refused_on_patch(int[] indexes)
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "trip",
                Lines = [new TransactionLineNotesRequest { Index = 0, Memo = "snacks" }],
            },
            (w.Groceries, -5m), (w.Fuel, -7m));
        var before = await w.Ledger.LoadNotesAsync(id);

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest
            {
                Side = w.Bank,
                Lines = [.. indexes.Select(i => new TransactionLineNotesRequest { Index = i, Memo = "line" })],
            },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessErrorCodes.LineNotesInvalid, await CodeAsync(resp));
        Assert.Equal(before, await w.Ledger.LoadNotesAsync(id));
    }

    [Fact]
    public async Task Line_notes_on_a_one_posting_result_are_refused()
    {
        await using var w = await WorldAsync();
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = Posted,
                SourceAccountId = w.Bank,
                Postings = [new TransactionPosting { CounterpartyAccountId = w.Groceries, Amount = -5m }],
                Notes = new TransactionNotesRequest { Side = w.Bank, Lines = [new TransactionLineNotesRequest { Index = 0, Memo = "x" }] },
            });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessErrorCodes.LineNotesOnOnePosting, await CodeAsync(resp));
    }

    // ----- splits ------------------------------------------------------------

    [Fact]
    public async Task Splitting_from_the_other_side_hands_it_over_and_neither_register_changes()
    {
        // A one-split entered from Checking to Savings (two real accounts), split from
        // Savings into two lines. Savings becomes the owner; what Savings showed
        // (inherited) becomes the transaction's and line 1's; Checking's own stays on
        // its leg of line 1; the new line starts blank.
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "move", Tags = ["house"] },
            (w.Savings, -100m));
        var savingsLeg = (await LegsAsync(id)).Single(l => l.Account == w.Savings).Id;

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Savings,
                Items =
                [
                    new TransactionPosting { LegId = savingsLeg, CounterpartyAccountId = w.Bank, Amount = 60m },
                    new TransactionPosting { CounterpartyAccountId = w.Groceries, Amount = 40m },
                ],
            },
            Notes = new TransactionNotesRequest { Side = w.Savings },
        });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        Assert.Equal(w.Savings, await OwnerAsync(id));
        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("move", ["house"]), notes.Header);
        Assert.Equal(new Notes<string>("move", ["house"]), LegNotes(notes, 0, w.Savings));
        Assert.Equal(new Notes<string>("move", ["house"]), LegNotes(notes, 0, w.Bank));
        Assert.True(LegNotes(notes, 1, w.Savings).IsEmpty);
        Assert.True(LegNotes(notes, 1, w.Groceries).IsEmpty);

        // Checking now sees a read-only line of a split, still showing its own.
        var fromBank = Assert.Single(await w.Ledger.ShownForAsync(id, w.Bank));
        Assert.Equal(LegKind.SplitOpposite, fromBank.Presentation.Kind);
        Assert.True(fromBank.Presentation.IsReadOnly);
        Assert.Equal("move", fromBank.Memo);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_split_takes_the_transactions_notes_and_each_lines_from_its_owner()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "trip", Tags = ["travel"],
                Lines =
                [
                    new TransactionLineNotesRequest { Index = 0, Memo = "snacks", Tags = ["food"] },
                    new TransactionLineNotesRequest { Index = 1, Tags = ["car"] },
                ],
            },
            (w.Groceries, -20m), (w.Fuel, -30m));

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(new Notes<string>("trip", ["travel"]), notes.Header);
        Assert.Equal(new Notes<string>("snacks", ["food"]), LegNotes(notes, 0, w.Bank));
        Assert.Equal(new Notes<string>(null, ["car"]), LegNotes(notes, 1, w.Bank));
        Assert.True(LegNotes(notes, 0, w.Groceries).IsEmpty);
        Assert.True(LegNotes(notes, 1, w.Fuel).IsEmpty);

        // The category registers see each line read-only, inheriting the line's own.
        var fromFuel = Assert.Single(await w.Ledger.ShownForAsync(id, w.Fuel));
        Assert.True(fromFuel.Presentation.IsReadOnly);
        Assert.Null(fromFuel.Memo);
        Assert.Equal(["car"], fromFuel.Tags);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task A_split_is_read_only_from_its_opposite_side_and_nothing_is_written()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "trip" },
            (w.Groceries, -20m), (w.Fuel, -30m));
        var before = await w.Ledger.LoadNotesAsync(id);

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Payee = "not this either",
            Notes = new TransactionNotesRequest { Side = w.Groceries, Memo = "mine" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(BusinessErrorCodes.ReadOnlyRow, await CodeAsync(resp));
        Assert.Equal(before, await w.Ledger.LoadNotesAsync(id));
        await using var db = Db.NewDbContext();
        Assert.Equal("Shop", await db.TxnHeaders.AsNoTracking().Where(h => h.Id == id).Select(h => h.Payee).SingleAsync());
    }

    [Fact]
    public async Task The_symmetric_split_is_edited_from_its_owner_only()
    {
        // Every line to one category: the category is on every posting too, so
        // only the stored owner tells the two sides apart.
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "bulk" },
            (w.Groceries, -20m), (w.Groceries, -30m));
        Assert.Equal(w.Bank, await OwnerAsync(id));

        var fromCategory = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Groceries, Memo = "mine" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fromCategory.StatusCode);
        Assert.Equal(BusinessErrorCodes.ReadOnlyRow, await CodeAsync(fromCategory));

        // Reshaping it from the category is refused the same way.
        var legs = await LegsAsync(id);
        var reshape = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Groceries,
                Items = [.. legs.Where(l => l.Account == w.Groceries).Select(l =>
                    new TransactionPosting { LegId = l.Id, CounterpartyAccountId = w.Bank, Amount = 25m })],
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reshape.StatusCode);
        Assert.Equal(BusinessErrorCodes.ReadOnlyRow, await CodeAsync(reshape));

        var shown = await w.Ledger.ShownForAsync(id, w.Groceries);
        Assert.Equal(2, shown.Count);
        Assert.All(shown, s => Assert.True(s.Presentation.IsReadOnly));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Removing_a_line_keeps_the_others_values_by_leg()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "trip",
                Lines =
                [
                    new TransactionLineNotesRequest { Index = 0, Memo = "first" },
                    new TransactionLineNotesRequest { Index = 1, Memo = "second" },
                    new TransactionLineNotesRequest { Index = 2, Memo = "third" },
                ],
            },
            (w.Groceries, -1m), (w.Fuel, -2m), (w.Groceries, -3m));
        var bankLegs = (await LegsAsync(id)).Where(l => l.Account == w.Bank).ToList();

        // Drop the middle line and reverse the other two.
        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Bank,
                Items =
                [
                    new TransactionPosting { LegId = bankLegs[2].Id, CounterpartyAccountId = w.Groceries, Amount = -3m },
                    new TransactionPosting { LegId = bankLegs[0].Id, CounterpartyAccountId = w.Groceries, Amount = -1m },
                ],
            },
        });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal("trip", notes.Header.Memo);
        Assert.Equal("third", LegNotes(notes, 0, w.Bank).Memo);
        Assert.Equal("first", LegNotes(notes, 1, w.Bank).Memo);
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Collapsing_to_one_line_joins_the_notes_onto_the_owner_and_clears_the_header()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Memo = "trip", Tags = ["travel"],
                Lines = [new TransactionLineNotesRequest { Index = 0, Memo = "snacks", Tags = ["food"] }],
            },
            (w.Groceries, -20m), (w.Fuel, -30m));
        var bankLeg = (await LegsAsync(id)).First(l => l.Account == w.Bank && l.Posting == 0).Id;

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Bank,
                Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = w.Groceries, Amount = -50m }],
            },
        });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        var notes = await w.Ledger.LoadNotesAsync(id);
        Assert.True(notes.Header.IsEmpty);
        Assert.Equal("trip · snacks", LegNotes(notes, 0, w.Bank).Memo);
        Assert.Equal(["food", "travel"], LegNotes(notes, 0, w.Bank).Tags);
        Assert.Equal(w.Bank, await OwnerAsync(id));
        var fromCategory = Assert.Single(await w.Ledger.ShownForAsync(id, w.Groceries));
        Assert.Equal(("trip · snacks", true), (fromCategory.Memo, fromCategory.Presentation.MemoInherited));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Recategorizing_a_line_onto_a_real_account_makes_its_override_that_accounts_own()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "mine" },
            (w.Groceries, -20m));
        await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Groceries, Memo = "the category's" },
        });
        var legs = await LegsAsync(id);
        var bankLeg = legs.Single(l => l.Account == w.Bank).Id;
        var categoryLeg = legs.Single(l => l.Account == w.Groceries).Id;

        var resp = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Bank,
                Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = w.Savings, Amount = -20m }],
            },
        });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());

        // The same leg, now on Savings, with its value: Savings' own.
        await using var db = Db.NewDbContext();
        var moved = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == categoryLeg);
        Assert.Equal(w.Savings, moved.AccountId);
        Assert.Equal("the category's", moved.LegMemo);
        var fromSavings = Assert.Single(await w.Ledger.ShownForAsync(id, w.Savings));
        Assert.Equal(("the category's", false), (fromSavings.Memo, fromSavings.Presentation.MemoInherited));
        await AssertInvariantsAsync(w);
    }

    [Fact]
    public async Task Recategorizing_a_splits_lines_is_a_repoint_its_notes_stay_and_the_owner_follows()
    {
        // A split owned by its category, every line to Checking: recategorizing those
        // lines moves every leg the owner has, so ownership follows them, and each leg
        // keeps its notes. The recategorize is not a notes write, so Checking, which
        // sees the lines read-only, can still be the account it is made from.
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, new TransactionNotesRequest { Side = w.Bank, Memo = "groceries run" },
            (w.Groceries, -50m));
        var categoryLeg = (await LegsAsync(id)).Single(l => l.Account == w.Groceries).Id;
        var split = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Groceries,
                Items =
                [
                    new TransactionPosting { LegId = categoryLeg, CounterpartyAccountId = w.Bank, Amount = 30m },
                    new TransactionPosting { CounterpartyAccountId = w.Bank, Amount = 20m },
                ],
            },
            Notes = new TransactionNotesRequest
            {
                Side = w.Groceries,
                Lines = [new TransactionLineNotesRequest { Index = 1, Memo = "second line" }],
            },
        });
        Assert.True(split.IsSuccessStatusCode, await split.Content.ReadAsStringAsync());
        Assert.Equal(w.Groceries, await OwnerAsync(id));
        var before = await w.Ledger.LoadNotesAsync(id);

        await using (var db = Db.NewRequestDbContextAsUser(w.Ledger.UserId))
        {
            var outcome = await new Coffer.Api.Db.Repositories.TransactionsRepository(db, TimeProvider.System)
                .RecategorizeSplitPostingsAsync(w.Ledger.LedgerId, id, w.Groceries, w.Fuel, dryRun: false);
            Assert.Equal(Coffer.Api.Db.Repositories.TransactionsRepository.SplitPostingRecategorizeResult.Ok, outcome.Result);
        }

        Assert.Equal(w.Fuel, await OwnerAsync(id));
        var after = await w.Ledger.LoadNotesAsync(id);
        Assert.Equal(before.Header, after.Header);
        Assert.Equal(LegNotes(before, 1, w.Groceries), LegNotes(after, 1, w.Fuel));
        Assert.Equal(LegNotes(before, 0, w.Bank), LegNotes(after, 0, w.Bank));
        await AssertInvariantsAsync(w);
    }

    // ----- the tag limit -----------------------------------------------------

    private static string[] Tags(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => $"{prefix}{i:00}")];

    [Fact]
    public async Task Twenty_tags_save_and_twenty_one_are_refused()
    {
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank, null, (w.Groceries, -5m));

        var twenty = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = Tags("t", 20) },
        });
        Assert.Equal(HttpStatusCode.NoContent, twenty.StatusCode);

        var twentyOne = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = Tags("t", 21) },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twentyOne.StatusCode);
        Assert.Equal(BusinessErrorCodes.TransactionTagsTooMany, await CodeAsync(twentyOne));
        Assert.Equal(20, LegNotes(await w.Ledger.LoadNotesAsync(id), 0, w.Bank).Tags.Count);
    }

    [Fact]
    public async Task A_set_a_collapse_grew_past_the_limit_saves_unchanged_or_smaller_and_refuses_growth()
    {
        // A collapse unions the transaction's tags with the line's and drops none,
        // so the one-split it leaves can hold more than the limit.
        await using var w = await WorldAsync();
        var id = await CreateAsync(w, w.Bank,
            new TransactionNotesRequest
            {
                Side = w.Bank, Tags = Tags("h", 12),
                Lines = [new TransactionLineNotesRequest { Index = 0, Tags = Tags("l", 12) }],
            },
            (w.Groceries, -20m), (w.Fuel, -30m));
        var bankLeg = (await LegsAsync(id)).First(l => l.Account == w.Bank && l.Posting == 0).Id;
        var collapse = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = w.Bank,
                Items = [new TransactionPosting { LegId = bankLeg, CounterpartyAccountId = w.Groceries, Amount = -50m }],
            },
        });
        Assert.True(collapse.IsSuccessStatusCode, await collapse.Content.ReadAsStringAsync());
        var all = LegNotes(await w.Ledger.LoadNotesAsync(id), 0, w.Bank).Tags;
        Assert.Equal(24, all.Count);

        var unchanged = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = [.. all] },
        });
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);

        var swapped = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = [.. all.Skip(1), "swap"] },
        });
        Assert.Equal(HttpStatusCode.NoContent, swapped.StatusCode);

        var grown = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = [.. all.Skip(1), "swap", "one more"] },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, grown.StatusCode);
        Assert.Equal(BusinessErrorCodes.TransactionTagsTooMany, await CodeAsync(grown));

        var shrunk = await PatchAsync(w, id, new PatchTransactionRequest
        {
            Notes = new TransactionNotesRequest { Side = w.Bank, Tags = [.. all.Take(22)] },
        });
        Assert.Equal(HttpStatusCode.NoContent, shrunk.StatusCode);
        Assert.Equal(22, LegNotes(await w.Ledger.LoadNotesAsync(id), 0, w.Bank).Tags.Count);
    }

    /// <summary>The 422 codes these tests expect, as the API spells them.</summary>
    private static class BusinessErrorCodes
    {
        public const string ReadOnlyRow = Coffer.Api.Errors.BusinessError.Codes.ReadOnlyRow;
        public const string SideNotOnTransaction = Coffer.Api.Errors.BusinessError.Codes.SideNotOnTransaction;
        public const string LineNotesOnOnePosting = Coffer.Api.Errors.BusinessError.Codes.LineNotesOnOnePosting;
        public const string LineNotesInvalid = Coffer.Api.Errors.BusinessError.Codes.LineNotesInvalid;
        public const string TransactionTagsTooMany = Coffer.Api.Errors.BusinessError.Codes.TransactionTagsTooMany;
    }
}
