using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db;

/// <summary>
/// A transaction merged into another (<c>is_merged_into</c> set) is a tombstone, and every
/// write a user aims at one transaction by its id treats it as absent.
/// </summary>
/// <remarks>
/// <para>No register, report or bulk action shows a merged-away row, and the merge itself
/// refuses to touch one. A write by id can still name it: a tab that still shows the row
/// from before it was matched elsewhere, or a connected app holding an id from before the
/// match. Such a write answers exactly as it answers an id that does not exist, with each
/// path's own not-found response; there is no wording or code of its own (decided by the
/// owner, 2026-11-18; ADR-0114 D15). The read an editor loads a transaction from answers
/// the same way.</para>
/// <para>Each by-id lookup reads its header through <see cref="NotMergedAway"/>, so a
/// merged-away row falls into that lookup's existing not-found branch. The merge routes
/// keep their own gate, which refuses a tombstone at either end (<c>merge-source-invalid</c>).
/// System writes are not by-id user writes and read the row as it is: the ingest's
/// promote-on-clear and matching, the reminder fire, the consistency repair, and the
/// delete of a winner, which un-merges its losers (<see cref="MergeLosers"/>). A fail-closed
/// catalog test holds every route and connected-app tool that names a transaction by id to
/// one of the two lists, guarded or exempt with a reason.</para>
/// </remarks>
internal static class MergedAway
{
    /// <summary>
    /// The headers a user-initiated write by id may target: every header except one merged
    /// into another.
    /// </summary>
    public static IQueryable<TxnHeaderRow> NotMergedAway(this IQueryable<TxnHeaderRow> headers) =>
        headers.Where(h => h.IsMergedInto == null);
}
