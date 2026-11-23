using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Rls;

/// <summary>
/// The write gaps FU-086 closed (one of the five, sidebar tabs, went with its tables
/// in mig 258) and the role-aware pair on the folders that replaced them, each attacked
/// directly as coffer_app with the API's checks out of the way, and each paired with
/// the write the same policy permits, so a refusal is the policy's and not a missing
/// foreign key's.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RlsWriteBoundaryTests : DatabaseTest
{
    public RlsWriteBoundaryTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>42501: a privilege refused, or a row RLS will not admit.</summary>
    private static async Task AssertRefusedAsync(Func<Task> write)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(write);
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.True(pg is not null, $"refused by something other than Postgres: {ex}");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, pg!.SqlState);
    }

    [Fact]
    public async Task A_viewer_cannot_tag_a_posting_and_an_editor_can()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewer = await ledger.AddMemberAsync("viewer");
        var editor = await ledger.AddMemberAsync("editor");
        var a = await ledger.AddBankAccountAsync("checking");
        var b = await ledger.AddBankAccountAsync("savings");
        var (leg, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, 10m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        var tag = await ledger.AddBareTagAsync("audit-" + Guid.NewGuid().ToString("N")[..8]);
        TxnLegTagRow Row() => new() { LegId = leg, TagId = tag, LedgerId = ledger.LedgerId };

        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            db.TxnLegTags.Add(Row());
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(editor))
        {
            db.TxnLegTags.Add(Row());
            Assert.Equal(1, await db.SaveChangesAsync());
        }
        // The boundary is tested on a bare context, which no save interceptor follows:
        // store the presentation the tag changed, as the upgrade's backfill does.
        await ledger.RecomputeNotesAsync(leg);
    }

    [Fact]
    public async Task A_viewer_cannot_write_the_operation_log_or_a_schedule()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewer = await ledger.AddMemberAsync("viewer");
        var editor = await ledger.AddMemberAsync("editor");
        var now = DateTime.UtcNow;
        LedgerOperationRow Operation(Guid by) => new()
        {
            Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, Family = "quote", ProviderKey = "yahoo",
            TriggeredVia = "manual", TriggeredByUserId = by, StartedAt = now,
        };
        ScheduledJobRow Schedule(Guid by, string jobType) => new()
        {
            LedgerId = ledger.LedgerId, JobType = jobType, Enabled = true, ConfiguredByUserId = by,
            CreatedAt = now, UpdatedAt = now,
        };

        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            db.LedgerOperations.Add(Operation(viewer));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            db.ScheduledJobs.Add(Schedule(viewer, "quote-refresh"));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(editor))
        {
            db.LedgerOperations.Add(Operation(editor));
            db.ScheduledJobs.Add(Schedule(editor, "quote-refresh"));
            Assert.Equal(2, await db.SaveChangesAsync());

            // A viewer still reads both: Settings → Activity and the schedule panel.
        }
        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            Assert.True(await db.LedgerOperations.AnyAsync(o => o.LedgerId == ledger.LedgerId));
            Assert.True(await db.ScheduledJobs.AnyAsync(j => j.LedgerId == ledger.LedgerId));
        }
    }

    [Fact]
    public async Task A_viewer_reads_folders_but_cannot_write_them_and_an_editor_can()
    {
        // Folders (mig 258) are ledger data, not per-user view state as the tabs they
        // replace were, so they take the role-aware pair: any grant reads, owner or
        // editor writes. Both tables, attacked as coffer_app with the API out of the way.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var viewer = await ledger.AddMemberAsync("viewer");
        var editor = await ledger.AddMemberAsync("editor");
        var account = await ledger.AddBankAccountAsync("checking");
        var existing = Guid.NewGuid();
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.AccountFolders.Add(new AccountFolderRow { Id = existing, LedgerId = ledger.LedgerId, AccountType = "bank", Name = "existing" });
            await svc.SaveChangesAsync();
        }
        AccountFolderRow Folder(string name) => new() { Id = Guid.NewGuid(), LedgerId = ledger.LedgerId, AccountType = "bank", Name = name };
        AccountFolderMemberRow Member() => new() { AccountId = account.Id, LedgerId = ledger.LedgerId, FolderId = existing, AccountType = "bank" };

        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            Assert.True(await db.AccountFolders.AnyAsync(f => f.Id == existing));
            db.AccountFolders.Add(Folder("by viewer"));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            db.AccountFolderMembers.Add(Member());
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(viewer))
        {
            Assert.Equal(0, await db.AccountFolders.Where(f => f.Id == existing)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, "renamed by viewer")));
            Assert.Equal(0, await db.AccountFolders.Where(f => f.Id == existing).ExecuteDeleteAsync());
        }
        await using (var db = Db.NewAppDbContextAsUser(editor))
        {
            db.AccountFolders.Add(Folder("by editor"));
            db.AccountFolderMembers.Add(Member());
            Assert.Equal(2, await db.SaveChangesAsync());
            Assert.Equal(1, await db.AccountFolders.Where(f => f.Id == existing)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Name, "renamed by editor")));
        }
    }

    [Fact]
    public async Task A_shared_report_is_read_by_every_member_and_changed_by_its_owner_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var author = await ledger.AddMemberAsync("viewer");
        var reportId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var svc = Db.NewServiceDbContext())
        {
            svc.SavedReports.Add(new SavedReportRow
            {
                Id = reportId, LedgerId = ledger.LedgerId, OwnerUserId = author, Name = "shared",
                Spec = "{}", Visibility = SavedReportVisibility.Ledger, CreatedAt = now, UpdatedAt = now,
            });
            await svc.SaveChangesAsync();
        }

        // The ledger's owner sees it; deleting it, or taking it over, admits no row.
        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            Assert.True(await db.SavedReports.AnyAsync(r => r.Id == reportId));
            Assert.Equal(0, await db.SavedReports.Where(r => r.Id == reportId).ExecuteDeleteAsync());
            Assert.Equal(0, await db.SavedReports.Where(r => r.Id == reportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "taken")));
        }
        await using (var db = Db.NewAppDbContextAsUser(author))
        {
            Assert.Equal(1, await db.SavedReports.Where(r => r.Id == reportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "renamed by its author")));
            Assert.Equal(1, await db.SavedReports.Where(r => r.Id == reportId).ExecuteDeleteAsync());
        }
    }

    [Fact]
    public async Task Invites_and_snapshot_parts_are_the_service_roles_alone()
    {
        // Invites go through the service role for every operation. A snapshot's parts
        // carry every member's private rows of the ledger; create and restore run as the
        // service role, and a snapshot's delete reaches its parts by cascade.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);

        await AssertRefusedAsync(() => db.Invites.AnyAsync());
        await AssertRefusedAsync(() => db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM ledger_snapshot_parts").ToListAsync());
    }
}
