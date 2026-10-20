namespace Coffer.Api.Contracts;

/// <summary>
/// Request body for
/// <c>POST /api/ledgers/{ledgerId}/feed-connections</c>. The user
/// pastes a one-shot setup token they generated at
/// <c>simplefin.org/setup</c>; the server exchanges it for the
/// long-lived access URL, seals the URL under the ledger's LEK
/// (ADR-0026), and persists. The setup token never re-appears
/// after this call and the plaintext access URL never crosses the
/// wire boundary outbound.
/// </summary>
public sealed class CreateFeedConnectionRequest
{
    /// <summary>Base64url-encoded URL string from
    /// <c>simplefin.org/setup</c>. SimpleFIN invalidates it on first
    /// successful claim.</summary>
    public string SetupToken { get; init; } = string.Empty;
}

/// <summary>
/// Public projection of a feed-connection row. Carries the audit-
/// safe fields only — the sealed access URL stays server-side.
/// </summary>
public sealed record FeedConnectionSummary(
    Guid Id,
    Guid LedgerId,
    string Provider,
    /// <summary>Display name for the FI. Null until the first
    /// sync (or the connect-time probe) populates it; the SPA
    /// renders "SimpleFIN" as the fallback.</summary>
    string? InstitutionName,
    /// <summary>One of <c>active</c>, <c>needs_reauth</c>,
    /// <c>error</c>, <c>disconnected</c> (CHECK enforced at the
    /// DB level).</summary>
    string Status,
    DateTime? LastSyncedAt,
    DateTime CreatedAt);

/// <summary>
/// Response body for
/// <c>POST /api/ledgers/{ledgerId}/feed-connections/{connectionId}/sync</c>.
/// One run summary: how many accounts the feed knows about, how
/// many transactions landed in pending review, how many were
/// already known (FITID-matched against an MD-imported row), and
/// which SimpleFIN accounts haven't been bound to a Coffer account
/// yet (the SPA prompts the user to map these).
///
/// <para><see cref="ConnectionStatus"/> reflects the post-sync
/// <c>feed_connections.status</c> — normally <c>active</c>; flips
/// to <c>needs_reauth</c> when SimpleFIN returned 403 so the SPA
/// can render a "Re-connect" call-to-action instead of a generic
/// error toast (defensive-API posture, SimpleFIN v2.0.0).</para>
///
/// <para><see cref="Errors"/> mirrors the SimpleFIN v2
/// <c>errlist[]</c> — non-fatal per-connection / per-account
/// messages surfaced verbatim. Empty list on a clean run.</para>
/// </summary>
public sealed record SyncResultDto(
    int AccountsDiscovered,
    /// <summary>Bank-posted rows the sync just landed in
    /// <c>txn_headers</c> with <c>needs_review=true</c> — plus any
    /// previously-pending FITIDs the sync promoted on this run.
    /// The SPA's daily "X for review" copy reads this.</summary>
    int TransactionsForReview,
    /// <summary>Bank-pending rows (SimpleFIN <c>pending: true</c>)
    /// the sync landed in <c>txn_headers</c> with
    /// <c>is_pending=true, needs_review=true</c>. Not yet cleared
    /// by the bank — will be flipped to <c>is_pending=false</c>
    /// in place when a future sync sees the same FITID with
    /// <c>pending: false</c>.</summary>
    int TransactionsStillPending,
    int AlreadyKnown,
    string ConnectionStatus,
    IReadOnlyList<SyncErrorDto> Errors);

/// <summary>
/// One SimpleFIN v2 <c>errlist[]</c> entry. <see cref="Code"/> is
/// the structured <c>prefix.subcode</c> the SPA can switch on
/// (e.g. <c>auth.revoked</c>); <see cref="Message"/> is the human
/// message. Optional scope ids when SimpleFIN attributed the error
/// to one connection or account on the feed.
/// </summary>
public sealed record SyncErrorDto(
    string Code,
    string Message,
    string? SimpleFinConnectionId,
    string? SimpleFinAccountId);

