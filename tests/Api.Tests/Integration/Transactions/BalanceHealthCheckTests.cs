using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Checking balances reports drift without repairing it; repairing is separate.
/// </summary>
/// <remarks>
/// If the only implementation of the running-sum rules lived inside the recompute's
/// DELETE + INSERT, the only way to learn whether a balance was right would be to
/// overwrite it, and a diagnostic would silently rewrite every balance row it walked.
/// Migration 206 separates the pure walk from the persist, so these tests pin
/// <b>a question that does not change the answer</b>. A row the walk produces and
/// nothing stores is drift as much as a stored row that disagrees, and the repair
/// restores it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class BalanceHealthCheckTests : DatabaseTest
{
    public BalanceHealthCheckTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Corrupt one stored balance the way an out-of-band write would.</summary>
    private async Task<(Guid AccountId, Guid HeaderId, decimal Corrupted)> CorruptOneAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.HeaderId)
            .FirstAsync();
        var corrupted = row.BalanceAfter + 1_234.56m;
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE txn_header_account_balances
               SET balance_after = {corrupted}
             WHERE header_id = {row.HeaderId} AND account_id = {row.AccountId};");
        return (row.AccountId, row.HeaderId, corrupted);
    }

    /// <summary>
    /// Remove one stored balance row: what a write that skipped the recompute leaves
    /// for a header it added. Deliberate corruption for the checker, written through EF
    /// past the writers that recompute, as the balances consistency drift test
    /// corrupts a row's value.
    /// </summary>
    private async Task<(Guid AccountId, Guid HeaderId, decimal Walked)> RemoveOneAsync(Guid ledgerId)
    {
        await using var db = Db.NewDbContext();
        var row = await db.TxnHeaderAccountBalances.AsNoTracking()
            .Where(b => b.LedgerId == ledgerId)
            .OrderBy(b => b.HeaderId)
            .FirstAsync();
        var removed = await db.TxnHeaderAccountBalances
            .Where(b => b.HeaderId == row.HeaderId && b.AccountId == row.AccountId)
            .ExecuteDeleteAsync();
        Assert.Equal(1, removed);
        // The ledger checked clean before the removal, so the removed row held the walk's value.
        return (row.AccountId, row.HeaderId, row.BalanceAfter);
    }

    private async Task<bool> StoredRowExistsAsync(Guid accountId, Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return await db.TxnHeaderAccountBalances.AsNoTracking()
            .AnyAsync(b => b.AccountId == accountId && b.HeaderId == headerId);
    }

    private async Task<decimal> StoredBalanceAsync(Guid accountId, Guid headerId)
    {
        await using var db = Db.NewDbContext();
        return (await db.TxnHeaderAccountBalances.AsNoTracking()
            .SingleAsync(b => b.AccountId == accountId && b.HeaderId == headerId))
            .BalanceAfter;
    }

    private async Task<SyntheticLedger> SeededLedgerAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var groceries = await ledger.AddCategoryAsync("groceries");
        for (var i = 0; i < 4; i++)
            await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 10 + i));
        return ledger;
    }

    [Fact]
    public async Task Check_reports_drift_and_leaves_the_stored_value_alone()
    {
        var ledger = await SeededLedgerAsync();
        var (accountId, headerId, corrupted) = await CorruptOneAsync(ledger.LedgerId);

        await using var db = Db.NewDbContext();
        var report = await new RegisterRepository(db, TimeProvider.System).CheckBalancesAsync(ledger.LedgerId);

        // It must SEE the drift ...
        Assert.False(report.Healthy);
        var hit = Assert.Single(report.Drifted, d => d.HeaderId == headerId);
        Assert.Equal(corrupted, hit.StoredBefore);
        Assert.NotEqual(corrupted, hit.RecomputedAfter);

        // ... and must NOT have fixed it. This is the whole point: the old
        // implementation could not report drift without erasing it.
        Assert.Equal(corrupted, await StoredBalanceAsync(accountId, headerId));
    }

    [Fact]
    public async Task Repair_fixes_the_drift_and_a_second_check_is_clean()
    {
        var ledger = await SeededLedgerAsync();
        var (accountId, headerId, corrupted) = await CorruptOneAsync(ledger.LedgerId);

        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);

        var repaired = await repo.VerifyAndHealBalancesAsync(ledger.LedgerId);
        Assert.Contains(repaired.Drifted, d => d.HeaderId == headerId);
        Assert.NotEqual(corrupted, await StoredBalanceAsync(accountId, headerId));

        // The check and the repair share one implementation of the rules, so
        // after a repair the check must agree there is nothing left.
        var after = await repo.CheckBalancesAsync(ledger.LedgerId);
        Assert.True(after.Healthy, $"{after.DriftedCount} rows still drifted after repair");
    }

    [Fact]
    public async Task Check_reports_a_missing_row_as_drift_and_leaves_it_missing()
    {
        var ledger = await SeededLedgerAsync();
        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);

        // Anchor: the ledger checks clean, and the row is there, before it is removed.
        Assert.True((await repo.CheckBalancesAsync(ledger.LedgerId)).Healthy);
        var (accountId, headerId, walked) = await RemoveOneAsync(ledger.LedgerId);

        // A walked row with no stored row is drift, said as missing: Stored reads as 0,
        // so the difference is the whole walked balance.
        var report = await repo.CheckBalancesAsync(ledger.LedgerId);
        Assert.False(report.Healthy);
        var hit = Assert.Single(report.Drifted);
        Assert.Equal((accountId, headerId), (hit.AccountId, hit.HeaderId));
        Assert.Equal(BalanceHealthDriftDto.NotStored, hit.Reason);
        Assert.Equal(0m, hit.StoredBefore);
        Assert.Equal(walked, hit.RecomputedAfter);
        Assert.Equal(walked, hit.Diff);

        // The ledger-wide consistency report names it the same way.
        var consistency = await new LedgerConsistencyRepository(
                db, repo, new HoldingsRecomputeService(db), TimeProvider.System)
            .CheckAsync(ledger.LedgerId);
        var balances = Assert.Single(consistency.Projections, p => p.Projection == ConsistencyProjections.Balances);
        Assert.False(balances.Healthy);
        var mismatch = Assert.Single(balances.Mismatches);
        Assert.Equal((accountId, headerId), (mismatch.AccountId, mismatch.HeaderId));
        Assert.Equal(ConsistencyReasons.NotStored, mismatch.Reason);
        Assert.Equal(walked, mismatch.Expected);

        // Neither check wrote it back.
        Assert.False(await StoredRowExistsAsync(accountId, headerId));
    }

    [Fact]
    public async Task Repair_restores_a_missing_row_and_reports_it()
    {
        var ledger = await SeededLedgerAsync();
        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);
        Assert.True((await repo.CheckBalancesAsync(ledger.LedgerId)).Healthy);
        var (accountId, headerId, walked) = await RemoveOneAsync(ledger.LedgerId);

        var repaired = await repo.VerifyAndHealBalancesAsync(ledger.LedgerId);
        Assert.False(repaired.Healthy);
        var hit = Assert.Single(repaired.Drifted);
        Assert.Equal((accountId, headerId), (hit.AccountId, hit.HeaderId));
        Assert.Equal(BalanceHealthDriftDto.NotStored, hit.Reason);
        Assert.Equal(0m, hit.StoredBefore);
        Assert.Equal(walked, hit.RecomputedAfter);
        Assert.Equal(walked, await StoredBalanceAsync(accountId, headerId));

        var after = await repo.CheckBalancesAsync(ledger.LedgerId);
        Assert.True(after.Healthy, $"{after.DriftedCount} rows still drifted after repair");
    }

    // ---- stored rows the walk does not produce ----------------------------------------

    /// <summary>A ledger whose headers include every kind the walk writes no running
    /// balance for, each made the way the app makes it, and an account with no line.</summary>
    private sealed record SkippedHeaders(
        SyntheticLedger Ledger, Guid Bank, Guid Idle, Guid Hidden, Guid Merged, Guid Template, Guid Visible);

    private async Task<SkippedHeaders> SkippedHeadersAsync()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking");
        var idle = await ledger.AddBankAccountAsync("idle");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var legs = new List<Guid>();
        for (var i = 0; i < 4; i++)
            legs.Add((await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 10 + i))).FromTxnId);
        var hidden = await ledger.ResolveHeaderIdAsync(legs[0]);
        var merged = await ledger.ResolveHeaderIdAsync(legs[1]);
        var visible = await ledger.ResolveHeaderIdAsync(legs[3]);
        await ledger.HideTransactionAsync(hidden);
        await ledger.MarkTransactionMergedAsync(merged, visible);
        // The hide and the merge write past the recompute, so the balances are rebuilt as a
        // save would leave them (SyntheticLedger.RecomputeBalancesAsync).
        await ledger.RecomputeBalancesAsync([bank.Id, groceries.Id]);

        // A reminder's template, through the API: its lines sit on the bank account, and the
        // walk writes no running balance for a template.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var created = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/reminders", new CreateReminderRequest
        {
            Rrule = "FREQ=MONTHLY;BYMONTHDAY=20",
            StartDate = new DateOnly(2026, 5, 20),
            Payee = "payee",
            SourceAccountId = bank.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -30m }],
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var seriesId = (await created.Content.ReadFromJsonAsync<ReminderDetail>())!.Id;
        await using var db = Db.NewDbContext();
        var template = await db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == seriesId).Select(r => r.TemplateHeaderId!.Value).SingleAsync();

        return new SkippedHeaders(ledger, bank.Id, idle.Id, hidden, merged, template, visible);
    }

    /// <summary>
    /// Store a running balance the walk does not produce: drift planted on purpose for the
    /// checker, written through EF past the writers that recompute, as the removal and the
    /// corruption above are.
    /// </summary>
    private async Task PlantAsync(Guid ledgerId, Guid headerId, Guid accountId, decimal balance)
    {
        await using var db = Db.NewDbContext();
        db.TxnHeaderAccountBalances.Add(new TxnHeaderAccountBalanceRow
        {
            HeaderId = headerId, AccountId = accountId, LedgerId = ledgerId, BalanceAfter = balance, NetAmount = 0m,
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("merged")]
    [InlineData("template")]
    [InlineData("account-with-no-line")]
    public async Task A_stored_row_the_walk_does_not_produce_is_reported_and_the_repair_removes_it(string kind)
    {
        var s = await SkippedHeadersAsync();
        var (headerId, accountId) = kind switch
        {
            "hidden" => (s.Hidden, s.Bank),
            "merged" => (s.Merged, s.Bank),
            "template" => (s.Template, s.Bank),
            _ => (s.Visible, s.Idle),
        };
        const decimal Planted = 777.77m;
        var ledgerId = s.Ledger.LedgerId;

        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);
        var consistency = new LedgerConsistencyRepository(db, repo, new HoldingsRecomputeService(db), TimeProvider.System);
        // Anchor: clean before the plant, so what is reported after is the plant alone.
        Assert.True((await repo.CheckBalancesAsync(ledgerId)).Healthy);
        await PlantAsync(ledgerId, headerId, accountId, Planted);

        // The balance check reports it as a row the walk does not produce.
        var report = await repo.CheckBalancesAsync(ledgerId);
        Assert.False(report.Healthy);
        var hit = Assert.Single(report.Drifted);
        Assert.Equal((accountId, headerId), (hit.AccountId, hit.HeaderId));
        Assert.Equal(BalanceHealthDriftDto.NotDerived, hit.Reason);
        Assert.Equal(Planted, hit.StoredBefore);
        Assert.Equal(0m, hit.RecomputedAfter);
        Assert.Equal(-Planted, hit.Diff);

        // The ledger-wide report names it in the same shape, and the check wrote nothing.
        var balances = Assert.Single((await consistency.CheckAsync(ledgerId)).Projections,
            p => p.Projection == ConsistencyProjections.Balances);
        Assert.False(balances.Healthy);
        var mismatch = Assert.Single(balances.Mismatches);
        Assert.Equal((accountId, headerId), (mismatch.AccountId, mismatch.HeaderId));
        Assert.Equal(ConsistencyReasons.NotDerived, mismatch.Reason);
        Assert.Equal("balance_after, not derived", mismatch.Field);
        Assert.Equal((Planted, 0m), (mismatch.Stored, mismatch.Expected));
        Assert.True(await StoredRowExistsAsync(accountId, headerId));

        // The balances repair removes it, says so, and leaves the ledger clean.
        var repaired = await consistency.RepairAsync(ledgerId, ConsistencyProjections.Balances);
        var removed = Assert.Single(repaired.Repaired.Mismatches);
        Assert.Equal((accountId, headerId), (removed.AccountId, removed.HeaderId));
        Assert.Equal(ConsistencyReasons.NotDerived, removed.Reason);
        Assert.False(await StoredRowExistsAsync(accountId, headerId));
        Assert.True(repaired.ProjectionAfter().Healthy);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("merged")]
    [InlineData("header-with-no-line-on-the-account")]
    public async Task A_stored_row_the_walk_does_not_produce_never_seeds_a_later_saves_recompute(string kind)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("checking", openingBalance: 1_000m);
        var other = await ledger.AddBankAccountAsync("other");
        var groceries = await ledger.AddCategoryAsync("groceries");
        var first = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 10))).FromTxnId);
        await ledger.AddTransactionPairAsync(bank.Id, groceries.Id, -25m, Utc(2026, 5, 11));

        // The header the planted row sits on: dated after every row the walk counts on the
        // account and before the save below, so it is the last stored row before the save's
        // anchor, which is where a recompute takes its seed.
        var straySource = kind == "header-with-no-line-on-the-account" ? other.Id : bank.Id;
        var stray = await ledger.ResolveHeaderIdAsync(
            (await ledger.AddTransactionPairAsync(straySource, groceries.Id, -40m, Utc(2026, 5, 15))).FromTxnId);
        if (kind == "hidden") await ledger.HideTransactionAsync(stray);
        if (kind == "merged") await ledger.MarkTransactionMergedAsync(stray, first);
        await ledger.RecomputeBalancesAsync([bank.Id, other.Id, groceries.Id]);

        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);
        Assert.True((await repo.CheckBalancesAsync(ledger.LedgerId)).Healthy);
        await PlantAsync(ledger.LedgerId, stray, bank.Id, 99_999m);

        // A save after it, through the API: its recompute starts at its own date.
        await using var factory = new ApiFactory(Db).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={await ledger.IssueSessionCookieAsync()}");
        var saved = await client.PostAsJsonAsync($"/api/ledgers/{ledger.LedgerId}/transactions", new CreateTransactionRequest
        {
            PostedAt = Utc(2026, 5, 20),
            Payee = "payee",
            SourceAccountId = bank.Id,
            Postings = [new TransactionPosting { CounterpartyAccountId = groceries.Id, Amount = -10m }],
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var savedHeader = (await saved.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("headerId").GetGuid();

        // The save wrote the walk's balance, seeded from the last row the walk counts:
        // the opening balance less the two counted rows and the save.
        Assert.Equal(1_000m - 25m - 25m - 10m, await StoredBalanceAsync(bank.Id, savedHeader));

        // So the only drift is the planted row itself, which the repair then removes.
        var report = await repo.CheckBalancesAsync(ledger.LedgerId);
        var only = Assert.Single(report.Drifted);
        Assert.Equal((bank.Id, stray, BalanceHealthDriftDto.NotDerived), (only.AccountId, only.HeaderId, only.Reason));
        await repo.VerifyAndHealBalancesAsync(ledger.LedgerId);
        Assert.True((await repo.CheckBalancesAsync(ledger.LedgerId)).Healthy);
    }

    [Fact]
    public async Task A_ledger_with_every_kind_of_skipped_header_and_nothing_planted_is_clean()
    {
        var s = await SkippedHeadersAsync();
        await using var db = Db.NewDbContext();
        var repo = new RegisterRepository(db, TimeProvider.System);

        var report = await repo.CheckBalancesAsync(s.Ledger.LedgerId);
        Assert.True(report.Healthy, $"unexpected drift: {string.Join(", ", report.Drifted.Select(d => d.Reason))}");
        Assert.True(report.RowsChecked > 0, "checked nothing — the walk found no rows");

        // The repair finds nothing to rewrite or remove either.
        var repaired = await repo.VerifyAndHealBalancesAsync(s.Ledger.LedgerId);
        Assert.True(repaired.Healthy, $"the repair rewrote {repaired.DriftedCount} rows of a clean ledger");
    }

    [Fact]
    public async Task Check_is_clean_on_an_untouched_ledger()
    {
        var ledger = await SeededLedgerAsync();

        await using var db = Db.NewDbContext();
        var report = await new RegisterRepository(db, TimeProvider.System).CheckBalancesAsync(ledger.LedgerId);

        // Guards against the opposite failure: a check that always reports drift
        // (a seed mismatch, say) would be just as useless as one that heals.
        Assert.True(report.Healthy, $"unexpected drift: {report.DriftedCount}");
        Assert.True(report.RowsChecked > 0, "checked nothing — the walk found no rows");
    }
}
