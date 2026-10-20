using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// Capture a header's ORIGINAL (feed) values before the first edit overwrites
/// them — migration 230's half of the flip.
/// </summary>
/// <remarks>
/// <para>Before 230, <c>txn_headers</c> held the feed's values and
/// <c>txn_header_overrides</c> held the user's, read back as
/// <c>COALESCE(o.x, h.x)</c>. Three things were wrong with that: a CLEARED
/// field was unrepresentable (a nullable override column has no companion "is
/// overridden" marker, so NULL had to mean both "not overridden" and
/// "overridden to empty", and COALESCE always chose the former); the two write
/// paths disagreed (investment wrote canonical, underneath the override, where
/// COALESCE could not see it); and no code ever deleted an override row, so
/// that disagreement was permanent.</para>
///
/// <para>Now the canonical row always holds the current values and the sidecar
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
/// an earlier edit and already holds the feed's values; overwriting it with the
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
    public static async Task CaptureAsync(
        AppDbContext db,
        TxnHeaderRow header,
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
            Memo = header.Memo,
            PostedAt = header.PostedAt,
            TransactedAt = header.TransactedAt,
            CheckNumber = header.CheckNumber,
        });
    }
}
