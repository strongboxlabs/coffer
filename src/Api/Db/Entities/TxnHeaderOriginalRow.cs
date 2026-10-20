namespace Coffer.Api.Db.Entities;

/// <summary>
/// EF entity for <c>txn_header_originals</c> (migration 230) — the feed's
/// values for a header that has since been edited.
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
/// <para>A row exists only for an edited header, which is what makes it the
/// "has been modified" signal the register's <c>has_overrides</c> flag now
/// reports. It is also what payee recall needs: <c>GetSimilarPayeesAsync</c>
/// anchors on the BANK's payee and suggests the curated one, so the two have to
/// keep existing separately.</para>
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
    public string? Payee { get; init; }
    public string? Memo { get; init; }
    public DateTime? PostedAt { get; init; }
    public DateTime? TransactedAt { get; init; }
    public string? CheckNumber { get; init; }
    /// <summary>When the first edit captured this. Diagnostic only.</summary>
    public DateTime CapturedAt { get; init; }
}
