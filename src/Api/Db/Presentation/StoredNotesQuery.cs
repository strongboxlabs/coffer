using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Presentation;

/// <summary>
/// The register, report and listing legs with the memo-and-tags presentation read from
/// what writers stored (<see cref="LegNotes"/>: the <c>txn_legs.notes_*</c> columns,
/// exposed by <c>resolved_transactions</c> for the row's leg and the posting's other
/// leg). Every reader of the presentation reads it here; none evaluates the rule.
/// </summary>
/// <remarks>
/// <para>Nothing here decides what a leg shows: every value is a column the C# rule
/// wrote. A reader's only choice is which leg's presentation it reads, the perspective:
/// the row's own, or the posting's other leg for a report or a listing line read from
/// its real account. Both are columns of the view row, so no perspective costs a join;
/// the tags of the chosen leg are matched by that leg's own key, never by a key
/// computed per row.</para>
/// <para>A leg whose columns are still null is a writer that skipped the recompute, and
/// the readers do not agree on what that does: a reader that materializes the
/// presentation (the register's rows, the listing, the reminders) fails; one that only
/// filters, groups or counts on it reads it wrong without an error: the entry key falls
/// back to the leg (a split shows ungrouped), a tag filter, the rail and the counts
/// leave the leg out, and a tag report counts it as untagged. The writers' guards keep
/// such a leg from existing, and the stored-equals-computed check names one.</para>
/// </remarks>
internal static class StoredNotesQuery
{
    /// <summary>Every leg of every live header, each with its own stored presentation and entry key.</summary>
    internal static IQueryable<RegisterLeg> RegisterLegs(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.ResolvedTransactions.Select(rt => new RegisterLeg
        {
            Row = rt,
            NotesLegId = rt.Id,
            Shown = new LegPresentation
            {
                Kind = rt.NotesKind!.Value,
                IsReadOnly = rt.NotesIsReadOnly!.Value,
                GroupsByHeader = rt.NotesGroupsByHeader!.Value,
                Memo = rt.NotesMemo,
                MemoInherited = rt.NotesMemoInherited!.Value,
                InheritedMemo = rt.NotesInheritedMemo,
                InheritFromAccountId = rt.NotesInheritFromAccountId,
                TagsInherited = rt.NotesTagsInherited!.Value,
                InPlayHeaderMemo = rt.NotesHeaderMemo,
                InPlayHeaderTagHeaderId = rt.NotesHeaderInPlay == true ? rt.HeaderId : null,
            },
            EntryKey = rt.NotesGroupsByHeader == true ? rt.HeaderId : rt.Id,
        });
    }

    /// <summary>
    /// Every leg of every live header, presented from a report's perspective:
    /// the real account the posting pairs it with, as that account's register shows the
    /// posting, so a category-side override never counts. A category leg paired with
    /// another category reads its own, or the other one when
    /// <paramref name="groupedOnCounterparty"/> (a report grouped by the account each
    /// posting pairs with); a leg paired with nothing reads its own.
    /// </summary>
    internal static IQueryable<ReportLeg> ReportLegs(AppDbContext db, bool groupedOnCounterparty = false) =>
        FromPerspective(db, ReportFromCounterparty(groupedOnCounterparty));

    /// <summary>Where <see cref="ReportLegs"/> reads the posting's other leg.</summary>
    internal static Expression<Func<ResolvedTransactionView, bool>> ReportFromCounterparty(bool groupedOnCounterparty) =>
        groupedOnCounterparty
            ? rt => rt.CounterpartyAccountId != null
            : rt => rt.CounterpartyAccountId != null && rt.CounterpartyAccountType != "category";

    /// <summary>
    /// Every leg of every live header, presented as the transaction listing shows its
    /// line: from the leg in scope (<see cref="ListingScope.Account"/>: the
    /// account's own; <see cref="ListingScope.Counterparty"/>: the category or account a
    /// money-side leg pairs with); with nothing in scope, a line between two real
    /// accounts from the posting's outflow leg, and for a zero amount from the leg on
    /// the side the header belongs to (the stored
    /// <see cref="LegPresentation.GroupsByHeader"/> of the posting's other leg), so both
    /// lines of a transfer show the same notes.
    /// </summary>
    internal static IQueryable<ReportLeg> ListingLegs(AppDbContext db, ListingScope scope) =>
        FromPerspective(db, ListingFromCounterparty(scope));

