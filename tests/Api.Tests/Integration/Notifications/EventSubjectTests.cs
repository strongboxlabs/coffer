using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The subject rule has two spellings, one in C# and one the database evaluates. This
/// keeps them the same rule.
/// </summary>
/// <remarks>
/// <para>The per-ledger events panel resolves each problem in memory with
/// <see cref="EventSubjects.Of"/>. The attention summary and the outstanding list group
/// in SQL, because they cannot afford to pull the log, through
/// <see cref="EventSubjects.WithSubject"/>: <c>Of</c> is a method body EF cannot
/// translate.</para>
///
/// <para>The risk is that the two drift on exactly the keys that matter. This runs both
/// over the same keys, against a real Postgres, and fails if they ever disagree.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class EventSubjectTests : DatabaseTest
{
    public EventSubjectTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    /// <summary>
    /// Every key any publisher in this build emits, plus the shapes that break naive
    /// implementations.
    /// </summary>
    /// <remarks>
    /// The dotless entry is the one that earns its place: <c>IndexOf(".")</c> is -1
    /// there, and the obvious <c>Substring(0, IndexOf("."))</c> becomes
    /// <c>substring(key, 1, -1)</c>, which Postgres refuses outright. No publisher
    /// emits one today — every key is a literal <c>&lt;subject&gt;.&lt;outcome&gt;</c>
    /// or is built as <c>jobType + ".failed"</c> — and an endpoint that throws on a row
    /// nobody predicted is still the wrong thing to ship.
    /// </remarks>
    private static readonly string[] Keys =
    [
        "backup.failed",
        "backup.none",
        "backup.stale",
        "backup.succeeded",
        "consistency.drift",
        "consistency.ok",
        "consistency.repaired",
        "feed-sync.disabled",
        "quote-refresh.failed",
        "quote-refresh.succeeded",
        "snapshot.failed",
        "reminder-backlog.deferred",
        "reminder-backlog.clear",
        "dotless",
        "trailing.",
        "two.dots.here",
    ];

    [Fact]
    public async Task The_database_and_CSharp_agree_on_every_subject()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        await using var db = Db.NewDbContext();
        foreach (var key in Keys)
        {
            db.LedgerEvents.Add(new LedgerEventRow
            {
                LedgerId = ledger.LedgerId,
                Severity = NotificationSeverity.Warning,
                Topic = NotificationTopics.Scheduler,
                EventKey = key,
                Summary = key,
                DetailJson = "{}",
            });
        }

        await db.SaveChangesAsync();

        // The projection every subject query composes on. Projected, not grouped, so a
        // disagreement names the key it happened on.
        var fromDatabase = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId)
            .WithSubject()
            .Select(e => new { e.EventKey, e.Subject })
            .ToListAsync();

        Assert.Equal(Keys.Length, fromDatabase.Count);
        foreach (var row in fromDatabase)
        {
            Assert.Equal(EventSubjects.Of(row.EventKey), row.Subject);
        }
    }

    [Fact]
    public void A_subject_keeps_its_separator_and_never_throws()
    {
        // The trailing dot is the whole reason the SQL form is safe, so it is asserted
        // rather than left as a property of the implementation. Anyone "tidying" it
        // away reintroduces substring(key, 1, -1) on the first dotless row.
        Assert.Equal("consistency.", EventSubjects.Of("consistency.drift"));
        Assert.Equal("consistency.", EventSubjects.Of("consistency.ok"));
        Assert.Equal("quote-refresh.", EventSubjects.Of("quote-refresh.failed"));

        // Two outcomes of one subject collapse together — the property everything
        // downstream is actually built on.
        Assert.Equal(EventSubjects.Of("backup.failed"), EventSubjects.Of("backup.succeeded"));

        // Different subjects stay apart, including one that is a prefix of another.
        Assert.NotEqual(EventSubjects.Of("backup.failed"), EventSubjects.Of("backup-age.failed"));

        // Degenerate shapes resolve to something rather than throwing.
        Assert.Equal("", EventSubjects.Of("dotless"));
        Assert.Equal("trailing.", EventSubjects.Of("trailing."));
        Assert.Equal("two.", EventSubjects.Of("two.dots.here"));
    }
}
