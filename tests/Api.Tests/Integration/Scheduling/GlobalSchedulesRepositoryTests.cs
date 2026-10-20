using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Scheduling;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Scheduling;

/// <summary>
/// The global (non-ledger) schedule store (mig 139). One row per job_type
/// deployment-wide, so each test resets the 'backup' row up front — the
/// ApiCollection runs sequentially, so this is deterministic (same approach
/// as the bootstrap-token suite). Covers upsert, the enabled→next_run_at
/// computation, and that the schedule and the sealed passphrase are written
/// independently (each preserves the other).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GlobalSchedulesRepositoryTests
{
    private readonly PostgresFixture _fixture;

    public GlobalSchedulesRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

    private GlobalSchedulesRepository NewRepo() => new(_fixture.NewServiceFactory());

    [Fact]
    public async Task Get_returns_null_when_never_configured()
    {
        await ResetBackupRowAsync();
        Assert.Null(await NewRepo().GetAsync(GlobalJobTypes.Backup));
    }

    [Fact]
    public async Task UpsertSchedule_creates_then_reflects_the_row()
    {
        await ResetBackupRowAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = NewRepo();
        var now = DateTime.UtcNow;

        var state = await repo.UpsertScheduleAsync(
            GlobalJobTypes.Backup, enabled: true,
            hourLocal: 3, minuteLocal: 30, timezone: "America/New_York",
            configuredByUserId: ledger.UserId, nowUtc: now);

        Assert.True(state.Schedule.Enabled);
        Assert.Equal(3, state.Schedule.HourLocal);
        Assert.Equal(30, state.Schedule.MinuteLocal);
        Assert.Equal("America/New_York", state.Schedule.Timezone);
        Assert.NotNull(state.Schedule.NextRunAt);   // enabled → computed
        Assert.False(state.PassphraseConfigured);    // none set yet

        var reread = await repo.GetAsync(GlobalJobTypes.Backup);
        Assert.NotNull(reread);
        Assert.True(reread!.Schedule.Enabled);
        Assert.Equal(3, reread.Schedule.HourLocal);
    }

    [Fact]
    public async Task UpsertSchedule_disabled_clears_next_run_at()
    {
        await ResetBackupRowAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = NewRepo();

        await repo.UpsertScheduleAsync(
            GlobalJobTypes.Backup, true, 3, 0, null, ledger.UserId, DateTime.UtcNow);
        var disabled = await repo.UpsertScheduleAsync(
            GlobalJobTypes.Backup, false, 3, 0, null, ledger.UserId, DateTime.UtcNow);

        Assert.False(disabled.Schedule.Enabled);
        Assert.Null(disabled.Schedule.NextRunAt);
    }

    [Fact]
    public async Task SetPassphrase_stores_bytes_and_flags_configured()
    {
        await ResetBackupRowAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = NewRepo();
        var sealed_ = new byte[] { 1, 2, 3, 4, 5 };

        await repo.SetPassphraseCiphertextAsync(
            GlobalJobTypes.Backup, sealed_, ledger.UserId, DateTime.UtcNow);

        var state = await repo.GetAsync(GlobalJobTypes.Backup);
        Assert.NotNull(state);
        Assert.True(state!.PassphraseConfigured);
        // The ciphertext round-trips for the create/scheduler paths…
        Assert.Equal(sealed_, await repo.GetPassphraseCiphertextAsync(GlobalJobTypes.Backup));
        // …but the schedule defaults stay off until configured.
        Assert.False(state.Schedule.Enabled);
    }

    [Fact]
    public async Task Schedule_and_passphrase_are_written_independently()
    {
        await ResetBackupRowAsync();
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var repo = NewRepo();
        var now = DateTime.UtcNow;

        // Passphrase first, then a schedule — the schedule upsert must not
        // wipe the passphrase.
        await repo.SetPassphraseCiphertextAsync(
            GlobalJobTypes.Backup, new byte[] { 9, 9, 9 }, ledger.UserId, now);
        await repo.UpsertScheduleAsync(
            GlobalJobTypes.Backup, true, 4, 15, null, ledger.UserId, now);

        var state = await repo.GetAsync(GlobalJobTypes.Backup);
        Assert.NotNull(state);
        Assert.True(state!.PassphraseConfigured);      // survived the schedule upsert
        Assert.True(state.Schedule.Enabled);
        Assert.Equal(4, state.Schedule.HourLocal);

        // And a passphrase rotate must not wipe the schedule.
        await repo.SetPassphraseCiphertextAsync(
            GlobalJobTypes.Backup, new byte[] { 7, 7 }, ledger.UserId, now);
        var after = await repo.GetAsync(GlobalJobTypes.Backup);
        Assert.True(after!.Schedule.Enabled);
        Assert.Equal(4, after.Schedule.HourLocal);
        Assert.Equal(new byte[] { 7, 7 }, await repo.GetPassphraseCiphertextAsync(GlobalJobTypes.Backup));
    }

    /// <summary>Wipe the singleton backup row so each test owns its state.</summary>
    private async Task ResetBackupRowAsync()
    {
        await using var db = _fixture.NewDbContext();   // service role
        await db.Database.ExecuteSqlRawAsync("DELETE FROM global_scheduled_jobs;");
    }
}
