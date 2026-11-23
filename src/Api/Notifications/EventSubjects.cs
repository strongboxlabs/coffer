using Coffer.Api.Db.Entities;

namespace Coffer.Api.Notifications;

/// <summary>
/// What a notification event is ABOUT — the one definition of "the same thing",
/// shared by everything that decides whether a problem is still outstanding.
/// </summary>
/// <remarks>
/// <para>Event keys are <c>&lt;subject&gt;.&lt;outcome&gt;</c> —
/// <c>quote-refresh.failed</c>, <c>consistency.drift</c>, <c>backup.succeeded</c> — so
/// a later <c>info</c> event with the same subject means that subject is fine again.
/// That rule needs no <c>resolved_at</c> column and no correlation id, and it makes
/// every scheduled-job warning self-resolving for free, because the success signal each
/// job already publishes on its next good run IS the all-clear.</para>
///
/// <para>This lives in one place because several readers depend on it: the per-ledger
/// events panel, the cross-ledger attention summary behind the navigation indicators,
/// and the list of a ledger's outstanding problems (<see cref="OutstandingSubjects"/>).
/// Two copies of "the same subject" would be two answers to "does this ledger need
/// me", disagreeing on exactly the events that matter most.</para>
///
/// <para><b>The subject keeps its trailing dot.</b> <c>consistency.</c>, not
/// <c>consistency</c>. That looks like an oddity and is load-bearing: the summary has
/// to group by subject in SQL, and the only formulation Npgsql translates without a
/// runtime error on a key that has no dot is the prefix through the separator —
/// <c>IndexOf(".") + 1</c> yields 0 rather than -1, so Postgres gets
/// <c>substring(key, 1, 0)</c> = <c>''</c> instead of a negative length, which it
/// refuses outright. Dropping the dot afterwards would mean two spellings of the same
/// concept, which is the drift this type exists to prevent. Nothing displays a subject,
/// so the dot costs nothing.</para>
///
/// <para>No publisher emits a dotless key today — every one is a literal
/// <c>&lt;subject&gt;.&lt;outcome&gt;</c> or is built as <c>jobType + ".failed"</c> from
/// a checked monitor name. This is written to be total anyway, because the alternative
/// is an endpoint that throws on a row nobody predicted.</para>
/// </remarks>
internal static class EventSubjects
{
    /// <summary>
    /// The subject of <paramref name="eventKey"/>, including its trailing separator.
    /// </summary>
    /// <remarks>
    /// Must stay semantically identical to <see cref="WithSubject"/>, which the
    /// database evaluates over the same column. <c>EventSubjectTests</c> holds them to
    /// it.
    /// </remarks>
    internal static string Of(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return eventKey[..(eventKey.IndexOf('.', StringComparison.Ordinal) + 1)];
    }

    /// <summary>
    /// Each event with its subject, computed by the database:
    /// <c>substring(event_key, 1, strpos(event_key, '.'))</c>.
    /// </summary>
    /// <remarks>
    /// The one translatable spelling of <see cref="Of"/>. A projection over a named
    /// type rather than a helper called inside a query, because EF translates an
    /// expression tree and not a method body: a helper would either fail to translate
    /// or quietly evaluate on the client over every row. Every query that groups or
    /// filters by subject composes on this.
    /// </remarks>
    internal static IQueryable<SubjectedLedgerEvent> WithSubject(this IQueryable<LedgerEventRow> events) =>
        events.Select(e => new SubjectedLedgerEvent
        {
            LedgerId = e.LedgerId,
            Subject = e.EventKey.Substring(0, e.EventKey.IndexOf(".") + 1),
            Severity = e.Severity,
            OccurredAt = e.OccurredAt,
            Topic = e.Topic,
            EventKey = e.EventKey,
            Summary = e.Summary,
        });
}

/// <summary>
/// The deployment-scope counterpart of the ledger overload: the same projection over
/// <c>system_events</c> (ADR-0102, amended for ADR-0110). One definition of the subject, spelled
/// once for each table, because a query is built over exactly one entity type.
/// </summary>
internal static class SystemEventSubjects
{
    internal static IQueryable<SubjectedSystemEvent> WithSubject(this IQueryable<SystemEventRow> events) =>
        events.Select(e => new SubjectedSystemEvent
        {
            Subject = e.EventKey.Substring(0, e.EventKey.IndexOf(".") + 1),
            Severity = e.Severity,
            OccurredAt = e.OccurredAt,
        });
}

/// <summary>A system event with its subject (<see cref="SystemEventSubjects.WithSubject"/>).</summary>
internal sealed class SubjectedSystemEvent
{
    public string Subject { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
}

/// <summary>A ledger event with its subject (<see cref="EventSubjects.WithSubject"/>).</summary>
internal sealed class SubjectedLedgerEvent
{
    public Guid LedgerId { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string EventKey { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}
