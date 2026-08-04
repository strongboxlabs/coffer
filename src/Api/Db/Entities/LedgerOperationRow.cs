namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>ledger_operations</c> (ADR-0055/0086; <c>sync_runs</c> mig 038
/// → <c>provider_runs</c> mig 132 → <c>ledger_operations</c> mig 185). One row per
/// operation on a ledger — a feed sync / file import, a Moneydance bootstrap import,
/// a quote refresh, or a snapshot restore — capturing the common metadata for the
/// unified per-ledger activity log.
/// </summary>
/// <remarks>
/// <para><see cref="Family"/> is <c>ingest</c> | <c>quote</c> | <c>snapshot</c>;
/// <see cref="ProviderKey"/> the concrete operation; <see cref="TriggeredVia"/>
/// how it started (manual / file-upload / post-sync / scheduled) and
/// <see cref="TriggeredByUserId"/> who — the real user, or the system user for
/// scheduled runs (ADR-0055 D2).</para>
///
/// <para>The provider-specific breakdown (counts/metadata) lives in
/// <see cref="DetailsJson"/> — a jsonb object whose shape varies per provider
/// (ingest: <c>txns_*</c>; quote: <c>prices_*</c> / <c>securities_unresolved</c>)
/// — so a new provider needs no schema change. The repository (de)serializes it
/// into typed per-family records.</para>
///
/// <para><see cref="Status"/> goes <c>running</c> → <c>completed</c> |
/// <c>partial</c> | <c>failed</c> | <c>needs_reauth</c>, written in one closing
/// UPDATE.</para>
/// </remarks>
internal sealed class LedgerOperationRow
{
    public Guid Id { get; init; }
    public Guid LedgerId { get; init; }

    /// <summary>Operation family: <c>ingest</c> | <c>quote</c> | <c>snapshot</c> (ADR-0055/0086).</summary>
    public required string Family { get; init; }
    /// <summary>Concrete operation: simplefin | ofx | qif | moneydance | yahoo | snapshot-restore | ….</summary>
    public required string ProviderKey { get; init; }
    /// <summary>How the run started: manual | file-upload | post-sync | scheduled.</summary>
    public required string TriggeredVia { get; init; }

    public Guid? FeedConnectionId { get; init; }
    public Guid? TriggeredByUserId { get; init; }

    /// <summary>Mutable — terminal status is written in the closing UPDATE.</summary>
    public string Status { get; set; } = "running";

    /// <summary>Top-level failure summary; per-error detail lives in
    /// <c>ledger_operation_errors</c>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Provider-specific counts/metadata as a jsonb object (ADR-0055).
    /// Mutable — written in the closing UPDATE. Default <c>{}</c>.</summary>
    public string DetailsJson { get; set; } = "{}";

    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
}
