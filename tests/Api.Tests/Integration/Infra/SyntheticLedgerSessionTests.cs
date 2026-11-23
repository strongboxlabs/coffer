using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// A seeded session and a seeded passkey are stamped by the clock production stamps them
/// with, the app's. The step-up rule compares a session's start with when each passkey was
/// added, and both come from the app clock in production. The database's clock is another
/// clock, which can lag the app's by more than the time between two writes, so a session
/// stamped by the column default can start before a passkey added just ahead of it, and the
/// rule then reads that passkey as one added during the session.
/// </summary>
/// <remarks>
/// The ledger is given a clock that stands still far from the real time, so a stamp from
/// any other clock fails here whether or not the database's clock agrees with the test
/// process's.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SyntheticLedgerSessionTests : DatabaseTest
{
    // timestamptz keeps microseconds, so the stored stamp is compared with the clock at that scale.
    private static readonly DateTimeOffset Instant =
        new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1_234_567);

    private static readonly DateTime InstantAtMicroseconds =
        new(Instant.UtcTicks - Instant.UtcTicks % 10, DateTimeKind.Utc);

    public SyntheticLedgerSessionTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_seeded_session_starts_by_the_ledgers_clock()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        ledger.Clock = new StoppedClock(Instant);

        await ledger.IssueSessionCookieAsync();

        await using var db = Db.NewDbContext();
        var started = (await db.AuthSessions.AsNoTracking()
            .Where(s => s.UserId == ledger.UserId)
            .Select(s => s.CreatedAt)
            .SingleAsync())
            .ToUniversalTime();
        Assert.True(started == InstantAtMicroseconds,
            $"The session starts at {started:O}, not at the ledger clock's {InstantAtMicroseconds:O}: "
            + "it was stamped by another clock.");
    }

    [Fact]
    public async Task A_seeded_passkey_is_added_by_the_ledgers_clock()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        ledger.Clock = new StoppedClock(Instant);

        var credential = await ledger.AddCredentialAsync();

        await using var db = Db.NewDbContext();
        var added = (await db.WebAuthnCredentials.AsNoTracking()
            .Where(c => c.Id == credential.Id)
            .Select(c => c.CreatedAt)
            .SingleAsync())
            .ToUniversalTime();
        Assert.True(added == InstantAtMicroseconds,
            $"The passkey was added at {added:O}, not at the ledger clock's {InstantAtMicroseconds:O}: "
            + "it was stamped by another clock.");
    }

    private sealed class StoppedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
