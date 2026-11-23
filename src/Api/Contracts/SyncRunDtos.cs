namespace Coffer.Api.Contracts;

/// <summary>
/// One run's summary as <see cref="SyncRunDetail"/> carries it (slice 2c.1,
/// migration 038): counters + status + timing + the failure message, with
/// `errorCount` / `promotionCount` as derived counts.
/// </summary>
/// <remarks>
/// <para><see cref="Status"/> values: <c>running</c>,
/// <c>completed</c>, <c>partial</c>, <c>failed</c>,
/// <c>needs_reauth</c>. CHECK enforced at the DB level.</para>
///
/// <para>The counters are read from the run's <c>details</c>; for a
/// run of a family other than ingest they are zero.</para>
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
/// audit rows. Backs the expandable row in Settings → Activity.
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
/// <param name="CurrencyCode">The currency <paramref name="WasAmount"/> and
/// <paramref name="BecameAmount"/> are in (ADR-0114 D15): the currency of the account the
/// row now lives on, its originating account. That is the account it was synced into
/// unless it was moved since, and a move relabels, so the figures read in the new
/// account's currency. Null for a row with no originating account.</param>
public sealed record SyncRunPromotionDto(
    Guid HeaderId,
    decimal WasAmount,
    decimal BecameAmount,
    DateTime PromotedAt,
    string? CurrencyCode = null);
