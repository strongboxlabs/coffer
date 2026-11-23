namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_originals</c> (migration 230) — the feed's
/// values for a header that has since been edited, or that was imported with
/// curated values the feed's differ from.
/// </summary>
/// <remarks>
/// <para>This is the flipped form of the old <c>txn_header_overrides</c>.
/// ADR-0003 kept the feed's values on <c>txn_headers</c> and the user's edit in
/// a sidecar, read back as <c>COALESCE(o.field, h.field)</c>. That made a
/// CLEARED field unrepresentable — a nullable override column has no companion
/// "is overridden" marker, so NULL has to mean both "not overridden" and
/// "overridden to empty", and COALESCE always resolves it as the former.</para>
///
/// <para>Now <c>txn_headers</c> always holds the CURRENT values and this table
/// holds the originals, captured once by the first edit that changes them. Reads
/// go straight to the header — no join, no COALESCE — and NULL means NULL.</para>
///
/// <para>A row exists for a header edited in Coffer, and for a Moneydance-imported
/// header whose feed payee or memo (<c>ol.orig-payee</c> / <c>ol.orig-memo</c>)
/// differs from the curated one, which the importer and migration 260 seed
/// (ADR-0100, 2026-10-21 amendment). So the view's <c>has_overrides</c> column (no
/// longer on the register response) means "the feed said something else", not "edited in Coffer". The row is also
/// what payee recall needs: <c>GetSimilarPayeesAsync</c> anchors on the BANK's
/// payee and suggests the curated one, so the two have to keep existing
/// separately.</para>
///
/// <para>Every column except the key is nullable because the feed itself may not
/// have supplied one — a manual row edited later captures nulls, and that is the
/// correct original.</para>
/// </remarks>
internal sealed class TxnHeaderOriginalRow
{
    public Guid HeaderId { get; init; }
    /// <summary>
    /// Denormalized from <c>txn_headers.ledger_id</c>, as the table it replaces
    /// carried it. The composite FK <c>(header_id, ledger_id)</c> enforces
    /// coherence and RLS gates on this column directly.
    /// </summary>
    public Guid LedgerId { get; init; }
    /// <summary>The feed's payee. Settable: the SimpleFIN pre-split backfill restates it
    /// on a row already on file while it still holds the value the backfill replaces
    /// (ADR-0114 D15).</summary>
    public string? Payee { get; set; }
    /// <summary>The feed's memo. Settable for the same backfill as
    /// <see cref="Payee"/>.</summary>
    public string? Memo { get; set; }
    public DateTime? PostedAt { get; init; }
    public DateTime? TransactedAt { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>When the first edit captured this, or for a Moneydance seed, when
    /// the header was imported. Diagnostic only.</summary>
    public DateTime CapturedAt { get; init; }

    /// <summary>
    /// The transaction's amount on its originating account, the sum of its legs there,
    /// as the feed gave it (migration 301, ADR-0114 D15): at an import that rounds, the
    /// provider's figure before rounding; at a first edit, the amount as it stood. In the
    /// row's stored sign, with no scale limit, so the bank's figure is kept exactly. Null
    /// on a row captured before migration 301.
    /// </summary>
    /// <remarks>Settable: a promote-on-clear restates the feed's amount and
    /// <see cref="RoundedAtImport"/> on a row already on file. With <see cref="Payee"/>
    /// and <see cref="Memo"/>, which the SimpleFIN pre-split backfill restates, these are
    /// the only columns written after capture (ADR-0114 D15); every other column is
    /// init-only.</remarks>
    public decimal? Amount { get; set; }

    /// <summary>
    /// True when an import or a promote-on-clear rounded <see cref="Amount"/> to its
    /// account currency's places, so the legs differ from it for that reason and not
    /// because of an edit (migration 301, ADR-0114 D15). With <c>needs_review</c> it shows
    /// the Review marker. False on a first edit's capture and on rows captured before
    /// migration 301.
    /// </summary>
    public bool RoundedAtImport { get; set; }
}
