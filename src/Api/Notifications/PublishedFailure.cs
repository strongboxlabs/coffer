namespace Coffer.Api.Notifications;

/// <summary>
/// How a failure is allowed to be described in something that gets PUBLISHED.
/// </summary>
/// <remarks>
/// <para>
/// A published summary leaves the machine. It is delivered to whatever URL an
/// operator or ledger holder configured — a webhook, a Healthchecks endpoint —
/// and in-app it is readable by every grant holder, including a viewer. A raw
/// <c>NpgsqlException</c> or <c>HttpRequestException</c> message routinely names a
/// host, a port, a database, a file path or a full URL, so putting one in a
/// summary is an egress path for infrastructure detail that no gate checks.
/// </para>
/// <para>
/// Capping the length bounded how much escaped; it did not stop it escaping. This
/// type is the thing that stops it: callers get a KIND, never the text.
/// </para>
/// <para>
/// Nothing is lost to whoever is actually debugging. The exception is logged in
/// full, and a capped message still reaches <c>last_error</c> in the database.
/// Both stay on this side of the wire, which is the whole distinction.
/// </para>
/// </remarks>
public static class PublishedFailure
{
    /// <summary>
    /// What KIND of failure this was, in words, with nothing quoted from the
    /// exception.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. The point is to tell a reader whether this is theirs
    /// to fix — a bank refusing a request is acted on differently from a database
    /// error — without handing an outbound webhook the hostname it happened
    /// against. Anything unrecognised says so plainly rather than guessing,
    /// because a wrong category is worse than none.
    ///
    /// <para>Lifted out of <c>JobMonitorSignal</c>, where it was private, when the
    /// backup handler turned out to need the same treatment. Backup is a
    /// DEPLOYMENT-scope monitor, so it cannot go through <c>JobMonitorSignal.For</c>
    /// to reach this: that method answers null for anything not registered as a
    /// per-ledger job type, and routing backup through it would have silenced the
    /// alert rather than redacted it.</para>
    /// </remarks>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            Coffer.Api.Sync.SimpleFin.SimpleFinException =>
                "the provider refused the request or answered with something unusable",
            HttpRequestException or TaskCanceledException or TimeoutException =>
                "a network request failed or timed out",
            Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException =>
                "the database rejected the operation",
            UnauthorizedAccessException or IOException =>
                "a file could not be read or written",
            _ => "an unexpected error",
        } + ". The full message is in the server log.";
    }

    /// <summary>
    /// The exception's TYPE, as the <c>Detail</c> payload a triager gets. A class
    /// name carries no user or infrastructure data, so it is the useful half
    /// without the leak.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DetailFor(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new Dictionary<string, string> { ["error_type"] = ex.GetType().Name };
    }
}