/// <summary>
/// Response body for <c>POST /api/ledgers/{ledgerId}/sync-all</c>
/// (slice 2c.3). One entry per active feed connection on the
/// ledger, plus a derived <see cref="HadAnyFailure"/> flag the SPA
/// uses to decide whether to surface a partial-failure banner.
/// </summary>
public sealed record SyncAllResultDto(
    IReadOnlyList<SyncAllConnectionEntry> Connections,
    bool HadAnyFailure);

/// <summary>
/// One connection's outcome inside a sync-all aggregate. Exactly
/// one of <see cref="Result"/> / <see cref="FailureCode"/> is
/// non-null:
/// <list type="bullet">
///   <item><description><see cref="Result"/> non-null when the sync
///   completed (even if `connectionStatus = needs_reauth` or
///   `errors[]` is non-empty — those are normal sync outcomes).</description></item>
///   <item><description><see cref="FailureCode"/> non-null when the
///   sync was rejected pre-flight (lock held, access URL missing
///   / corrupted, etc.) — the SPA renders the code as a short
///   "skipped" reason.</description></item>
/// </list>
/// </summary>
public sealed record SyncAllConnectionEntry(
    Guid ConnectionId,
    SyncResultDto? Result,
    string? FailureCode);

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{ledgerId}/accounts/{accountId}/feed-mapping</c>.
/// Binds a Coffer account to one SimpleFIN account on a connection
/// so future syncs route that SimpleFIN account's transactions
/// here. Idempotent: re-mapping the same pair is a no-op.
/// </summary>
public sealed class PatchAccountFeedMappingRequest
{
    public Guid FeedConnectionId { get; init; }
    public string SimpleFinAccountId { get; init; } = string.Empty;
}

/// <summary>
/// Request body for
/// <c>PATCH /api/ledgers/{lid}/accounts/{aid}/sync-from-date</c>
/// (slice 2c.5). Sets <c>accounts.last_simplefin_sync_at</c> so the
/// next sync against this account asks SimpleFIN for transactions
/// from <see cref="SyncFromDate"/> forward (with the same 7-day
/// overlap the auto-watermark path uses).
///
/// <para>Null <see cref="SyncFromDate"/> clears the watermark — the
/// next sync asks for the full 90-day window. Useful as a
/// "backfill more history" affordance.</para>
/// </summary>
public sealed class PatchAccountSyncFromDateRequest
{
    /// <summary>User-supplied watermark, or null to reset. Must not
    /// be in the future — the endpoint returns 422
    /// <c>sync-from-date-in-future</c> otherwise.</summary>
    public DateTime? SyncFromDate { get; init; }
}

/// <summary>
/// One row of the per-connection accounts list (slice 2c.4).
/// Backs <c>GET /api/ledgers/{ledgerId}/feed-connections/{cid}/accounts</c>.
/// Renders as the unified mapped+unmapped accounts panel under
/// each connection on the bank-feeds page; the
/// <see cref="BoundLedgerAccountId"/> discriminates between
/// mapped (non-null) and unmapped (null) rows.
/// </summary>
public sealed record FeedConnectionAccountDto(
    string SimpleFinAccountId,
    string Name,
    string? OrgName,
    string? Currency,
    decimal? Balance,
    DateTime LastSeenAt,
    /// <summary>The Coffer account currently bound to this
    /// SimpleFIN account on this connection, or null when
    /// unmapped.</summary>
    Guid? BoundLedgerAccountId,
    string? BoundLedgerAccountName,
    /// <summary>Per-account sync watermark on the bound Coffer
    /// account (slice 2c.5). The next sync asks SimpleFIN for
    /// transactions from <c>(this − 7d)</c> forward for this
    /// account; null = "no successful sync yet, full 90-day
    /// window next time." Null when unmapped.</summary>
    DateTime? BoundLedgerAccountSyncFrom);