    /// <summary>Where <see cref="ListingLegs"/> reads the posting's other leg.</summary>
    internal static Expression<Func<ResolvedTransactionView, bool>> ListingFromCounterparty(ListingScope scope) =>
        scope switch
        {
            ListingScope.Account => rt => false,
            ListingScope.Counterparty => rt => rt.CounterpartyAccountId != null,
            _ => rt => rt.CounterpartyAccountId != null && rt.CounterpartyAccountType != "category"
                       && (rt.Amount > 0m || (rt.Amount == 0m && rt.CounterpartyNotesGroupsByHeader == true)),
        };

    /// <summary>
    /// Every view row presented from its own leg, or where <paramref name="fromCounterparty"/>
    /// holds, from the posting's other leg: each value is the one column or the other.
    /// </summary>
    private static IQueryable<ReportLeg> FromPerspective(
        AppDbContext db, Expression<Func<ResolvedTransactionView, bool>> fromCounterparty)
    {
        ArgumentNullException.ThrowIfNull(db);
        Expression<Func<ResolvedTransactionView, ReportLeg>> read = rt => new ReportLeg
        {
            Row = rt,
            NotesLegId = fromCounterparty.Inline(rt) ? rt.CounterpartyId : rt.Id,
            Shown = new LegPresentation
            {
                Kind = (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesKind : rt.NotesKind)!.Value,
                IsReadOnly = (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesIsReadOnly : rt.NotesIsReadOnly)!.Value,
                GroupsByHeader = (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesGroupsByHeader : rt.NotesGroupsByHeader)!.Value,
                Memo = fromCounterparty.Inline(rt) ? rt.CounterpartyNotesMemo : rt.NotesMemo,
                MemoInherited = (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesMemoInherited : rt.NotesMemoInherited)!.Value,
                InheritedMemo = fromCounterparty.Inline(rt) ? rt.CounterpartyNotesInheritedMemo : rt.NotesInheritedMemo,
                InheritFromAccountId = fromCounterparty.Inline(rt)
                    ? rt.CounterpartyNotesInheritFromAccountId : rt.NotesInheritFromAccountId,
                TagsInherited = (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesTagsInherited : rt.NotesTagsInherited)!.Value,
                InPlayHeaderMemo = fromCounterparty.Inline(rt) ? rt.CounterpartyNotesHeaderMemo : rt.NotesHeaderMemo,
                InPlayHeaderTagHeaderId =
                    (fromCounterparty.Inline(rt) ? rt.CounterpartyNotesHeaderInPlay : rt.NotesHeaderInPlay) == true
                        ? rt.HeaderId : null,
            },
        };
        return db.ResolvedTransactions.Select(read.Expand());
    }

    /// <summary>
    /// Every view row column except the stored presentation's, which a reader reads
    /// as the presentation (<see cref="RegisterLegs"/>), so a page's rows never carry
    /// them twice.
    /// </summary>
    internal static readonly Expression<Func<ResolvedTransactionView, ResolvedTransactionView>> WithoutNotes = r =>
        new ResolvedTransactionView
        {
            Id = r.Id,
            AccountId = r.AccountId,
            Payee = r.Payee,
            Amount = r.Amount,
            PostedAt = r.PostedAt,
            TransactedAt = r.TransactedAt,
            Status = r.Status,
            IsHidden = r.IsHidden,
            BalanceAfter = r.BalanceAfter,
            Origin = r.Origin,
            IsPending = r.IsPending,
            IsMergedInto = r.IsMergedInto,
            InvestmentAction = r.InvestmentAction,
            ExternalId = r.ExternalId,
            CreatedAt = r.CreatedAt,
            CheckNumber = r.CheckNumber,
            CounterpartyId = r.CounterpartyId,
            TxnGroupId = r.TxnGroupId,
            LegIndex = r.LegIndex,
            CounterpartyAccountId = r.CounterpartyAccountId,
            CounterpartyAccountName = r.CounterpartyAccountName,
            CounterpartyAccountType = r.CounterpartyAccountType,
            HeaderId = r.HeaderId,
            ClearedAt = r.ClearedAt,
            ClearedByUserId = r.ClearedByUserId,
            OnlineMatchFitid = r.OnlineMatchFitid,
            OnlineMatchFiId = r.OnlineMatchFiId,
            NeedsReview = r.NeedsReview,
            SecurityId = r.SecurityId,
            SecurityTicker = r.SecurityTicker,
            SecurityName = r.SecurityName,
            Quantity = r.Quantity,
            UnitPrice = r.UnitPrice,
            PostingRole = r.PostingRole,
            IngestActionHint = r.IngestActionHint,
            IngestSecurityId = r.IngestSecurityId,
            IngestShares = r.IngestShares,
            IngestUnitPrice = r.IngestUnitPrice,
            IngestFee = r.IngestFee,
            IngestAmount = r.IngestAmount,
            IngestSecurityTickerHint = r.IngestSecurityTickerHint,
            ProviderRawPayload = r.ProviderRawPayload,
            HeaderSeq = r.HeaderSeq,
            HeaderAccountNetAmount = r.HeaderAccountNetAmount,
            ProviderKey = r.ProviderKey,
            IsMergeWinner = r.IsMergeWinner,
            ImportSource = r.ImportSource,
            DerivedAction = r.DerivedAction,
            AccountPostingsOnHeader = r.AccountPostingsOnHeader,
            HeaderTotalPostings = r.HeaderTotalPostings,
            AccountType = r.AccountType,
            OriginalAmount = r.OriginalAmount,
            AmountRoundedAtImport = r.AmountRoundedAtImport,
            CounterpartyAmount = r.CounterpartyAmount,
            CounterpartyCurrencyCode = r.CounterpartyCurrencyCode,
            ReviewClearedAmount = r.ReviewClearedAmount,
            ReviewClearedByMatch = r.ReviewClearedByMatch,
        };

