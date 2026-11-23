using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every writer of an input of the memo-and-tags rule leaves the stored presentation
/// equal to what the rule computes from the rows it wrote: through the API's own
/// endpoints, so the save interceptor and the set-based writers' explicit recomputes
/// are what is exercised.
/// </summary>
/// <remarks>
/// Each test ends with <see cref="LegNotes.DifferencesAsync"/> over the whole ledger,
/// the stored-equals-computed check; a writer that skipped the recompute leaves a leg
/// whose stored row is missing or stale, and the check names it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StoredNotesWriterTests : DatabaseTest
{
    private readonly ITestOutputHelper _output;

    public StoredNotesWriterTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    private static DateTime Day(int month, int day) => new(2026, month, day, 12, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        SyntheticLedger Ledger, HttpClient Client, ApiFactory Factory,
        AccountRow BankA, AccountRow BankB, AccountRow CatA, AccountRow CatB, AccountRow Fee, AccountRow Income,
        AccountRow BrokerA, AccountRow BrokerB, Guid Security) : IAsyncDisposable
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
        var bankA = await ledger.AddBankAccountAsync("Account A");
        var bankB = await ledger.AddBankAccountAsync("Account B");
        var catA = await ledger.AddCategoryAsync("Category A");
        var catB = await ledger.AddCategoryAsync("Category B");
        var fee = await ledger.AddCategoryAsync("Category D");
        var income = await ledger.AddCategoryAsync("Category E", kind: "income");
        var brokerA = await ledger.AddInvestmentAccountAsync("Brokerage A");
        var brokerB = await ledger.AddInvestmentAccountAsync("Brokerage B");
        var security = await ledger.AddSecurityAsync("Fund A", "FNDA");
        var factory = new ApiFactory(Db).WithoutDevAuth();
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return new World(ledger, client, factory, bankA, bankB, catA, catB, fee, income, brokerA, brokerB, security);
    }

    /// <summary>The stored-equals-computed check over the ledger, naming each leg by its payee.</summary>
    private async Task AssertStoredAsync(World w)
    {
        await using var db = Db.NewDbContext();
        var differences = await LegNotes.DifferencesAsync(db, w.Ledger.LedgerId, CancellationToken.None);
        var stored = await StoredNotesEquivalence.PresentedLegsAsync(db, w.Ledger.LedgerId);
        _output.WriteLine($"{stored} stored legs, {differences.Count} differences");
        if (differences.Count > 0)
        {
            var payees = await db.TxnLegs.AsNoTracking()
                .Where(l => l.LedgerId == w.Ledger.LedgerId)
                .Join(db.TxnHeaders, l => l.HeaderId, h => h.Id, (l, h) => new { l.Id, h.Payee })
                .ToDictionaryAsync(x => x.Id.ToString(), x => x.Payee);
            Assert.Fail(string.Join("\n", differences.Take(20).Select(d =>
            {
                var id = d.Split(' ', 3)[1].TrimEnd(':');
                return $"[{payees.GetValueOrDefault(id) ?? "?"}] {d}";
            })));
        }
        Assert.True(stored > 0, "nothing stored, so nothing was compared");

        // Fresh is not the same as right: the stored rows read through every reader's
        // perspective must also be what the rule shows from that perspective.
        await StoredNotesEquivalence.AssertEquivalentAsync(db, w.Ledger.LedgerId);
        if (await db.Tags.AnyAsync(t => t.LedgerId == w.Ledger.LedgerId))
            await StoredNotesEquivalence.AssertFiltersEquivalentAsync(db, w.Ledger.LedgerId, StoredNotesEquivalence.FilterSample.All);
    }

    private static async Task<Guid> CreateBankAsync(
        World w, Guid source, DateTime postedAt, string payee, string? memo, params (Guid Counterparty, decimal Amount)[] postings)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/transactions",
            new CreateTransactionRequest
            {
                PostedAt = postedAt,
                Payee = payee,
                Notes = memo is null ? null : new TransactionNotesRequest { Side = source, Memo = memo },
                SourceAccountId = source,
                Postings = postings.Select(p => new TransactionPosting
                {
                    CounterpartyAccountId = p.Counterparty, Amount = p.Amount,
                }).ToArray(),
            });
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    private static async Task<Guid> CreateInvestmentAsync(World w, CreateInvestmentTransactionRequest request)
    {
        var resp = await w.Client.PostAsJsonAsync($"/api/ledgers/{w.Ledger.LedgerId}/investment-transactions", request);
        Assert.True(resp.StatusCode == HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("headerId").GetGuid();
    }

    [Fact]
    public async Task Every_create_stores_what_the_rule_computes()
    {
        await using var w = await WorldAsync();
        await CreateBankAsync(w, w.BankA.Id, Day(3, 1), "Payee One", "one memo", (w.CatA.Id, -10m));
        await CreateBankAsync(w, w.BankA.Id, Day(3, 2), "Payee Move", null, (w.BankB.Id, -11m));
        await CreateBankAsync(w, w.BankA.Id, Day(3, 3), "Payee Split", "split memo", (w.CatA.Id, -12m), (w.CatB.Id, -13m));
        await CreateBankAsync(w, w.BankA.Id, Day(3, 4), "Payee Symmetric", null, (w.CatA.Id, -14m), (w.CatA.Id, -15m));
        foreach (var request in InvestmentCreates(w))
            await CreateInvestmentAsync(w, request);
        await AssertStoredAsync(w);
    }

    private static async Task SendAsync(World w, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/ledgers/{w.Ledger.LedgerId}/{path}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var resp = await w.Client.SendAsync(request);
        Assert.True(resp.IsSuccessStatusCode, $"{method} {path}: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<Guid> LegOnAsync(Guid headerId, Guid accountId, int posting = 0)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId && l.AccountId == accountId && l.PostingIndex == posting)
            .Select(l => l.Id).SingleAsync();
    }

    [Fact]
    public async Task Bank_edits_store_what_the_rule_computes()
    {
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(3, 1), "Payee One", "one", (w.CatA.Id, -10m));
        var other = await CreateBankAsync(w, w.BankA.Id, Day(3, 2), "Payee Two", null, (w.BankB.Id, -9m));

        // The header's memo and tags.
        await SendAsync(w, HttpMethod.Patch, $"transactions/{h}",
            new { notes = new { side = w.BankA.Id, memo = "edited", tags = new[] { "t-a", "t-b" } } });
        await AssertStoredAsync(w);

        // One to two lines, the first keeping its leg, the first line with notes of its own.
        var line = await LegOnAsync(h, w.BankA.Id);
        await SendAsync(w, HttpMethod.Patch, $"transactions/{h}", new
        {
            postings = new
            {
                sourceAccountId = w.BankA.Id,
                items = new object[]
                {
                    new { legId = line, counterpartyAccountId = w.CatA.Id, amount = -10m },
                    new { counterpartyAccountId = w.CatB.Id, amount = -5m },
                },
            },
            notes = new
            {
                side = w.BankA.Id,
                lines = new[] { new { index = 0, memo = "line one", tags = new[] { "t-line" } } },
            },
        });
        await AssertStoredAsync(w);

        // A line recategorized to a real account, then the split collapsed to one line.
        var second = await LegOnAsync(h, w.BankA.Id, posting: 1);
        await SendAsync(w, HttpMethod.Patch, $"transactions/{h}", new
        {
            postings = new
            {
                sourceAccountId = w.BankA.Id,
                items = new object[]
                {
                    new { legId = line, counterpartyAccountId = w.CatA.Id, amount = -10m },
                    new { legId = second, counterpartyAccountId = w.BankB.Id, amount = -5m },
                },
            },
        });
        await AssertStoredAsync(w);
        await SendAsync(w, HttpMethod.Patch, $"transactions/{h}", new
        {
            postings = new
            {
                sourceAccountId = w.BankA.Id,
                items = new object[] { new { legId = line, counterpartyAccountId = w.CatB.Id, amount = -10m } },
            },
        });
        await AssertStoredAsync(w);

        // Deleted: nothing of it remains stored.
        await SendAsync(w, HttpMethod.Delete, $"transactions/{other}");
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task Moves_merges_and_tag_changes_store_what_the_rule_computes()
    {
        await using var w = await WorldAsync();
        var a = await CreateBankAsync(w, w.BankA.Id, Day(3, 1), "Payee One", "one", (w.CatA.Id, -10m));
        var b = await CreateBankAsync(w, w.BankA.Id, Day(3, 2), "Payee Split", "split", (w.CatA.Id, -11m), (w.CatB.Id, -12m));
        var c = await CreateBankAsync(w, w.BankB.Id, Day(3, 3), "Payee Move", null, (w.CatA.Id, -13m));
        await SendAsync(w, HttpMethod.Patch, $"transactions/{a}",
            new { notes = new { side = w.BankA.Id, tags = new[] { "t-shared", "t-only" } } });
        await SendAsync(w, HttpMethod.Patch, $"transactions/{c}",
            new { notes = new { side = w.BankB.Id, tags = new[] { "t-other" } } });
        // Line tags, which the lines' opposite sides show inherited: a tag that changes
        // id or goes away changes what those rows show.
        var line0 = await LegOnAsync(b, w.BankA.Id);
        var line1 = await LegOnAsync(b, w.BankA.Id, posting: 1);
        await SendAsync(w, HttpMethod.Patch, $"transactions/{b}", new
        {
            postings = new
            {
                sourceAccountId = w.BankA.Id,
                items = new object[]
                {
                    new { legId = line0, counterpartyAccountId = w.CatA.Id, amount = -11m },
                    new { legId = line1, counterpartyAccountId = w.CatB.Id, amount = -12m },
                },
            },
            notes = new
            {
                side = w.BankA.Id,
                lines = new[]
                {
                    new { index = 0, tags = new[] { "t-shared" } },
                    new { index = 1, tags = new[] { "t-only" } },
                },
            },
        });
        await AssertStoredAsync(w);

        // One transaction to another bank: its legs and its owner move without the tracker.
        await SendAsync(w, HttpMethod.Post, "transactions/bulk-move-account", new
        {
            selection = new { kind = "explicit", accountId = w.BankB.Id, headerIds = new[] { c } },
            targetAccountId = w.BankA.Id,
        });
        await AssertStoredAsync(w);

        // Every row of a category to another one.
        await SendAsync(w, HttpMethod.Post, "transactions/bulk-move-account", new
        {
            selection = new
            {
                kind = "all", accountId = w.CatB.Id, statusFilter = "all",
                selectedAt = DateTime.UtcNow.AddMinutes(1), excludeIds = Array.Empty<Guid>(), headerIds = Array.Empty<Guid>(),
            },
            targetAccountId = w.Fee.Id,
        });
        await AssertStoredAsync(w);

        // A category merged into another.
        await SendAsync(w, HttpMethod.Post, $"categories/{w.CatA.Id}/merge", new MergeCategoryRequest(w.Fee.Id));
        await AssertStoredAsync(w);

        // A tag merged into another, then a tag deleted.
        await using (var db = Db.NewDbContext())
        {
            var tags = await db.Tags.AsNoTracking().Where(t => t.LedgerId == w.Ledger.LedgerId)
                .ToDictionaryAsync(t => t.Name, t => t.Id);
            await SendAsync(w, HttpMethod.Post, $"tags/{tags["t-only"]}/merge", new { intoTagId = tags["t-other"] });
            await AssertStoredAsync(w);
            await SendAsync(w, HttpMethod.Delete, $"tags/{tags["t-shared"]}");
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_save_through_the_service_context_stores_what_the_rule_computes()
    {
        // The worker's context (feed sync, auto-post) carries the same interceptors. Its
        // writers plan what they create and stamp the new legs, as here; the recompute
        // after the save then writes the tag rows.
        await using var w = await WorldAsync();
        var id = Guid.NewGuid();
        await using (var db = Db.NewServiceFactory().Create())
        {
            var header = TxnHeaderRow.NewIngested(id, w.Ledger.LedgerId, w.CatA.Id, origin: "file_import", externalId: $"imp-{id:N}");
            header.ProviderKey = "qif";
            header.Payee = "Payee Imported";
            header.PostedAt = Day(5, 1);
            header.TransactedAt = header.PostedAt;
            db.TxnHeaders.Add(header);
            var legs = new[]
            {
                Leg(id, w.Ledger.LedgerId, w.CatA.Id, 5m, 0), Leg(id, w.Ledger.LedgerId, w.BankA.Id, -5m, 0),
                Leg(id, w.Ledger.LedgerId, w.CatA.Id, 6m, 1), Leg(id, w.Ledger.LedgerId, w.BankB.Id, -6m, 1),
            };
            db.TxnLegs.AddRange(legs);
            var shape = AnnotationWriter.Shape(null, null,
                legs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id)),
                AnnotationWriter.AccountFacts.None);
            var resolved = await AnnotationWriter.ResolveAsync(db, w.Ledger.LedgerId,
                new Coffer.Domain.Annotations.AnnotationIntent<string>(
                    w.CatA.Id,
                    Coffer.Domain.Annotations.FieldEdit<string>.Set("worker memo"),
                    Coffer.Domain.Annotations.FieldEdit<IReadOnlyList<string>>.Set(["t-worker"])),
                CancellationToken.None);
            var plan = Coffer.Domain.Annotations.AnnotationPlanner.Seed(shape, resolved);
            Assert.True(plan.IsValid);
            await AnnotationWriter.ApplyAsync(db, header, legs, plan, CancellationToken.None);
            await db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId);
        }
        await using (var db = Db.NewDbContext())
            Assert.True(await db.TxnLegNoteTags.AnyAsync(t => t.HeaderId == id), "the recompute wrote no tag rows");
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_one_posting_buy_with_transaction_tags_reads_the_same_stored_and_live()
    {
        // The shape the investment tag tests create: a buy with no fee, its tags given on
        // create. One posting, so the write primitive puts them on the brokerage leg
        // (ADR-0108 D1), and the stored path must show the same as the rule run over the loaded rows.
        await using var w = await WorldAsync();
        var h = await CreateInvestmentAsync(w, new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security,
            Shares = 10m, Price = 100m, Amount = 1000m, PostedAt = Day(5, 4), Payee = "Payee Tagged Buy",
            Tags = ["t-a", "t-b"],
        });
        await AssertStoredAsync(w);

        await using var db = Db.NewDbContext();
        var brokerageLeg = await LegOnAsync(h, w.BrokerA.Id);
        var oracle = Assert.Single(await StoredNotesEquivalence.OracleRegisterAsync(db, w.Ledger.LedgerId), r => r.LegId == brokerageLeg);
        _output.WriteLine($"rule over the loaded rows, brokerage row: {oracle}");
        // The brokerage's row shows the event's tags, as the investment tag tests expect.
        Assert.Equal(["t-a", "t-b"], oracle.Shown!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Two_recomputes_of_one_header_run_one_after_the_other()
    {
        // Two writers edit different lines of one split at once. The second's recompute
        // must wait for the first's transaction and then read both edits; reading before
        // the wait would store the first line's old memo, or collide on the stored key.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(6, 1), "Payee Race", "race", (w.CatA.Id, -10m), (w.CatB.Id, -11m));
        var line0 = await LegOnAsync(h, w.BankA.Id);
        var line1 = await LegOnAsync(h, w.BankA.Id, posting: 1);

        await using var first = Db.NewDbContext();
        await using var firstTx = await first.Database.BeginTransactionAsync();
        await first.TxnLegs.Where(l => l.Id == line0).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "first line"));
        await LegNotes.RecomputeAsync(first, [h], CancellationToken.None);

        await using var second = Db.NewDbContext();
        await second.TxnLegs.Where(l => l.Id == line1).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "second line"));
        var racing = LegNotes.RecomputeAsync(second, [h], CancellationToken.None);
        Assert.NotSame(racing, await Task.WhenAny(racing, Task.Delay(TimeSpan.FromSeconds(2))));

        await firstTx.CommitAsync();
        await racing;
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_restore_copies_the_stored_presentation_back_verbatim()
    {
        // A snapshot captures the notes_* columns with the legs and the tag-role rows in
        // their own part, and a restore copies both back as they are, with no recompute:
        // it refuses a snapshot of another schema version, so they are the rule's values
        // for the inputs restored beside them. Proven by a payload whose stored values are
        // not what the rule computes: the restore must bring back exactly those.
        await using var w = await WorldAsync();
        var split = await CreateBankAsync(w, w.BankA.Id, Day(9, 1), "Payee Restore", "split memo", (w.CatA.Id, -10m), (w.CatB.Id, -11m));
        await CreateBankAsync(w, w.BankA.Id, Day(9, 2), "Payee Restore Two", "one memo", (w.CatA.Id, -12m));
        await SendAsync(w, HttpMethod.Patch, $"transactions/{split}",
            new { notes = new { side = w.BankA.Id, tags = new[] { "t-restore" } } });
        await AssertStoredAsync(w);

        var createResp = await w.Client.PostAsJsonAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/snapshots", new CreateSnapshotRequest("full"));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var snap = (await createResp.Content.ReadFromJsonAsync<CreateSnapshotResponse>())!.Snapshot!;

        // In the payload: every leg's shown memo, and every tag row's inherited flag,
        // replaced by values the rule never produced.
        const string marker = "as the snapshot holds it";
        await using (var probe = Db.OpenServiceConnection())
        {
            async Task<int> TamperAsync(string part, string row)
            {
                await using var cmd = new Npgsql.NpgsqlCommand($"""
                    UPDATE ledger_snapshot_parts p
                       SET content = (SELECT COALESCE(jsonb_agg({row}), '[]'::jsonb) FROM jsonb_array_elements(p.content) r)
                     WHERE p.snapshot_id = @s AND p.part_name = '{part}' AND jsonb_array_length(p.content) > 0
                    """, probe);
                cmd.Parameters.AddWithValue("s", snap.Id);
                return await cmd.ExecuteNonQueryAsync();
            }
            Assert.True(await TamperAsync("txn_legs", $"jsonb_set(r, '{{notes_memo}}', '\"{marker}\"')") > 0,
                "the snapshot has no txn_legs part, so nothing was tampered with");
            Assert.True(await TamperAsync("txn_leg_note_tags", "jsonb_set(r, '{inherited}', to_jsonb(NOT (r->>'inherited')::boolean))") > 0,
                "the snapshot has no tag-role rows, so the copy of the table is not tested");
        }
        int capturedTagRows;
        await using (var db = Db.NewDbContext())
            capturedTagRows = await db.TxnLegNoteTags.CountAsync(t => t.LedgerId == w.Ledger.LedgerId);

        // Edits after the snapshot, which the restore rolls back.
        await SendAsync(w, HttpMethod.Patch, $"transactions/{split}",
            new { notes = new { side = w.BankA.Id, memo = "after the snapshot", tags = Array.Empty<string>() } });

        var restoreResp = await w.Client.PostAsync(
            $"/api/ledgers/{w.Ledger.LedgerId}/snapshots/{snap.Id}/restore", content: null);
        Assert.Equal(HttpStatusCode.NoContent, restoreResp.StatusCode);

        try
        {
            await using var db = Db.NewDbContext();
            var memos = await db.Set<ResolvedTransactionView>().AsNoTracking()
                .Where(r => r.HeaderId == split).Select(r => r.NotesMemo).ToListAsync();
            Assert.NotEmpty(memos);
            Assert.All(memos, m => Assert.Equal(marker, m));
            Assert.Equal(capturedTagRows, await db.TxnLegNoteTags.CountAsync(t => t.LedgerId == w.Ledger.LedgerId));
            // Every restored value is the payload's, so every leg now differs from the rule.
            var differences = await LegNotes.DifferencesAsync(db, w.Ledger.LedgerId, CancellationToken.None);
            var legs = await db.TxnLegs.CountAsync(l => l.LedgerId == w.Ledger.LedgerId);
            Assert.Equal(legs, differences.Count);
        }
        finally
        {
            // The values were made wrong on purpose; put them right for the suite's check.
            await w.Ledger.RecomputeAllNotesAsync();
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_recompute_refuses_any_isolation_level_but_read_committed()
    {
        // Under REPEATABLE READ the inputs read after the lock are the snapshot's, taken
        // before the lock was granted, not what the previous holder committed.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(6, 2), "Payee Isolation", "memo", (w.CatA.Id, -10m));
        foreach (var level in new[] { System.Data.IsolationLevel.RepeatableRead, System.Data.IsolationLevel.Serializable })
        {
            await using var db = Db.NewDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(level);
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => LegNotes.RecomputeAsync(db, [h], CancellationToken.None));
            Assert.Equal("25000", refused.SqlState);
        }
        await using (var db = Db.NewDbContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            await LegNotes.RecomputeAsync(db, [h], CancellationToken.None);
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_recompute_rewrites_only_the_tag_rows_whose_key_header_or_roles_changed()
    {
        await using var w = await WorldAsync();
        var a = await CreateBankAsync(w, w.BankA.Id, Day(6, 3), "Payee Diff", "memo", (w.CatA.Id, -10m), (w.CatB.Id, -11m));
        await SendAsync(w, HttpMethod.Patch, $"transactions/{a}",
            new { notes = new { side = w.BankA.Id, tags = new[] { "t-one", "t-two" } } });
        var b = await CreateBankAsync(w, w.BankB.Id, Day(6, 4), "Payee Target", null, (w.CatA.Id, -12m));
        await AssertStoredAsync(w);

        await using var db = Db.NewDbContext();
        // Nothing changed: nothing written.
        Assert.Equal(LegNotes.Writes.None, await LegNotes.RecomputeAsync(db, [a, b], CancellationToken.None));

        // A line memo: that leg's columns, and the other side's that inherits it; no tag row.
        var line0 = await LegOnAsync(a, w.BankA.Id);
        await db.TxnLegs.Where(l => l.Id == line0).ExecuteUpdateAsync(s => s.SetProperty(l => l.LegMemo, "line"));
        var memo = await LegNotes.RecomputeAsync(db, [a], CancellationToken.None);
        Assert.True(memo.LegsUpdated > 0, "the edited line's stored memo did not change");
        Assert.Equal(0, memo.TagRowsDeleted);
        Assert.Equal(0, memo.TagRowsInserted);

        // A header tag removed: only the rows naming it go; the other tag's rows stay.
        int rowsOfTwo, rowsOfOne;
        var tags = await db.Tags.AsNoTracking().Where(t => t.LedgerId == w.Ledger.LedgerId).ToDictionaryAsync(t => t.Name, t => t.Id);
        rowsOfTwo = await db.TxnLegNoteTags.CountAsync(t => t.HeaderId == a && t.TagId == tags["t-two"]);
        rowsOfOne = await db.TxnLegNoteTags.CountAsync(t => t.HeaderId == a && t.TagId == tags["t-one"]);
        Assert.True(rowsOfTwo > 0 && rowsOfOne > 0);
        await db.TxnHeaderTags.Where(t => t.HeaderId == a && t.TagId == tags["t-two"]).ExecuteDeleteAsync();
        var removed = await LegNotes.RecomputeAsync(db, [a], CancellationToken.None);
        Assert.Equal(rowsOfTwo, removed.TagRowsDeleted);
        Assert.Equal(0, removed.TagRowsInserted);
        Assert.Equal(rowsOfOne, await db.TxnLegNoteTags.CountAsync(t => t.HeaderId == a && t.TagId == tags["t-one"]));

        // A posting moved to another split by a set-based write, its line carrying a tag
        // of its own: the line's row keeps its key and its roles, and only its header
        // changes, so the header alone must make it a changed row, deleted and inserted
        // again under the new header.
        var c = await CreateBankAsync(w, w.BankA.Id, Day(6, 5), "Payee Split Target", null, (w.CatA.Id, -14m), (w.CatB.Id, -15m));
        var movedLine = await LegOnAsync(a, w.BankA.Id, posting: 1);
        var lineTag = Guid.NewGuid();
        db.Tags.Add(new TagRow { Id = lineTag, LedgerId = w.Ledger.LedgerId, Name = "t-line", CreatedAt = Day(6, 5) });
        db.TxnLegTags.Add(new TxnLegTagRow { LegId = movedLine, TagId = lineTag, LedgerId = w.Ledger.LedgerId, CreatedAt = Day(6, 5) });
        await db.SaveChangesAsync();
        await LegNotes.RecomputeAsync(db, [a], CancellationToken.None);
        var before = await db.TxnLegNoteTags.AsNoTracking().SingleAsync(t => t.LegId == movedLine && t.TagId == lineTag);
        Assert.Equal(a, before.HeaderId);

        await db.TxnLegs.Where(l => l.HeaderId == a && l.PostingIndex == 1).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.HeaderId, c).SetProperty(l => l.PostingIndex, 2));
        // As a writer that moves legs leaves them (the readers' posting counts).
        foreach (var header in new[] { a, c })
            _ = await db.RecomputePostingCountsForHeader(header).Select(r => r.HeaderId).SingleAsync();
        await LegNotes.RecomputeAsync(db, [c], CancellationToken.None);
        var after = await db.TxnLegNoteTags.AsNoTracking().SingleAsync(t => t.LegId == movedLine && t.TagId == lineTag);
        Assert.Equal(
            (before.Shown, before.InPlay, before.Inherited, before.TransactionLevel),
            (after.Shown, after.InPlay, after.Inherited, after.TransactionLevel));
        Assert.Equal(c, after.HeaderId);
        await LegNotes.RecomputeAsync(db, [a, b], CancellationToken.None);
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_stamped_leg_is_inserted_with_its_values_and_the_recompute_writes_none_of_its_columns()
    {
        await using var w = await WorldAsync();
        var id = Guid.NewGuid();
        var header = TxnHeaderRow.NewIngested(id, w.Ledger.LedgerId, w.BankA.Id, origin: "file_import", externalId: $"imp-{id:N}");
        header.ProviderKey = "qif";
        header.Payee = "Payee Stamped";
        header.PostedAt = Day(5, 2);
        header.TransactedAt = header.PostedAt;
        var legs = new[]
        {
            Leg(id, w.Ledger.LedgerId, w.BankA.Id, -5m, 0),
            Leg(id, w.Ledger.LedgerId, w.CatA.Id, 5m, 0),
        };
        legs[0].AssignLegMemo("stamped side");

        // The values a writer knows before the insert: the rule over the rows it will save.
        LegNotes.Stamp(
            new Coffer.Domain.Annotations.NotesHeaderInput(id, null, w.BankA.Id, null, [],
                [.. legs.Select(l => new Coffer.Domain.Annotations.NotesLegInput(l.Id, l.AccountId, l.PostingIndex, l.LegMemo, []))]),
            new Dictionary<Guid, Guid>(), legs);
        Assert.All(legs, l => Assert.NotNull(l.NotesKind));

        // Saved with no recompute, then recomputed: the columns already hold the result.
        await using (var bare = Db.NewDbContext())
        {
            bare.TxnHeaders.Add(header);
            bare.TxnLegs.AddRange(legs);
            await bare.SaveChangesAsync();
            var writes = await LegNotes.RecomputeAsync(bare, [id], CancellationToken.None);
            Assert.Equal(0, writes.LegsUpdated);
        }
        await AssertStoredAsync(w);

        // Insert-only: setting a stamped value on a leg already saved fails its save.
        await using (var db = Db.NewDbContext())
        {
            var saved = await db.TxnLegs.SingleAsync(l => l.Id == legs[1].Id);
            LegNotes.Stamp(
                new Coffer.Domain.Annotations.NotesHeaderInput(id, null, w.CatA.Id, null, [],
                    [new Coffer.Domain.Annotations.NotesLegInput(saved.Id, saved.AccountId, saved.PostingIndex, "other", [])]),
                new Dictionary<Guid, Guid>(), [saved]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_failure_in_any_saved_changes_step_rolls_the_save_back_with_the_other_interceptors_work()
    {
        // A save that recomputes balances runs in its writer's transaction, which takes the
        // ledger write lock first (migration 275), so the balance recompute's SavedChanges
        // work commits or rolls back with it: here a later step fails, and neither the
        // amount nor its balances survive. A save that recomputes nothing, the one after
        // the failure, runs in the transaction the notes interceptor opens.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(8, 2), "Payee Atomic", "before", (w.CatA.Id, -10m));
        var line = await LegOnAsync(h, w.BankA.Id);
        var other = await CreateBankAsync(w, w.BankA.Id, Day(8, 3), "Payee Later", null, (w.CatA.Id, -1m));
        var otherLeg = await LegOnAsync(other, w.BankA.Id);
        decimal BalanceSql(AppDbContext db) => db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.HeaderId == other && b.AccountId == w.BankA.Id).Select(b => b.BalanceAfter).Single();
        decimal before;
        await using (var db = Db.NewDbContext()) before = BalanceSql(db);

        var failing = new FailAfterSave();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(
                new LegDerivedRecomputeInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<LegDerivedRecomputeInterceptor>.Instance),
                failing, new LegNotesRecomputeInterceptor())
            .Options;
        await using (var db = new AppDbContext(options))
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == line);
            leg.AssignLegMemo("after");
            leg.Amount = -500m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId));
            Assert.True(failing.Ran, "the failing step never ran, so nothing was tested");
            // Not left on an aborted transaction: the context saves again, and commits.
            Assert.Null(db.Database.CurrentTransaction);
            failing.Armed = false;
            var next = await db.TxnLegs.SingleAsync(l => l.Id == otherLeg);
            next.AssignLegMemo("saved after the failure");
            await db.SaveChangesAsync();
        }

        await using (var db = Db.NewDbContext())
        {
            // The memo the create placed on the bank's side, as it was before the save.
            var leg = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == line);
            Assert.Equal("before", leg.LegMemo);
            Assert.Equal(-10m, leg.Amount);
            Assert.Equal(before, BalanceSql(db));
            Assert.Equal("saved after the failure", (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == otherLeg)).LegMemo);
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_notes_input_saved_with_no_transaction_rolls_its_trade_price_work_back_with_a_failed_step()
    {
        // The production chain, in its production order, with a probe first and a failing
        // step just before the notes interceptor, on a save that recomputes neither balances
        // nor holdings and opens no transaction of its own: a trade's header memo, an input
        // of the memo-and-tags rule, and its provider payload, which the trade-price rule
        // reads. It runs in the transaction the notes interceptor opens before the write,
        // the trade price's re-derivation with it, so a failure after them leaves none of
        // their work. A save that recomputes holdings runs in its writer's locked
        // transaction instead
        // (A_save_that_recomputes_holdings_in_the_locked_transaction_rolls_its_holdings_work_back_with_a_failed_step).
        await using var w = await WorldAsync();
        var buy = await CreateInvestmentAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 10m, Price = 20m,
            PostedAt = Day(7, 1), Payee = "Payee Chain", Memo = "chain memo",
        });
        string? memoBefore;
        await using (var db = Db.NewDbContext())
            memoBefore = (await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy)).Memo;
        var pricesBefore = await PlantStaleTradePriceAsync(w, Day(7, 1));

        var probe = new TransactionProbe();
        var reseeds = new CommandCounter("fn_trade_price_reseed");
        var failing = new FailAfterSave();
        await using (var db = new AppDbContext(ProductionChain(probe, reseeds, failing)))
        {
            var header = await db.TxnHeaders.SingleAsync(h => h.Id == buy);
            header.ProviderRawPayload = """{"refreshed":true}""";
            header.AssignMemo("after");
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.True(failing.Ran, "the failing step never ran, so nothing was tested");
            Assert.True(reseeds.Count > 0, "the save re-derived no trade price, so nothing was tested");
            Assert.True(probe.SawTransaction, "the first interceptor's SavedChanges ran outside the save's transaction");
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using (var db = Db.NewDbContext())
        {
            var header = await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy);
            Assert.Null(header.ProviderRawPayload);
            Assert.Equal(memoBefore, header.Memo);
            // The re-derivation ran and wrote the rule's price; rolled back with the save,
            // the planted price stands.
            Assert.Equal(pricesBefore, TradePrices(db, w));
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_save_that_changes_no_input_still_runs_the_other_interceptors_work_in_its_transaction()
    {
        // A save that changes no input of the memo-and-tags rule, recomputes neither
        // balances nor holdings and opens no transaction of its own: only the provider
        // payload of a trade's header, which the trade-price rule reads. The notes
        // interceptor opens its transaction for every save, input or not, so the trade
        // price's re-derivation still runs in it, and a failure after it leaves none of that
        // work.
        await using var w = await WorldAsync();
        var buy = await CreateInvestmentAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 10m, Price = 20m,
            PostedAt = Day(7, 2), Payee = "Payee Chain Plain",
        });
        var pricesBefore = await PlantStaleTradePriceAsync(w, Day(7, 2));

        var probe = new TransactionProbe();
        var reseeds = new CommandCounter("fn_trade_price_reseed");
        var failing = new FailAfterSave();
        await using (var db = new AppDbContext(ProductionChain(probe, reseeds, failing)))
        {
            var header = await db.TxnHeaders.SingleAsync(h => h.Id == buy);
            header.ProviderRawPayload = """{"refreshed":true}""";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.True(failing.Ran, "the failing step never ran, so nothing was tested");
            Assert.True(reseeds.Count > 0, "the save re-derived no trade price, so nothing was tested");
            Assert.True(probe.SawTransaction, "the first interceptor's SavedChanges ran outside the save's transaction");
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using (var db = Db.NewDbContext())
        {
            Assert.Null((await db.TxnHeaders.AsNoTracking().SingleAsync(h => h.Id == buy)).ProviderRawPayload);
            Assert.Equal(pricesBefore, TradePrices(db, w));
        }
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_save_that_recomputes_holdings_in_the_locked_transaction_rolls_its_holdings_work_back_with_a_failed_step()
    {
        // The production chain, in its production order, with a probe first and a failing
        // step just before the notes interceptor, on a save that recomputes holdings: a
        // trade's shares, price and amount. It runs in its writer's transaction, which takes
        // the ledger write lock first (migration 275), so the holdings recompute's
        // SavedChanges work, and the balance recompute's, run in it and roll back with it
        // when the later step fails: the position is left as it was, with the trade.
        await using var w = await WorldAsync();
        var buy = await CreateInvestmentAsync(w, new CreateInvestmentTransactionRequest
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 10m, Price = 20m,
            PostedAt = Day(7, 3), Payee = "Payee Chain Holdings",
        });
        string Holdings(AppDbContext db) => string.Join(';', db.Holdings.AsNoTracking()
            .Where(h => h.LedgerId == w.Ledger.LedgerId && h.SecurityId == w.Security)
            .OrderBy(h => h.AccountId).Select(h => $"{h.AccountId}:{h.Quantity}:{h.CostBasis}").ToList());
        Guid tradeLeg;
        decimal tradeAmount;
        string holdingsBefore;
        await using (var db = Db.NewDbContext())
        {
            var trade = await db.TxnLegs.AsNoTracking()
                .Where(l => l.HeaderId == buy && l.SecurityId == w.Security && l.Quantity == 10m)
                .Select(l => new { l.Id, l.Amount }).SingleAsync();
            (tradeLeg, tradeAmount) = (trade.Id, trade.Amount);
            holdingsBefore = Holdings(db);
        }
        Assert.NotEqual(string.Empty, holdingsBefore);

        var probe = new TransactionProbe();
        var recomputes = new CommandCounter("recompute_holdings_for_account_security");
        var failing = new FailAfterSave();
        await using (var db = new AppDbContext(ProductionChain(probe, recomputes, failing)))
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == tradeLeg);
            leg.Quantity = 15m;
            leg.UnitPrice = 30m;
            leg.Amount = tradeAmount * 2m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesUnderLedgerLockAsync(w.Ledger.LedgerId));
            Assert.True(failing.Ran, "the failing step never ran, so nothing was tested");
            Assert.True(recomputes.Count > 0, "the save recomputed no holdings, so nothing was tested");
            Assert.True(probe.SawTransaction, "the first interceptor's SavedChanges ran outside the save's transaction");
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using (var db = Db.NewDbContext())
        {
            // The position first: its recompute ran, and rolled back with the save.
            Assert.Equal(holdingsBefore, Holdings(db));
            var leg = await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == tradeLeg);
            Assert.Equal((10m, tradeAmount), (leg.Quantity!.Value, leg.Amount));
        }
        await AssertStoredAsync(w);
    }

    /// <summary>The security's stored prices, in a form to compare.</summary>
    private static string TradePrices(AppDbContext db, World w) => string.Join(';', db.SecurityPrices.AsNoTracking()
        .Where(p => p.LedgerId == w.Ledger.LedgerId && p.SecurityId == w.Security)
        .OrderBy(p => p.PriceDate).ThenBy(p => p.Source).Select(p => $"{p.PriceDate}:{p.Price}:{p.Source}").ToList());

    /// <summary>The trade price stored for <paramref name="day"/> out of step with its trade,
    /// as the consistency tests plant drift, so a re-derivation of the day that commits
    /// writes over it; returns the security's prices as planted.</summary>
    private async Task<string> PlantStaleTradePriceAsync(World w, DateTime day)
    {
        await using var db = Db.NewDbContext();
        var date = DateOnly.FromDateTime(day);
        Assert.Equal(1, await db.SecurityPrices
            .Where(p => p.LedgerId == w.Ledger.LedgerId && p.SecurityId == w.Security && p.PriceDate == date)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 99m)));
        return TradePrices(db, w);
    }

    /// <summary>The production interceptor chain, in its production order, with
    /// <paramref name="first"/> first, <paramref name="commands"/> watching every command,
    /// and <paramref name="beforeNotes"/> just before the notes interceptor.</summary>
    private Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> ProductionChain(
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor first,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor commands,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor beforeNotes) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(
                first,
                commands,
                new LegDerivedRecomputeInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<LegDerivedRecomputeInterceptor>.Instance),
                new HoldingsRecomputeInterceptor(),
                new TradePriceFromLegInterceptor(),
                beforeNotes,
                new LegNotesRecomputeInterceptor())
            .Options;

    /// <summary>Counts the commands whose text contains <paramref name="marker"/>.</summary>
    private sealed class CommandCounter(string marker) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal)) Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task A_save_that_fails_on_a_row_another_request_removed_leaves_no_transaction_behind()
    {
        // A concurrency failure is reported apart from other failures; the transaction
        // the interceptor opened for the save must still end with it, or the context's
        // next save would run inside it and be lost when the context goes.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(8, 4), "Payee Gone", "gone", (w.CatA.Id, -10m));
        var other = await CreateBankAsync(w, w.BankA.Id, Day(8, 5), "Payee Kept", null, (w.CatA.Id, -2m));
        var line = await LegOnAsync(h, w.BankA.Id);
        var otherLeg = await LegOnAsync(other, w.BankA.Id);

        await using (var db = Db.NewServiceFactory().Create())
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == line);
            await SendAsync(w, HttpMethod.Delete, $"transactions/{h}");
            leg.AssignLegMemo("edited after it went");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            Assert.Null(db.Database.CurrentTransaction);

            db.ChangeTracker.Clear();
            var next = await db.TxnLegs.SingleAsync(l => l.Id == otherLeg);
            next.AssignLegMemo("saved after the conflict");
            await db.SaveChangesAsync();
        }

        await using (var db = Db.NewDbContext())
            Assert.Equal("saved after the conflict", (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == otherLeg)).LegMemo);
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_cancel_that_arrives_as_the_save_commits_does_not_stop_the_commit()
    {
        // Once the write and its recompute are done, the commit takes no token: a cancel
        // arriving then cannot leave the outcome unknown, so the save commits whole.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(8, 6), "Payee Late Cancel", "before", (w.CatA.Id, -10m));
        var line = await LegOnAsync(h, w.BankA.Id);

        using var cts = new CancellationTokenSource();
        var atCommit = new CancelAtCommit(cts);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(atCommit, new LegNotesRecomputeInterceptor())
            .Options;
        await using (var db = new AppDbContext(options))
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == line);
            leg.AssignLegMemo("after");
            await db.SaveChangesAsync(cts.Token);
            Assert.Null(db.Database.CurrentTransaction);
        }
        Assert.True(atCommit.Fired, "the cancel never fired at the commit, so nothing was tested");
        await using (var db = Db.NewDbContext())
            Assert.Equal("after", (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == line)).LegMemo);
        await AssertStoredAsync(w);
    }

    /// <summary>Cancels the save's token as a transaction starts to commit.</summary>
    private sealed class CancelAtCommit(CancellationTokenSource cts) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Fired = true;
            cts.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Records whether its SavedChanges step ran inside a transaction.</summary>
    private sealed class TransactionProbe : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool SawTransaction { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            SawTransaction = eventData.Context?.Database.CurrentTransaction is not null;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Throws in SavedChanges while armed: a step that fails after the write and the balance recompute.</summary>
    private sealed class FailAfterSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Armed { get; set; } = true;
        public bool Ran { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Armed) return ValueTask.FromResult(result);
            Ran = true;
            throw new InvalidOperationException("a step after the save failed");
        }
    }

    [Fact]
    public async Task A_recompute_locks_its_headers_in_id_order()
    {
        // Two recomputes over overlapping headers cannot deadlock only if both lock in one
        // order. One header is held elsewhere; a recompute over it and the headers with
        // smaller ids must, while it waits, already hold every one of those smaller ids.
        // The held header is the one the table stores first, so a lock taken in storage
        // order would wait on it before locking any other.
        await using var w = await WorldAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 12; i++)
            created.Add(await CreateBankAsync(w, w.BankA.Id, Day(7, 1 + i), $"Payee Order {i}", null, (w.CatA.Id, -1m - i)));

        List<Guid> physical;
        await using (var probe = Db.OpenServiceConnection())
        await using (var cmd = new Npgsql.NpgsqlCommand(
            "SELECT id FROM txn_headers WHERE id = ANY(@ids) ORDER BY ctid", probe))
        {
            cmd.Parameters.AddWithValue("ids", created.ToArray());
            physical = [];
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) physical.Add(reader.GetGuid(0));
        }
        var held = physical.First(p => created.Count(c => c.CompareTo(p) < 0) >= 3);
        var smaller = created.Where(c => c.CompareTo(held) < 0).ToList();

        await using var holder = Db.NewDbContext();
        await using var holderTx = await holder.Database.BeginTransactionAsync();
        _ = await holder.LockTxnHeaders([held]).Select(r => r.Locked).SingleAsync();

        await using var racer = Db.NewDbContext();
        var racing = LegNotes.RecomputeAsync(racer, [held, .. smaller], CancellationToken.None);
        Assert.NotSame(racing, await Task.WhenAny(racing, Task.Delay(TimeSpan.FromSeconds(2))));

        var free = new List<Guid>();
        await using (var probe = Db.OpenServiceConnection())
        {
            foreach (var id in smaller)
            {
                await using var tx = await probe.BeginTransactionAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(
                    "SELECT 1 FROM txn_headers WHERE id = @id FOR NO KEY UPDATE NOWAIT", probe, tx);
                cmd.Parameters.AddWithValue("id", id);
                try
                {
                    await cmd.ExecuteScalarAsync();
                    free.Add(id);
                }
                catch (Npgsql.PostgresException e) when (e.SqlState == "55P03")
                {
                    // Held by the waiting recompute, as it should be.
                }
                await tx.RollbackAsync();
            }
        }

        await holderTx.CommitAsync();
        await racing;
        Assert.True(free.Count == 0,
            $"{free.Count} of {smaller.Count} smaller headers were not locked while the recompute waited");
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_cancel_between_the_write_and_its_recompute_rolls_both_back()
    {
        // The recompute runs in the save's transaction, so a request cancelled after the
        // write and before the recompute ends with neither: the memo as it was, and the
        // stored values that go with it. Nothing is committed half done.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(8, 1), "Payee Cancel", "before", (w.CatA.Id, -10m), (w.CatB.Id, -11m));
        var line = await LegOnAsync(h, w.BankA.Id);

        using var cts = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(new CancelAfterSave(cts), new LegNotesRecomputeInterceptor())
            .Options;
        await using (var db = new AppDbContext(options))
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == line);
            leg.AssignLegMemo("after");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cts.Token));
            Assert.Null(db.Database.CurrentTransaction);
        }
        Assert.True(cts.IsCancellationRequested, "the cancel never fired, so nothing was tested");
        await using (var db = Db.NewDbContext())
            Assert.Null((await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == line)).LegMemo);
        await AssertStoredAsync(w);
    }

    [Fact]
    public async Task A_cancelled_save_leaves_no_interceptor_holding_its_context()
    {
        // Every save interceptor that keeps state per save keys it by the context, from
        // its SavingChanges to its SavedChanges. A cancel between them ends the save with
        // no SavedChanges run: each drops its entry on SaveChangesCanceled, and keys it
        // weakly besides, as an entry left past a save (a concurrency failure, which EF
        // reports as neither a failure nor a cancel) must not keep a disposed context, its
        // change tracker with it, alive for the life of the process. Read off each
        // interceptor's own table, found by its type, so one added later is held to it.
        await using var w = await WorldAsync();
        var h = await CreateBankAsync(w, w.BankA.Id, Day(8, 7), "Payee Cancelled", "before", (w.CatA.Id, -10m));
        var line = await LegOnAsync(h, w.BankA.Id);
        string? memoBefore;
        await using (var db = Db.NewDbContext())
            memoBefore = (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == line)).LegMemo;

        using var cts = new CancellationTokenSource();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.ServiceConnectionString)
            .AddInterceptors(
                new CancelOnSaved(cts),
                new LegDerivedRecomputeInterceptor(Microsoft.Extensions.Logging.Abstractions.NullLogger<LegDerivedRecomputeInterceptor>.Instance),
                new HoldingsRecomputeInterceptor(),
                new TradePriceFromLegInterceptor(),
                new LegNotesRecomputeInterceptor())
            .Options;
        await using (var db = new AppDbContext(options))
        {
            var leg = await db.TxnLegs.SingleAsync(l => l.Id == line);
            leg.AssignLegMemo("after");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cts.Token));
            Assert.Null(db.Database.CurrentTransaction);

            var tables = typeof(AppDbContext).Assembly.GetTypes()
                .Where(t => typeof(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor).IsAssignableFrom(t))
                .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Static
                                             | System.Reflection.BindingFlags.Public
                                             | System.Reflection.BindingFlags.NonPublic)
                    .Where(f => f.FieldType.IsGenericType
                                && f.FieldType.GetGenericArguments().Contains(typeof(DbContext)))
                    .Select(f => (Name: $"{t.Name}.{f.Name}", Field: f)))
                .ToList();
            Assert.True(tables.Count >= 4,
                $"found only [{string.Join(", ", tables.Select(t => t.Name))}]; the scan is not finding the interceptors' tables");
            foreach (var (name, field) in tables)
            {
                Assert.True(field.FieldType.GetGenericTypeDefinition() == typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>),
                    $"{name} holds its contexts strongly ({field.FieldType.Name}); key it weakly");
                var lookup = new object?[] { db, null };
                Assert.False((bool)field.FieldType.GetMethod("TryGetValue")!.Invoke(field.GetValue(null), lookup)!,
                    $"{name} still holds the cancelled save's entry");
            }
        }
        Assert.True(cts.IsCancellationRequested, "the cancel never fired, so nothing was tested");

        // Nothing of the cancelled save was written.
        await using (var db = Db.NewDbContext())
            Assert.Equal(memoBefore, (await db.TxnLegs.AsNoTracking().SingleAsync(l => l.Id == line)).LegMemo);
        Assert.NotEqual("after", memoBefore);
        await AssertStoredAsync(w);
    }

    /// <summary>Registered first: cancels the save's token and throws as the save's statements
    /// complete, so no interceptor after it reaches its SavedChanges.</summary>
    private sealed class CancelOnSaved(CancellationTokenSource cts) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Cancels the save's token once the save has committed, before the interceptors after it run.</summary>
    private sealed class CancelAfterSave(CancellationTokenSource cts) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private static TxnLegRow Leg(Guid headerId, Guid ledgerId, Guid accountId, decimal amount, int posting) => new()
    {
        Id = Guid.NewGuid(), HeaderId = headerId, LedgerId = ledgerId,
        AccountId = accountId, PostingIndex = posting, Amount = amount,
    };

    private static IEnumerable<CreateInvestmentTransactionRequest> InvestmentCreates(World w)
    {
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 10m, Price = 20m,
            PostedAt = Day(4, 1), Payee = "Payee Buy", Memo = "buy memo",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Sell, SecurityId = w.Security, Shares = 1m, Price = 25m,
            PostedAt = Day(4, 2), Payee = "Payee Sell",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.DividendCash, SecurityId = w.Security,
            CategoryAccountId = w.Income.Id, Amount = 12m, PostedAt = Day(4, 3), Payee = "Payee Dividend", Memo = "div memo",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Transfer, TransferAccountId = w.BankA.Id,
            Amount = 40m, PostedAt = Day(4, 4), Payee = "Payee Xfr", Memo = "xfr memo",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.Buy, SecurityId = w.Security, Shares = 3m, Price = 21m,
            FeeAccountId = w.Fee.Id, FeeAmount = 3m, PostedAt = Day(4, 5), Payee = "Payee Buy Fee", Memo = "fee memo",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.BuyXfr, SecurityId = w.Security, Shares = 2m, Price = 22m,
            TransferAccountId = w.BrokerB.Id, FeeAccountId = w.Fee.Id, FeeAmount = 2m, PostedAt = Day(4, 6), Payee = "Payee Funded",
        };
        yield return new()
        {
            BrokerageAccountId = w.BrokerA.Id, Action = LedgerActions.TransferShares, SecurityId = w.Security, Shares = 4m,
            TransferAccountId = w.BrokerB.Id, PostedAt = Day(4, 7), Payee = "Payee In Kind", Memo = "in kind memo",
        };
    }
}
