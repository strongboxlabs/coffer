using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// Capture a header's ORIGINAL (feed) values before the first edit overwrites
/// them — migration 230's half of the flip.
/// </summary>
/// <remarks>
/// <para>The inverse shape — <c>txn_headers</c> holding the feed's values and
/// <c>txn_header_overrides</c> the user's, read back as
/// <c>COALESCE(o.x, h.x)</c> — has three problems: a CLEARED field is
/// unrepresentable (a nullable override column has no companion "is
/// overridden" marker, so NULL has to mean both "not overridden" and
/// "overridden to empty", and COALESCE always chooses the former); a write
/// path that writes canonical, underneath the override, is invisible to
/// COALESCE; and an override row that is never deleted makes that
/// disagreement permanent.</para>
///
/// <para>So the canonical row always holds the current values and the sidecar
/// holds the feed's. This helper is the ONE place either write path captures
/// that sidecar, which is what makes bank and investment genuinely the same
/// design rather than two similarly-shaped ones.</para>
///
/// <para>Capture is <i>unconditional on the field</i> — every header column the
/// sidecar carries is snapshotted, not just the ones this edit touches. A
/// per-field capture would need a per-field "was this captured" marker to tell
/// "the feed's payee was NULL" from "we never captured the payee", which is the
/// exact ambiguity the flip exists to remove.</para>
///
/// <para>Capture is <i>once per header</i>. A row already on file was written by
/// an earlier edit, or seeded by the Moneydance importer from the feed's own
/// strings (ADR-0100, 2026-10-21 amendment), and already holds the feed's
/// values; overwriting it with the
/// current ones would quietly redefine "original" as "whatever it was before the
/// most recent edit" and make a reset restore the wrong thing.</para>
/// </remarks>
internal static class HeaderOriginals
{
    /// <summary>
    /// Ensure <paramref name="header"/> has an originals row, snapshotting its
    /// values AS THEY ARE NOW. Call BEFORE mutating any of them, inside the same
    /// transaction as the edit — a crash between the two would leave a mutated
    /// row with no recorded original.
    /// </summary>
    /// <param name="memo">The transaction's memo as it is now, wherever it sits: a
    /// one-posting transaction keeps it on its originating side's leg, not on the
    /// header. Computed by <see cref="AnnotationWriter.CaptureOriginalsAsync"/>, which is
    /// how every edit path captures.</param>
    /// <param name="amount">The transaction's amount on its originating account, the sum
    /// of its legs there, as it is now (ADR-0114 D15): on a first edit the amount from
    /// before the edit, at an import that rounds the provider's figure before rounding.
    /// Null when the header has no leg on an originating account.</param>
    /// <param name="roundedAtImport">True only for an import that rounded
    /// <paramref name="amount"/> to its account currency's places; false for an edit.</param>
    public static async Task CaptureAsync(
        AppDbContext db,
        TxnHeaderRow header,
        string? memo,
        decimal? amount,
        bool roundedAtImport,
        CancellationToken cancellationToken)
    {
        // The local lookup matters: two edits inside one PatchAsync (header
        // fields plus the merge date stamp) both call this, and the first one's
        // row is still only in the change tracker.
        var tracked = db.TxnHeaderOriginals.Local
            .FirstOrDefault(o => o.HeaderId == header.Id);
        if (tracked is not null) return;

        var onFile = await db.TxnHeaderOriginals
            .AnyAsync(o => o.HeaderId == header.Id, cancellationToken)
            .ConfigureAwait(false);
        if (onFile) return;

        db.TxnHeaderOriginals.Add(new TxnHeaderOriginalRow
        {
            HeaderId = header.Id,
            LedgerId = header.LedgerId,
            Payee = header.Payee,
            Memo = memo,
            PostedAt = header.PostedAt,
            TransactedAt = header.TransactedAt,
            CheckNumber = header.CheckNumber,
            Amount = amount,
            RoundedAtImport = roundedAtImport,
        });
    }
}
