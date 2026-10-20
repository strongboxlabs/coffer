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
/// <para>This lives in one place because two readers now depend on it: the per-ledger
/// events panel, and the cross-ledger attention summary behind the navigation
/// indicators. Two copies of "the same subject" would be two answers to "does this
/// ledger need me", disagreeing on exactly the events that matter most.</para>
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
    /// Must stay semantically identical to the expression in
    /// <c>LedgerNotificationsEndpoints.SubjectInSql</c>, which the database evaluates
    /// over the same column. <c>EventSubjectTests</c> holds them to it.
    /// </remarks>
    internal static string Of(string eventKey)
    {
        ArgumentNullException.ThrowIfNull(eventKey);
        return eventKey[..(eventKey.IndexOf('.', StringComparison.Ordinal) + 1)];
    }
}
