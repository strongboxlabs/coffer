using Coffer.Api.Db.Entities;

namespace Coffer.Api.Ingest;

/// <summary>
/// Inputs an <see cref="IPullProvider"/> needs that aren't on the
/// <see cref="FeedConnectionRow"/> alone — the snapshot of mapped
/// Ledger accounts (with per-account sync watermarks) the provider
/// uses to compute its lookback window, plus the optional narrow-
/// to-one-account filter the slice 2c.3 per-account endpoint
/// passes.
/// </summary>
/// <remarks>
/// Why pass the watermark snapshot from the orchestrator instead
/// of letting the provider query <c>AppDbContext</c> directly:
/// keeps the provider boundary clean per ADR-0031 §1 (providers
/// translate; orchestrator owns DB). The provider still owns the
/// per-provider math of how to convert watermarks into a request
/// start-date (SimpleFIN sends one date for all accounts; OFX
/// would have per-account <c>DTSTART</c>) — it just doesn't load
/// the watermarks itself.
/// </remarks>
public sealed record PullContext(
    FeedConnectionRow Connection,
    /// <summary>The ledger's wrapped LEK (per ADR-0026). The
    /// provider uses this with <c>LedgerKeyService.Open</c> to
    /// unwrap its connection-specific secret material (SimpleFIN
    /// access URL, future Plaid item-access-token, etc.).</summary>
    byte[] LedgerWrappedLek,
    /// <summary>Snapshot of mapped Ledger accounts on this
    /// connection at run time. Providers read this for window /
    /// watermark math; the orchestrator uses it to dispatch
    /// transactions to <c>account_id</c>s after the provider
    /// returns.</summary>
    IReadOnlyList<MappedAccountWatermark> MappedAccounts,
    /// <summary>When non-null, narrows the provider's fetch to a
    /// single bank-side account id (slice 2c.3 per-account
    /// endpoint). Pull providers translate this to their wire-
    /// level filter; the orchestrator also uses it to defensively
    /// narrow its dispatch loop.</summary>
    string? AccountIdFilter);

/// <summary>
/// One row of the <see cref="PullContext.MappedAccounts"/>
/// snapshot. External id is the connection-side identifier; ledger
/// account id is the local binding; <c>LastSyncedAt</c> is the
/// per-account watermark the provider uses for incremental fetch
/// (NULL means "never synced — request the full window").
/// </summary>
public sealed record MappedAccountWatermark(
    Guid LedgerAccountId,
    string ExternalId,
    DateTime? LastSyncedAt);
