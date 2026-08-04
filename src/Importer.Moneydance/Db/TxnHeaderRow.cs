namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Persistable shape of one row in <c>txn_headers</c> (ADR-0022). One
/// row per Moneydance txn (or user-entered txn, etc.); carries the
/// event-level envelope — payee, memo, posted-at, status, check
/// number, plus the online-match status. The importer leaves the
/// cleared-transition audit columns (<c>cleared_at</c>,
/// <c>cleared_by_user_id</c>) NULL — they're populated by user action
/// in the SPA, not on import.
/// </summary>
/// <remarks>
/// <para>External id is the source-system's txn id alone (no leg
/// suffix); the partial unique index <c>(ledger_id, external_id)
/// WHERE external_id IS NOT NULL</c> drives idempotent re-imports
/// keyed at the event level rather than per-leg.</para>
///
/// <para><see cref="Status"/> is the normalized 3-state vocabulary
/// (<c>uncleared</c>, <c>reconciling</c>, <c>cleared</c>) enforced
/// by a CHECK constraint on the underlying table. The importer maps
/// Moneydance's raw letter codes (<c>c</c>, <c>C</c>, <c>x</c>,
/// <c>X</c>, <c>R</c>) to <c>cleared</c> and everything else to
/// <c>uncleared</c> — see <c>NormalizeMdStatus</c> in the mapper.</para>
/// </remarks>
public sealed record TxnHeaderRow(
    Guid Id,
    Guid LedgerId,
    string Origin,
    string? ExternalId,
    string? Payee,
    string? Memo,
    DateTimeOffset PostedAt,
    DateTimeOffset? TransactedAt,
    string Status,
    string? CheckNumber,
    bool IsPending,
    bool IsHidden,
    Guid? IsMergedInto,
    string? ImportSource,
    // Migration 030 audit pair. The DB CHECK enforces
    // (Status = "cleared") ⇔ (ClearedAt IS NOT NULL); mappers that
    // emit Status="cleared" must also stamp ClearedAt (typically the
    // header's posted_at — the best approximation of "when cleared"
    // we have for pre-import data). ClearedByUserId is left NULL
    // since the importer doesn't act as a real user.
    DateTimeOffset? ClearedAt,
    Guid? ClearedByUserId,
    // Migration 034: OFX online-match identity — the composite
    // (fi_id, fitid) is the OFX dedup key. The audit-only
    // ol.match-status / ol.match-type / ol.orig-txn fields were
    // dropped in mig 109 (ADR-0035 §4); the importer now persists
    // the full MD JSON per-row payload via ProviderRawPayload, so
    // those fields are recoverable from JSONB if ever needed.
    string? OnlineMatchFitid,
    string? OnlineMatchFiId,
    // Migration 047: investment-action label moved from per-leg to
    // per-header (one action per event, shared across all postings).
    // NULL on non-investment events. Investment values per ADR-0027
    // (enforced by migration 062's CHECK constraint):
    //   'buy', 'buyx', 'sell', 'sellx',
    //   'dividend_cash', 'dividend_reinvest', 'divx',
    //   'transfer', 'misc'.
    string? Action,
    // Mig 107: register provenance and audit signals.
    //   ProviderKey   — per-provider tag (mdplus / ofx / qif / csv).
    //                   NULL when Origin = 'manual'.
    //   IsMergeWinner — backfilled FALSE on import; the API merge
    //                   path flips to TRUE post-import.
    // Mig 109 / ADR-0035 §3: ProviderRawPayload — verbatim per-row
    // JSON from the source provider (MD txn item / SimpleFIN payload
    // / future OFX-CSV providers). Required for classification
    // backfills to be pure SQL — see DecomposeOrigin and the
    // reclassify CLI. Empty-string-as-NULL is acceptable; the column
    // is JSONB nullable.
    // Defaults so older test fixtures that construct TxnHeaderRow
    // positionally don't break.
    string? ProviderKey = null,
    bool IsMergeWinner = false,
    string? ProviderRawPayload = null,
    // ADR-0047 / migration 124: TRUE marks this header a recurring-reminder
    // TEMPLATE (never a live cash event). Reminders import their embedded txn
    // as a template header+legs; the live_txn_headers view + the recompute
    // exclude it. Trailing optional so existing positional constructions are
    // unaffected.
    bool IsRecurringTemplate = false);
