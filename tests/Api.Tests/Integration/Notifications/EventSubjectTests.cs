using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Notifications;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Notifications;

/// <summary>
/// The subject rule is written twice — once in C#, once as a LINQ expression the
/// database evaluates. This is what keeps them the same rule.
/// </summary>
/// <remarks>
/// <para>Two readers depend on "the same subject": the per-ledger events panel resolves
/// each problem in memory, and the cross-ledger attention summary groups in SQL because
/// it cannot afford to pull the log. Neither could use the other's spelling —
/// <see cref="EventSubjects.Of"/> is a method body EF cannot translate, and the query's
/// expression is not something C# should be reimplementing at every call site.</para>
///
/// <para>So there are two, and the risk is that they drift on exactly the keys that
/// matter. A comment asking the next person to keep them in step is not a mechanism.
/// This runs both over the same keys, against a real Postgres, and fails if they ever
/// disagree.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class EventSubjectTests
{
    private readonly PostgresFixture _fixture;

    public EventSubjectTests(PostgresFixture fixture) => _fixture = fixture;

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
        "scheduler.job-disabled",
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
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewDbContext();
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

        // Exactly the expression LedgerNotificationsEndpoints.ListAttentionAsync groups
        // by. Projected, not grouped, so a disagreement names the key it happened on.
        var fromDatabase = await db.LedgerEvents.AsNoTracking()
            .Where(e => e.LedgerId == ledger.LedgerId)
            .Select(e => new
            {
                e.EventKey,
                Subject = e.EventKey.Substring(0, e.EventKey.IndexOf(".") + 1),
            })
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