    /// <summary>The stored tags of a leg's presentation that play the role.</summary>
    internal static readonly Expression<Func<TxnLegNoteTagRow, bool>> Shown = t => t.Shown;

    /// <inheritdoc cref="Shown"/>
    internal static readonly Expression<Func<TxnLegNoteTagRow, bool>> InPlay = t => t.InPlay;

    /// <inheritdoc cref="Shown"/>
    internal static readonly Expression<Func<TxnLegNoteTagRow, bool>> Inherited = t => t.Inherited;

    /// <inheritdoc cref="Shown"/>
    internal static readonly Expression<Func<TxnLegNoteTagRow, bool>> TransactionLevel = t => t.TransactionLevel;

    /// <summary>The tags a leg's stored presentation names in <paramref name="role"/>, each once.</summary>
    internal static Expression<Func<Guid, IQueryable<TagRow>>> TagsOf(
        AppDbContext db, Expression<Func<TxnLegNoteTagRow, bool>> role)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(role);
        Expression<Func<Guid, IQueryable<TagRow>>> tags = legId => db.TxnLegNoteTags
            .Where(t => t.LegId == legId)
            .Where(role)
            .Join(db.Tags, t => t.TagId, g => g.Id, (t, g) => g);
        return tags;
    }

    /// <summary>
    /// A register leg whose in-play tags include one matching <paramref name="tag"/>:
    /// what a tag filter and a text search over tag names match.
    /// </summary>
    internal static Expression<Func<RegisterLeg, bool>> HasInPlayTag(
        AppDbContext db, Expression<Func<TagRow, bool>> tag)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tag);
        Expression<Func<RegisterLeg, bool>> matches = l => db.TxnLegNoteTags
            .Where(t => t.LegId == l.NotesLegId && t.InPlay)
            .Join(db.Tags, t => t.TagId, g => g.Id, (t, g) => g)
            .Any(tag);
        return matches;
    }

    /// <summary>
    /// A view row whose presentation, from the leg <paramref name="fromCounterparty"/>
    /// picks, has <paramref name="tagId"/> in play: a report's or a listing's tag filter.
    /// </summary>
    /// <remarks>
    /// <para>Two conditions, so the tag drives the read. The first keeps the rows of
    /// <paramref name="candidateHeaders"/>, read beforehand by
    /// <see cref="InPlayTagHeaders"/>: a list of keys, which the database applies to the
    /// headers before any leg is joined, so only those headers' legs are joined to their
    /// other leg. It holds for every row that matches, since the row's leg and its other
    /// leg are both legs of the row's header and each tag row carries its leg's header.
    /// The second is the exact test on what is left: the row's leg or its other leg,
    /// whichever <paramref name="fromCounterparty"/> picks, has the tag in play, each
    /// matched on that leg's own key.</para>
    /// <para>The headers are a list of values, not a subquery: a subquery would be
    /// joined above the view's own joins, after every leg is paired with its other
    /// leg, and the planner could not tell how few rows it keeps. Both conditions are
    /// filters, never joins, so a header with the tag on more than one leg yields each
    /// of its rows once.</para>
    /// </remarks>
    internal static Expression<Func<ResolvedTransactionView, bool>> HasInPlayTagFrom(
        AppDbContext db, Expression<Func<ResolvedTransactionView, bool>> fromCounterparty, Guid tagId,
        IReadOnlyCollection<Guid> candidateHeaders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(fromCounterparty);
        ArgumentNullException.ThrowIfNull(candidateHeaders);
        var headers = candidateHeaders.ToList();
        var tagged = db.TxnLegNoteTags.Where(t => t.TagId == tagId && t.InPlay).Select(t => t.LegId);
        Expression<Func<ResolvedTransactionView, bool>> has = rt => headers.Contains(rt.HeaderId)
            && (fromCounterparty.Inline(rt)
                ? tagged.Contains(rt.CounterpartyId)
                : tagged.Contains(rt.Id));
        return has.Expand();
    }

    /// <summary>
    /// The headers with a leg that has <paramref name="tagId"/> in play, each once, from
    /// the tag's index: every row <see cref="HasInPlayTagFrom"/> keeps is a row of one.
    /// </summary>
    internal static IQueryable<Guid> InPlayTagHeaders(AppDbContext db, Guid tagId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.TxnLegNoteTags.Where(t => t.TagId == tagId && t.InPlay).Select(t => t.HeaderId).Distinct();
    }

    /// <summary>No tag is in play for the leg's presentation: the complement of what a tag cell counts.</summary>
    internal static Expression<Func<Guid, bool>> Untagged(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return legId => !db.TxnLegNoteTags.Any(t => t.LegId == legId && t.InPlay);
    }

    /// <summary>
    /// The memo one account's register shows for each header in <paramref name="entries"/>,
    /// from the stored presentation of the header's legs on that account (live and
    /// template headers alike). A header with no leg on its account is left out.
    /// </summary>
    /// <remarks>
    /// The entry reads its first leg there by posting: where the header is in play (the
    /// split side, an event's investment side) every one of them shows the transaction's
    /// memo, and elsewhere a one-posting row has that one leg. A leg whose presentation
    /// was never stored fails the read, as the register's does.
    /// </remarks>
    internal static async Task<IReadOnlyDictionary<Guid, string?>> MemosOnAccountsAsync(
        AppDbContext db, IReadOnlyCollection<(Guid HeaderId, Guid AccountId)> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return new Dictionary<Guid, string?>();
        var wanted = entries.ToHashSet();
        var headerIds = wanted.Select(e => e.HeaderId).Distinct().ToList();
        var legs = (await db.StoredLegPresentations.AsNoTracking()
                .Where(l => headerIds.Contains(l.HeaderId))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(l => wanted.Contains((l.HeaderId, l.AccountId)))
            .ToList();
        foreach (var leg in legs.Where(l => l.Kind is null))
            throw new InvalidOperationException($"Leg {leg.LegId} has no stored memo-and-tags presentation.");

        var result = new Dictionary<Guid, string?>();
        foreach (var header in legs.GroupBy(l => l.HeaderId))
        {
            var first = header.OrderBy(l => l.PostingIndex).ThenBy(l => l.LegId).First();
            result[header.Key] = first.HeaderInPlay == true ? first.HeaderMemo : first.Memo;
        }
        return result;
    }

    /// <summary>
    /// A leg's own memo, as its stored presentation gives it: what an editor opened on
    /// the leg's side starts from. A shown memo the leg inherits is not its own; on an
    /// event's investment side the memo shown is the transaction's, and the leg holds
    /// none of its own.
    /// </summary>
    internal static string? OwnMemo(StoredLegNotesView leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.Kind is null)
            throw new InvalidOperationException($"Leg {leg.LegId} has no stored memo-and-tags presentation.");
        return leg.Kind == LegKind.EventSide || leg.MemoInherited == true ? null : leg.Memo;
    }

    /// <summary>
    /// Whether the tags a leg's stored presentation shows are its own, by the same test as
    /// <see cref="OwnMemo"/>: shown tags the leg inherits are not its own, and on an
    /// event's investment side the tags shown are the transaction's.
    /// </summary>
    internal static bool OwnsShownTags(StoredLegNotesView leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.Kind is null)
            throw new InvalidOperationException($"Leg {leg.LegId} has no stored memo-and-tags presentation.");
        return leg.Kind != LegKind.EventSide && leg.TagsInherited != true;
    }

    /// <summary>
    /// The names of the tags in <paramref name="role"/>, aggregated in the database,
    /// distinct, in the order the database sorts names; null when there are none.
    /// </summary>
    internal static Expression<Func<Guid, string[]?>> TagNamesOf(
        AppDbContext db, Expression<Func<TxnLegNoteTagRow, bool>> role)
    {
        var tagsOf = TagsOf(db, role);
        Expression<Func<Guid, string[]?>> names = legId => tagsOf.Inline(legId)
            .GroupBy(t => 1)
            .Select(g => EF.Functions.ArrayAgg(g.Select(t => t.Name).Distinct().OrderBy(n => n)))
            .FirstOrDefault();
        return names.Expand();
    }
}
