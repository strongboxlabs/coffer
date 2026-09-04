namespace Coffer.Api.Contracts;

/// <summary>
/// A delivery provider this build can route to.
/// </summary>
/// <param name="DetectsAbsence">
/// Whether it can report that something did NOT happen. Only a heartbeat provider
/// can — noticing silence requires something outside the deployment to be counting,
/// and a dead container sends no chat message.
/// </param>
/// <param name="Monitors">
/// The jobs this provider can be BOUND to, empty for one that cannot detect absence.
/// Served as build metadata so a UI never restates the list: a healthchecks URL is one
/// check, so the picker has to offer the same set the API validates against, and two
/// copies of that set is one copy too many.
/// </param>
public sealed record NotificationProviderDto(
    string SubscriberKey,
    string DisplayName,
    bool DetectsAbsence,
    IReadOnlyList<string> Monitors);

/// <summary>
/// A configured delivery target. Carries no URL or token by design — the config is
/// sealed with the master key and never read back out (ADR-0096 D8).
/// </summary>
public sealed record NotificationSubscriberDto(
    Guid Id,
    string SubscriberKey,
    string DisplayName,
    bool IsEnabled,
    string MinSeverity,
    string[]? Topics,
    string? Monitors,
    DateTime? LastSuccessAt,
    DateTime? LastFailureAt,
    string? LastError,
    int ConsecutiveFailures);

/// <summary>
/// The configured targets, and which monitors have anyone watching for their silence.
/// </summary>
/// <param name="MonitorCoverage">
/// Every known monitor mapped to whether a dead-man's switch is bound to it. A false
/// entry means that job could stop happening and nothing outside this install would
/// notice — the failure that ran for 68 hours unnoticed.
/// <para>
/// A map, not the boolean this used to be. One healthchecks URL is one check, so
/// "some heartbeat target exists" was rendered as "absence detection is covered" while
/// a target bound to backups said nothing about any other job. The uncovered entries
/// are the ones worth showing.
/// </para>
/// </param>
public sealed record NotificationSubscribersResponse(
    IReadOnlyList<NotificationSubscriberDto> Subscribers,
    IReadOnlyDictionary<string, bool> MonitorCoverage);

/// <summary>Create a delivery target. The URL is sealed on the way in.</summary>
public sealed record CreateNotificationSubscriberRequest
{
    public required string SubscriberKey { get; init; }
    public string? DisplayName { get; init; }
    public required string Url { get; init; }
    public string? Token { get; init; }
    public string MinSeverity { get; init; } = "warning";
    public string[]? Topics { get; init; }

    /// <summary>
    /// For a heartbeat provider: the ONE monitor this URL watches. Required for that
    /// capability and rejected for a message one — a healthchecks URL identifies a
    /// single check, so leaving it unbound would mean a target that receives nothing.
    /// </summary>
    public string? Monitors { get; init; }
}

/// <summary>One recorded deployment-scope event.</summary>
/// <param name="ResolvedAt">
/// When a LATER event said this subject is fine again, or null while it is still a live
/// problem. Derived on read, not stored: the event log stays append-only, and "is this
/// still true?" is a question about the rows around a row rather than a property of it.
/// </param>
public sealed record SystemEventDto(
    Guid Id,
    DateTime OccurredAt,
    string Severity,
    string Topic,
    string EventKey,
    string Summary,
    DateTime? ResolvedAt = null);

/// <summary>
/// A ledger's notification settings: its own delivery targets.
/// </summary>
/// <remarks>
/// There is no mode. A ledger's events go to that ledger's targets, and nowhere else —
/// migration 214 retired the 'inherit' mode that sent them to the deployment's targets
/// instead. None configured means silence, which is why the panel has to say so.
/// </remarks>
/// <param name="MonitorCoverage">
/// This ledger's ENABLED scheduled jobs, and whether each has a dead-man's switch
/// watching it. Keyed on the ledger's own jobs rather than on every monitor the build
/// knows, so a ledger with snapshots switched off is not warned about snapshots.
/// </param>
/// <param name="MonitorsWatchingNothing">
/// Monitors this ledger has a switch bound to whose job is NOT enabled here. A check that
/// can only ever read "Never", which <see cref="MonitorCoverage"/> structurally cannot
/// report: it is keyed on the jobs that run, so a switch on a job that does not run
/// matches nothing and vanishes.
/// </param>
public sealed record LedgerNotificationSettingsDto(
    IReadOnlyList<NotificationSubscriberDto> Subscribers,
    IReadOnlyDictionary<string, bool> MonitorCoverage,
    IReadOnlyList<string> MonitorsWatchingNothing);
