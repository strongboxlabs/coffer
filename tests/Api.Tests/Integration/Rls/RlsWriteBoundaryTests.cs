using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Rls;

/// <summary>
/// The five write gaps FU-086 closed, each attacked directly as coffer_app with the
/// API's checks out of the way, and each paired with the write the same policy permits,
/// so a refusal is the policy's and not a missing foreign key's.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RlsWriteBoundaryTests
{
    private readonly PostgresFixture _fixture;

    public RlsWriteBoundaryTests(PostgresFixture fixture) => _fixture = fixture;

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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var viewer = await ledger.AddMemberAsync("viewer");
        var editor = await ledger.AddMemberAsync("editor");
        var a = await ledger.AddBankAccountAsync("checking");
        var b = await ledger.AddBankAccountAsync("savings");
        var (leg, _) = await ledger.AddTransactionPairAsync(a.Id, b.Id, 10m, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        var tag = await ledger.AddBareTagAsync("audit-" + Guid.NewGuid().ToString("N")[..8]);
        TxnLegTagRow Row() => new() { LegId = leg, TagId = tag, LedgerId = ledger.LedgerId };

        await using (var db = _fixture.NewAppDbContextAsUser(viewer))
        {
            db.TxnLegTags.Add(Row());
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(editor))
        {
            db.TxnLegTags.Add(Row());
            Assert.Equal(1, await db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_viewer_cannot_write_the_operation_log_or_a_schedule()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
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

        await using (var db = _fixture.NewAppDbContextAsUser(viewer))
        {
            db.LedgerOperations.Add(Operation(viewer));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(viewer))
        {
            db.ScheduledJobs.Add(Schedule(viewer, "quote-refresh"));
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(editor))
        {
            db.LedgerOperations.Add(Operation(editor));
            db.ScheduledJobs.Add(Schedule(editor, "quote-refresh"));
            Assert.Equal(2, await db.SaveChangesAsync());

            // A viewer still reads both: Settings → Activity and the schedule panel.
        }
        await using (var db = _fixture.NewAppDbContextAsUser(viewer))
        {
            Assert.True(await db.LedgerOperations.AnyAsync(o => o.LedgerId == ledger.LedgerId));
            Assert.True(await db.ScheduledJobs.AnyAsync(j => j.LedgerId == ledger.LedgerId));
        }
    }

    [Fact]
    public async Task A_member_cannot_touch_another_members_account_group_but_can_their_own()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var other = await ledger.AddMemberAsync("editor");
        var account = await ledger.AddBankAccountAsync("checking");
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await using (var svc = _fixture.NewServiceDbContext())
        {
            svc.UserAccountGroups.Add(new UserAccountGroupRow { Id = mine, UserId = ledger.UserId, LedgerId = ledger.LedgerId, Name = "mine" });
            svc.UserAccountGroups.Add(new UserAccountGroupRow { Id = theirs, UserId = other, LedgerId = ledger.LedgerId, Name = "theirs" });
            svc.UserAccountGroupMembers.Add(new UserAccountGroupMemberRow { GroupId = theirs, AccountId = account.Id, LedgerId = ledger.LedgerId });
            await svc.SaveChangesAsync();
        }

        // The ledger's own creator is its owner, and still has no business in a group that
        // is someone else's.
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            db.UserAccountGroupMembers.Add(new UserAccountGroupMemberRow { GroupId = theirs, AccountId = Guid.NewGuid(), LedgerId = ledger.LedgerId });
            await AssertRefusedAsync(() => db.SaveChangesAsync());
        }
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            Assert.Equal(0, await db.UserAccountGroupMembers.Where(m => m.GroupId == theirs).ExecuteDeleteAsync());
        }
        await using (var svc = _fixture.NewServiceDbContext())
        {
            Assert.True(await svc.UserAccountGroupMembers.AnyAsync(m => m.GroupId == theirs), "the other member's group was emptied");
        }
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            db.UserAccountGroupMembers.Add(new UserAccountGroupMemberRow { GroupId = mine, AccountId = account.Id, LedgerId = ledger.LedgerId });
            Assert.Equal(1, await db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task A_shared_report_is_read_by_every_member_and_changed_by_its_owner_alone()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var author = await ledger.AddMemberAsync("viewer");
        var reportId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var svc = _fixture.NewServiceDbContext())
        {
            svc.SavedReports.Add(new SavedReportRow
            {
                Id = reportId, LedgerId = ledger.LedgerId, OwnerUserId = author, Name = "shared",
                Spec = "{}", Visibility = SavedReportVisibility.Ledger, CreatedAt = now, UpdatedAt = now,
            });
            await svc.SaveChangesAsync();
        }

        // The ledger's owner sees it; deleting it, or taking it over, admits no row.
        await using (var db = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            Assert.True(await db.SavedReports.AnyAsync(r => r.Id == reportId));
            Assert.Equal(0, await db.SavedReports.Where(r => r.Id == reportId).ExecuteDeleteAsync());
            Assert.Equal(0, await db.SavedReports.Where(r => r.Id == reportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Name, "taken")));
        }
        await using (var db = _fixture.NewAppDbContextAsUser(author))
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);

        await AssertRefusedAsync(() => db.Invites.AnyAsync());
        await AssertRefusedAsync(() => db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM ledger_snapshot_parts").ToListAsync());
    }
}
