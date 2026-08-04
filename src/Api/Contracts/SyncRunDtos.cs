namespace Coffer.Api.Contracts;

/// <summary>
/// One run in the per-connection sync activity log (slice 2c.1,
/// migration 038). List-mode projection — counters + status +
/// timing, with `errorCount` / `promotionCount` as derived
/// summaries the SPA can show before expanding the detail view.
/// </summary>
/// <remarks>
/// <para><see cref="Status"/> values: <c>running</c>,
/// <c>completed</c>, <c>partial</c>, <c>failed</c>,
/// <c>needs_reauth</c>. CHECK enforced at the DB level.</para>
///
/// <para>The "for review" / "still pending" / "already known"
/// triple mirrors the live <see cref="SyncResultDto"/> the user
/// saw when this run completed, so the activity-log row reads
/// the same as the original toast.</para>
/// </remarks>
public sealed record SyncRunSummary(
    Guid Id,
    Guid? FeedConnectionId,
    string Status,
    int TxnsFetched,
    int TxnsInserted,
    int TxnsPromoted,
    int TxnsAlreadyKnown,
    int TxnsStillPending,
    string? ErrorMessage,
    DateTime StartedAt,
    DateTime? CompletedAt,
    Guid? TriggeredByUserId,
    int ErrorCount,
    int PromotionCount);

/// <summary>
/// Full detail for one run — summary + the persisted
/// <c>sync_run_errors</c> entries + <c>sync_run_promotions</c>
/// audit rows. Backs the expandable per-run panel on the SPA.
/// </summary>
public sealed record SyncRunDetail(
    SyncRunSummary Summary,
    IReadOnlyList<SyncErrorDto> Errors,
    IReadOnlyList<SyncRunPromotionDto> Promotions);

/// <summary>
/// One promote-on-clear event — the bank cleared a previously
/// pending charge at a different amount than the original hold
/// (restaurant tip, exchange-rate shift, FX rounding).
/// <see cref="HeaderId"/> points at the existing
/// <c>txn_headers</c> row; the SPA can deep-link to it.
/// </summary>
public sealed record SyncRunPromotionDto(
    Guid HeaderId,
    decimal WasAmount,
    decimal BecameAmount,
    DateTime PromotedAt);
