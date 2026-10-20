namespace Coffer.Api.Db.Entities;

/// <summary>
/// One deployment-scope notification (migration 207, ADR-0096 D1).
/// </summary>
/// <remarks>
/// Deliberately has no <c>LedgerId</c>. A system event is a fact about the
/// installation — "the backup failed" stays true after every ledger is deleted — and
/// its authorization question is "is this caller an admin?", not "does this caller
/// hold that ledger?". Per-ledger events live in <c>ledger_operations</c>.
/// </remarks>
internal sealed class SystemEventRow
{
    public Guid Id { get; init; }
    public DateTime OccurredAt { get; init; }
    public required string Severity { get; init; }
    public required string Topic { get; init; }
    public required string EventKey { get; init; }
    public required string Summary { get; init; }
    public string DetailJson { get; set; } = "{}";
}

/// <summary>
/// One configured delivery target (migration 207, ADR-0096 D7/D8).
/// </summary>
internal sealed class NotificationSubscriberRow
{
    public Guid Id { get; init; }
    public required string SubscriberKey { get; init; }
    public required string DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Inclusive floor: <c>warning</c> means warning and critical.</summary>
    public string MinSeverity { get; set; } = "warning";

    /// <summary>Null means every topic.</summary>
    public string[]? Topics { get; set; }

    /// <summary>
    /// The one monitor a heartbeat subscriber watches (mig 212); null for a message
    /// subscriber. One healthchecks URL is one check, so this is a binding, not a filter.
    /// </summary>
    public string? Monitors { get; set; }

    /// <summary>
    /// The provider's URL and optional token, sealed with the master key.
    /// </summary>
    /// <remarks>
    /// Sealed rather than plain because a webhook URL routinely embeds its own
    /// credential, and sealed HERE rather than in a docker secret because this is a
    /// runtime integration, not a bootstrap credential (ADR-0096 D8).
    /// </remarks>
    public required byte[] ConfigCiphertext { get; set; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }

    // Delivery health. Recorded so a target that silently stops working is visible;
    // otherwise the notification subsystem reproduces the silent failure it exists
    // to remove, one layer up.
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// One ledger-scope notification (migration 208, ADR-0096 D1).
/// </summary>
/// <remarks>
/// Distinct from <c>ledger_operations</c>, which is a RUN log — a drift notice is
/// not a run, it has no start, end or provider. Cascades with its ledger and is
/// gated by grant, which is exactly why it is not a nullable-ledger row on
/// <c>system_events</c>.
/// </remarks>
internal sealed class LedgerEventRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public DateTime OccurredAt { get; init; }
    public required string Severity { get; init; }
    public required string Topic { get; init; }
    public required string EventKey { get; init; }
    public required string Summary { get; init; }
    public string DetailJson { get; set; } = "{}";
}

/// <summary>
/// A ledger's own delivery target, used when its mode is <c>own</c> (mig 208).
/// </summary>
internal sealed class LedgerNotificationSubscriberRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }
    public required string SubscriberKey { get; init; }
    public required string DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string MinSeverity { get; set; } = "warning";
    public string[]? Topics { get; set; }

    /// <summary>
    /// The one monitor a heartbeat subscriber watches (mig 212); null for a message
    /// subscriber. One healthchecks URL is one check, so this is a binding, not a filter.
    /// </summary>
    public string? Monitors { get; set; }
    public required byte[] ConfigCiphertext { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastFailureAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
}
