using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write gateway for creating new transactions. Inserts one
/// <c>txn_headers</c> row plus a posting pair per posting into
/// <c>txn_legs</c> (ADR-0019) inside a single Postgres transaction: mirrored,
/// except between real accounts in different currencies, where each leg carries
/// its own account's figure (ADR-0114 D13, <see cref="PostingLegPair"/>).
///
/// Writes go through the regular <see cref="AppDbContext"/>
/// (coffer_app role). Migration 022 RLS policies cover the inserts:
/// <c>txn_headers</c> uses the user-grant WITH CHECK; <c>txn_legs</c>
/// uses <c>header_id IN (SELECT id FROM txn_headers)</c> which the
/// freshly-inserted header satisfies in the same transaction.
/// </summary>
/// <remarks>
/// <para>Single write gateway for transactions (ADR-0025): create,
/// patch (header overrides + postings reshape), set-recon-status,
/// delete. <see cref="RegisterRepository"/> remains the read side.
/// The pre-ADR-0025 <c>TransactionOverridesRepository</c> retired
/// in the same slice — its header-override + leg-edit duties
/// merged here, and the leg-edit half was replaced by full
/// postings reshape semantics.</para>
///
/// <para><b>Balance recompute</b> on <c>txn_header_account_balances</c>
/// is handled automatically by <see cref="LegDerivedRecomputeInterceptor"/>
/// after every <c>SaveChangesAsync</c> on this context (mig 102 /
/// ADR-0032 / ADR-0034). Every mutation method below reaches
/// <c>SaveChangesAsync</c>, so balance maintenance is implicit. Do
/// NOT add explicit recompute calls — the interceptor is the single
/// source of truth.</para>
///
/// <para><b>The ledger write lock</b> (migration 275). Every method whose save can
/// recompute balances or holdings takes it as the first statement of its transaction,
/// before the reads its write is planned from (<see cref="LedgerWriteLock"/>); the
/// recompute functions refuse a transaction that wrote without it. Methods that write
/// only notes, tags or reconciliation status recompute neither and take no lock.</para>
/// </remarks>
public sealed class TransactionsRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public TransactionsRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Every account this header's legs already sit on.
    /// </summary>
    /// <remarks>
    /// For the inactive-account gate. The rule is that re-targeting onto a
    /// deactivated account is refused while an account ALREADY on the posting
    /// is left alone, so the validator has to know which is which — without
    /// this it sees only the payload, where "unchanged" and "newly chosen"
    /// look identical.
    /// </remarks>
    public Task<IReadOnlySet<Guid>> LegAccountIdsAsync(
        Guid ledgerId, Guid headerId, CancellationToken cancellationToken = default) =>
        LegAccountIdsAsync(_db, ledgerId, headerId, cancellationToken);

    /// <summary>
    /// The stored pair of each posting of <paramref name="headerId"/>, keyed by the id of
    /// its leg on <paramref name="sourceAccountId"/>: that leg and the other leg of its
    /// posting (<see cref="PostingLegPair.StoredPairs"/>). Read before an edit, so the
    /// posting validation can pass a figure the edit resends as it was stored
    /// (ADR-0114 D15). Empty for a header with no legs on that account.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, PostingLegPair.StoredPair>> StoredPairsAsync(
        Guid ledgerId, Guid headerId, Guid sourceAccountId, CancellationToken cancellationToken = default)
    {
        // A merged-away header has no stored pairs, as an id that does not exist has none
        // (MergedAway): the edit it validates answers not-found.
        var live = _db.TxnHeaders.NotMergedAway();
        var legs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && l.HeaderId == headerId && live.Any(h => h.Id == l.HeaderId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return PostingLegPair.StoredPairs(legs, sourceAccountId);
    }

    /// <summary>
    /// <see cref="LegAccountIdsAsync(Guid, Guid, CancellationToken)"/> on a given
    /// context, for a repository that must read it inside its own transaction (the
    /// investment PATCH).
    /// </summary>
    internal static async Task<IReadOnlySet<Guid>> LegAccountIdsAsync(
        AppDbContext db, Guid ledgerId, Guid headerId, CancellationToken cancellationToken = default)
    {
        // A merged-away header sits on no account, as an id that does not exist sits on
        // none (MergedAway), so the posting validation reads it as it reads an unknown id.
        var live = db.TxnHeaders.NotMergedAway();
        var ids = await db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId && l.HeaderId == headerId && live.Any(h => h.Id == l.HeaderId))
            .Select(l => l.AccountId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Create a manual transaction with one or more postings
    /// (ADR-0025). <c>postings.Count == 1</c> produces a
    /// single-row; <c>&gt; 1</c> produces a multi-split. All
    /// postings' source-side legs land on <paramref name="sourceAccountId"/>;
    /// each posting's counterparty leg is created on its
    /// <see cref="TransactionPosting.CounterpartyAccountId"/>, built by
    /// <see cref="PostingLegPair.Build"/>: the negated amount, so the posting sums to
    /// zero, except between two real accounts in different currencies, where it is the
    /// posting's <see cref="TransactionPosting.CounterpartyAmount"/> in the counterparty's
    /// own currency (ADR-0114 D13). Inserts happen inside one Postgres transaction.
    /// </summary>
    /// <remarks>
    /// <para>A posting across currencies without a second amount, or with a zero or
    /// same-signed one, is refused (<see cref="CreateOutcome.PostingRefusal"/>) before
    /// anything is written, whoever the caller is: the reminder fire reaches here without
    /// the endpoint's validation.</para>
    /// <para>Balance recompute is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// <c>SaveChangesAsync</c>.</para>
    /// <para>The memo and tags are planned by the write primitive from
    /// <paramref name="notes"/>, written from <paramref name="sourceAccountId"/>: on one
    /// posting they are that side's own, on several the whole transaction's with
    /// per-line notes. A refusal returns before anything is written.</para>
    /// </remarks>
    public async Task<CreateOutcome> CreateAsync(
        Guid ledgerId,
        Guid sourceAccountId,
        DateTime postedAt,
        string? payee,
        string? checkNumber,
        DateTime? transactedAt,
        IReadOnlyList<TransactionPosting> postings,
        AnnotationIntent<string>? notes,
        // Adjust-at-post fire (ADR-0049): when set, the committed occurrence is
        // stamped to its series + slot. Null = a normal live bank create.
        Guid? recurringTransactionId = null,
        DateOnly? occurrenceDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postings);

        var headerId = Guid.NewGuid();

        // Plan first: a refused write stores nothing. The side must be the register
        // the transaction is entered from, which carries the source leg of every
        // posting; it becomes the originating account.
        var intent = notes ?? AnnotationIntent<string>.Untouched(sourceAccountId);
        if (intent.Side != sourceAccountId)
            return CreateOutcome.Refused(new AnnotationViolation(
                AnnotationViolationCodes.SideNotOnTransaction,
                "The notes must be written from the account the transaction is entered from."));
        var shape = AnnotationWriter.Shape(null, null,
            postings.SelectMany((p, i) => new[]
            {
                new AnnotationWriter.PlannedLeg(i, sourceAccountId),
                new AnnotationWriter.PlannedLeg(i, p.CounterpartyAccountId),
            }),
            AnnotationWriter.AccountFacts.None);

        // The ledger write lock (migration 275) is the first statement, before the reads
        // the write is planned from. Reuse-from-fire (ADR-0049): inside an ambient
        // transaction (the reminder fire path opened one, with the lock, to make the
        // committed occurrence + catch-up atomic) it joins that transaction, takes
        // nothing new, and leaves the commit to that caller.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var resolved = await AnnotationWriter.ResolveAsync(_db, ledgerId, intent, cancellationToken)
            .ConfigureAwait(false);
        var plan = AnnotationPlanner.Seed(shape, resolved);
        if (!plan.IsValid) return CreateOutcome.Refused(plan.Violations[0]);

        // Each posting's two legs, from both accounts' type and currency, read once.
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, ledgerId, postings.Select(p => p.CounterpartyAccountId).Append(sourceAccountId), cancellationToken)
            .ConfigureAwait(false);
        var amounts = new PostingLegPair.LegAmounts[postings.Count];
        for (var i = 0; i < postings.Count; i++)
        {
            var (built, refused) = PostingLegPair.Build(sourceAccountId, postings[i], i, sides);
            if (refused is not null) return CreateOutcome.RefusedPosting(refused);
            amounts[i] = built;
        }

        // 'manual' origin: the factory's; the origin column also informs the
        // hard-delete path (manual rows can be hard-deleted; imported rows become
        // is_hidden=true to preserve audit).
        var header = TxnHeaderRow.NewTransaction(headerId, ledgerId, sourceAccountId);
        header.Payee = HeaderText.Normalize(payee);
        header.CheckNumber = checkNumber;
        header.PostedAt = postedAt;
        // NOT NULL since mig 189: a null request value means "no distinct tax
        // date", which is stored as the posted date.
        header.TransactedAt = transactedAt ?? postedAt;
        // Stamped only when the reminder fire path supplies them.
        header.RecurringTransactionId = recurringTransactionId;
        header.OccurrenceDate = occurrenceDate;
        _db.TxnHeaders.Add(header);

        var legs = new List<TxnLegRow>(postings.Count * 2);
        for (var i = 0; i < postings.Count; i++)
            legs.AddRange(AddPostingLegs(headerId, ledgerId, sourceAccountId, postings[i], amounts[i], postingIndex: i));

        await AnnotationWriter.ApplyAsync(_db, header, legs, plan, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreateOutcome.Created(headerId);
    }

    /// <summary>
    /// Outcome of <see cref="CreateAsync"/>: the new header; or the reason the write
    /// primitive refused the notes (a 422 with the violation's code); or the posting the
    /// leg-pair builder refused (<see cref="PostingRefusal"/>, ADR-0114 D13). Nothing is
    /// written on either refusal.
    /// </summary>
    public sealed record CreateOutcome(
        Guid HeaderId, AnnotationViolation? Refusal, PostingLegPair.Refusal? PostingRefusal = null)
    {
        public static CreateOutcome Created(Guid headerId) => new(headerId, null);
        public static CreateOutcome Refused(AnnotationViolation violation) => new(Guid.Empty, violation);
        internal static CreateOutcome RefusedPosting(PostingLegPair.Refusal refusal) => new(Guid.Empty, null, refusal);
    }

    /// <summary>
    /// Postings-reshape pre-check outcome. Either a typed failure
    /// the endpoint surfaces as 422, or success with the prepared
    /// <see cref="PostingsReshapePlan"/> the apply step consumes.
    /// </summary>
    private enum PostingsReshapeFailure
    {
        /// <summary>The supplied source account doesn't match the
        /// existing source-side legs' account — the SPA tried to
        /// move the transaction across accounts via this endpoint,
        /// which isn't its job.</summary>
        SourceAccountMismatch,
        /// <summary>A request item's <c>LegId</c> doesn't match any
        /// existing source-side leg on this header.</summary>
        LegNotInHeader,
        /// <summary>Two request items carry the SAME <c>LegId</c>.
        /// Silently destructive if unchecked: both items
        /// resolve to one tracked row, so the last array position
        /// wins, the earlier one produces no posting, and the header
        /// ends up with fewer postings than the request listed plus a
        /// hole in <c>posting_index</c> — which then trips the
        /// <c>posting_index &gt; 0</c> split detectors and the
        /// <c>posting_index = 0</c> canonical-leg lookup. All on an
        /// HTTP 200.</summary>
        DuplicateLegId,
    }

    /// <summary>
    /// Result of <see cref="PreparePostingsReshapeAsync"/>. Carries
    /// the per-leg lookups the apply step needs so it doesn't have
    /// to re-load anything from the DB.
    /// </summary>
    private sealed record PostingsReshapePlan(
        Guid HeaderId,
        Guid LedgerId,
        Guid SourceAccountId,
        IReadOnlyList<TransactionPosting> Items,
        IReadOnlyList<TxnLegRow> SourceLegs,
        IReadOnlyDictionary<Guid, TxnLegRow> SourceLegById,
        IReadOnlyDictionary<Guid, TxnLegRow> CounterpartyBySourceLegId,
        IReadOnlySet<Guid> KeepLegIds)
    {
        /// <summary>Each item's two leg amounts, in item order, built by
        /// <see cref="PostingLegPair.Build"/> before anything is applied.</summary>
        public IReadOnlyList<PostingLegPair.LegAmounts> Amounts { get; init; } = [];
    }

    /// <summary>
    /// Outcome of <see cref="PatchAsync"/>. Each rejection code maps
    /// 1:1 to a 422 surface in the endpoint.
    /// </summary>
    public enum PatchResult
    {
        Ok,
        HeaderNotInLedger,
        PostingsLegNotInHeader,
        /// <summary>Two items in the postings list carry the same
        /// <c>legId</c>. Rejected rather than applied: the reshape
        /// would drop a posting and leave a hole in
        /// <c>posting_index</c> while returning 200.</summary>
        PostingsDuplicateLegId,
        PostingsSourceAccountMismatch,
        /// <summary>Slice 2c.6d: <c>mergeFromHeaderId</c> in the
        /// PATCH body doesn't resolve to a valid merge source —
        /// wrong ledger, wrong origin (not manual), already merged,
        /// or self-merge.</summary>
        MergeSourceInvalid,
        /// <summary>ADR-0029: header exists in the ledger but its
        /// <c>action</c> is non-null — it's an investment-shape
        /// header that belongs on <c>/investment-transactions</c>.
        /// The bank endpoint surfaces this as
        /// <c>transaction-header-is-investment</c>.</summary>
        HeaderNotBankShape,
        /// <summary>Migration 230: the request carried <c>postedAt</c> or
        /// <c>transactedAt</c> explicitly set to null. Both columns are NOT
        /// NULL, so there is no cleared state to move to. Rejected rather than
        /// ignored — silently doing nothing with a field the caller named is
        /// the exact failure the presence-based header fields exist to
        /// remove.</summary>
        HeaderDateNull,
        /// <summary>The write primitive refused the notes (a read-only row, a side
        /// not on the transaction, bad line notes, a tag set grown past the limit);
        /// <see cref="PatchOutcome.Refusal"/> says which. Nothing is written.</summary>
        NotesRefused,
        /// <summary>The leg-pair builder refused a posting between real accounts in
        /// different currencies: no second amount, or a zero or same-signed pair that the
        /// edit changes (ADR-0114 D13); <see cref="PatchOutcome.PostingRefusal"/> says
        /// which. Nothing is written.</summary>
        PostingRefused,
    }

    /// <summary>
    /// <see cref="PatchAsync"/>'s result, with the write primitive's reason when it
    /// refused the notes.
    /// </summary>
    public sealed record PatchOutcome(
        PatchResult Result, AnnotationViolation? Refusal = null, PostingLegPair.Refusal? PostingRefusal = null)
    {
        public static implicit operator PatchOutcome(PatchResult result) => new(result);
    }

    /// <summary>Outcome of <see cref="RecategorizeAsync"/> (ADR-0068).</summary>
    public enum RecategorizeResult
    {
        Ok, HeaderNotInLedger, HeaderNotBankShape, IsSplit, NoCategoryLeg,
        CategoryNotInLedger, NoChange, ApplyFailed,
    }

    public sealed record RecategorizeOutcome(
        RecategorizeResult Result, string? BeforeCategory, string? AfterCategory);

    /// <summary>
    /// Recategorize ONE simple transaction (ADR-0068): repoint its single category
    /// leg to <paramref name="newCategoryId"/>. Only a single-posting, bank-shape
    /// header with exactly one category leg qualifies — a split (&gt;1 posting) or a
    /// transfer (no category leg) is rejected (the editor owns those). REUSES
    /// <see cref="PatchAsync"/> for the leg reshape, so the balance + posting-count
    /// recompute runs through the one canonical path (the interceptor). dryRun reports
    /// the before/after category without writing.
    /// </summary>
    public async Task<RecategorizeOutcome> RecategorizeAsync(
        Guid ledgerId, Guid headerId, Guid newCategoryId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) before the reads the reshape is built
        // from, so no other write to the ledger lands between them and the reshape. A dry
        // run writes nothing and takes no lock.
        await using var write = dryRun
            ? null
            : await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // A merged-away header is not in the ledger to a write by id (MergedAway).
        var header = await _db.TxnHeaders.AsNoTracking().NotMergedAway()
            .Where(h => h.Id == headerId && h.LedgerId == ledgerId)
            .Select(h => new { h.Action })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return new(RecategorizeResult.HeaderNotInLedger, null, null);
        if (header.Action is not null) return new(RecategorizeResult.HeaderNotBankShape, null, null);

        var legs = await (
            from l in _db.TxnLegs.AsNoTracking()
            where l.HeaderId == headerId && l.LedgerId == ledgerId
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            select new { l.Id, l.AccountId, l.PostingIndex, l.Amount, a.AccountType, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (legs.Select(l => l.PostingIndex).Distinct().Count() > 1)
            return new(RecategorizeResult.IsSplit, null, null);

        var categoryLegs = legs.Where(l => l.AccountType == "category").ToList();
        var sourceLeg = legs.FirstOrDefault(l => l.AccountType != "category");
        if (categoryLegs.Count != 1 || sourceLeg is null)
            return new(RecategorizeResult.NoCategoryLeg, null, null);
        var categoryLeg = categoryLegs[0];

        var newCat = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == newCategoryId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.Name })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (newCat is null || newCat.AccountType != "category")
            return new(RecategorizeResult.CategoryNotInLedger, null, null);

        if (categoryLeg.AccountId == newCategoryId)
            return new(RecategorizeResult.NoChange, categoryLeg.Name, newCat.Name);
        if (dryRun)
            return new(RecategorizeResult.Ok, categoryLeg.Name, newCat.Name);

        // Reuse the canonical bank-transaction edit: keep the single posting (matched
        // by its source-side leg id) with its counterparty repointed to the new
        // category. PatchAsync owns the reshape + the derived-state recompute. A
        // repoint, not a notes write: each leg keeps its own memo and tags (it is the
        // same row), and ownership follows the moved leg if it was the owner's.
        var patch = await PatchCoreAsync(ledgerId, headerId, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = sourceLeg.AccountId,
                Items = new[]
                {
                    new TransactionPosting
                    {
                        LegId = sourceLeg.Id,
                        CounterpartyAccountId = newCategoryId,
                        Amount = sourceLeg.Amount,
                    },
                },
            },
        }, repointTarget: newCategoryId, cancellationToken).ConfigureAwait(false);
        if (patch.Result != PatchResult.Ok)
            return new(RecategorizeResult.ApplyFailed, categoryLeg.Name, newCat.Name);

        await write!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(RecategorizeResult.Ok, categoryLeg.Name, newCat.Name);
    }

    public enum SplitPostingRecategorizeResult
    {
        Ok, HeaderNotInLedger, HeaderNotBankShape, NotSplit, TargetNotInLedger,
        PostingNotFound, NoChange, UnsupportedShape, ApplyFailed,
    }

    public sealed record SplitPostingRecategorizeOutcome(
        SplitPostingRecategorizeResult Result, int Moved, string? FromCategory, string? ToCategory);

    /// <summary>
    /// Recategorize the posting(s) of ONE SPLIT (multi-posting) bank-shape transaction
    /// that sit on <paramref name="fromCategoryId"/> — repoint them to
    /// <paramref name="toCategoryId"/> and leave every other posting untouched. ALL of
    /// this header's fromCategory postings move (a re-home; no "which one" ambiguity —
    /// that keeps the bulk wrapper unambiguous). Bank-shape only: an investment header
    /// (<c>action</c> set) or a single-posting transaction (<see cref="RecategorizeAsync"/>'s
    /// job) is rejected. REUSES <see cref="PatchAsync"/> with the FULL posting set (only
    /// the matched counterparties swapped), so the ADR-0025 reshape + recompute run
    /// through the one canonical path. dryRun reports the count without writing. The
    /// per-header primitive behind <see cref="BulkRecategorizeSplitPostingsAsync"/>.
    /// </summary>
    public async Task<SplitPostingRecategorizeOutcome> RecategorizeSplitPostingsAsync(
        Guid ledgerId, Guid headerId, Guid fromCategoryId, Guid toCategoryId,
        bool dryRun, CancellationToken cancellationToken = default)
    {
        // The ledger write lock first, as RecategorizeAsync takes it: a reshape built from
        // a split read before another write changed it would drop or misplace lines.
        await using var write = dryRun
            ? null
            : await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        // A merged-away header is not in the ledger to a write by id (MergedAway).
        var header = await _db.TxnHeaders.AsNoTracking().NotMergedAway()
            .Where(h => h.Id == headerId && h.LedgerId == ledgerId)
            .Select(h => new { h.Action })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return new(SplitPostingRecategorizeResult.HeaderNotInLedger, 0, null, null);
        if (header.Action is not null) return new(SplitPostingRecategorizeResult.HeaderNotBankShape, 0, null, null);

        var legs = await (
            from l in _db.TxnLegs.AsNoTracking()
            where l.HeaderId == headerId && l.LedgerId == ledgerId
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            // This ORDER BY is load-bearing, not tidiness. The GroupBy below keys on
            // PostingIndex and preserves the order its keys first appear in, and
            // PatchAsync renumbers posting_index from the resulting array position —
            // so reading unordered silently REWRITES the user's split order as a side
            // effect of a recategorize. Postgres guarantees no order without it, and
            // because every UPDATE moves the row to a new heap location a seq scan
            // tends to return recently-touched legs LAST: the permutation is likely,
            // not theoretical. Recategorizing one line of a long paycheck split
            // could reshuffle every other line of it.
            //
            // The sibling read in RecategorizeAsync needs no such clause: it returns
            // IsSplit before order can matter, so it only ever sees one posting.
            orderby l.PostingIndex, l.Id
            select new { l.Id, l.AccountId, l.PostingIndex, l.Amount, a.AccountType, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // A split has >1 posting. Single-posting is set_transaction_category's job.
        if (legs.Select(l => l.PostingIndex).Distinct().Count() <= 1)
            return new(SplitPostingRecategorizeResult.NotSplit, 0, null, null);

        var toCat = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == toCategoryId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.Name })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (toCat is null || toCat.AccountType != "category")
            return new(SplitPostingRecategorizeResult.TargetNotInLedger, 0, null, null);

        var fromLegs = legs
            .Where(l => l.AccountType == "category" && l.AccountId == fromCategoryId)
            .ToList();
        if (fromLegs.Count == 0)
            return new(SplitPostingRecategorizeResult.PostingNotFound, 0, null, toCat.Name);
        var fromName = fromLegs[0].Name;
        if (fromCategoryId == toCategoryId)
            return new(SplitPostingRecategorizeResult.NoChange, 0, fromName, toCat.Name);

        // Source account = a fromCategory posting's OTHER (non-category) leg's account —
        // the single account PatchAsync expects on every posting (bank reshape).
        var anchorSource = legs.FirstOrDefault(l =>
            l.PostingIndex == fromLegs[0].PostingIndex && l.Id != fromLegs[0].Id);
        if (anchorSource is null)
            return new(SplitPostingRecategorizeResult.UnsupportedShape, 0, fromName, toCat.Name);
        var sourceAccountId = anchorSource.AccountId;

        // Rebuild the FULL posting set: one Item per posting keyed by its leg on the
        // source account, counterparty unchanged — except EVERY posting whose
        // counterparty is fromCategory, which moves to toCategory. Every posting must
        // carry a source-account leg or the reshape would DROP it (PatchAsync deletes
        // postings whose source leg isn't in the set).
        var items = new List<TransactionPosting>();
        var moved = 0;
        foreach (var g in legs.GroupBy(l => l.PostingIndex))
        {
            var src = g.FirstOrDefault(l => l.AccountId == sourceAccountId);
            var other = g.FirstOrDefault(l => src is null || l.Id != src.Id);
            if (src is null || other is null)
                return new(SplitPostingRecategorizeResult.UnsupportedShape, 0, fromName, toCat.Name);
            var isTarget = other.AccountType == "category" && other.AccountId == fromCategoryId;
            if (isTarget) moved++;
            items.Add(new TransactionPosting
            {
                LegId = src.Id,
                CounterpartyAccountId = isTarget ? toCategoryId : other.AccountId,
                Amount = src.Amount,
                // The far leg as stored (ADR-0114 D13): a transfer between currencies keeps
                // its own figure, and the builder drops it on a mirrored line.
                CounterpartyAmount = other.Amount,
            });
        }

        if (dryRun)
            return new(SplitPostingRecategorizeResult.Ok, moved, fromName, toCat.Name);

        // A repoint (see RecategorizeAsync): the moved legs keep their notes, and
        // ownership follows them only if the owner had no other leg.
        var patch = await PatchCoreAsync(ledgerId, headerId, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = sourceAccountId,
                Items = items.ToArray(),
            },
        }, repointTarget: toCategoryId, cancellationToken).ConfigureAwait(false);
        if (patch.Result != PatchResult.Ok)
            return new(SplitPostingRecategorizeResult.ApplyFailed, moved, fromName, toCat.Name);

        await write!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SplitPostingRecategorizeResult.Ok, moved, fromName, toCat.Name);
    }

    /// <summary>
    /// The reject reason of a header a bulk recategorize could not move because another
    /// write to the ledger held its write lock past the wait (migration 275): the busy
    /// refusal's own code. The header wrote nothing; the headers moved before it stay moved,
    /// and the bulk goes on to the next.
    /// </summary>
    public const string LedgerWriteBusyReject = Errors.BusinessError.Codes.LedgerWriteBusy;

    public enum BulkSplitPostingResult { Ok, TargetNotInLedger, NoHeaders }

    public sealed record BulkSplitPostingReject(Guid HeaderId, string Reason);

    public sealed record BulkSplitPostingOutcome(
        BulkSplitPostingResult Result, int Moved, int Unchanged, string? ToCategory,
        IReadOnlyList<BulkSplitPostingReject> Rejects);

    /// <summary>
    /// Bulk <c>set_split_posting_category</c> (ADR-0068 slice E): across
    /// <paramref name="headerIds"/>, repoint every posting on fromCategory to
    /// toCategory via the per-header <see cref="RecategorizeSplitPostingsAsync"/>.
    /// Best-effort — a header that isn't a bank-shape split with a fromCategory posting,
    /// or that another write to the ledger kept from its write lock past the wait
    /// (<see cref="LedgerWriteBusyReject"/>), is returned in <c>Rejects</c> (never blocks
    /// the rest; each header commits on its own). The whole call fails only
    /// for a bad target category or an empty list (mirrors
    /// <see cref="BulkRecategorizeAsync"/>). dryRun previews the tally without writing.
    /// </summary>
    public async Task<BulkSplitPostingOutcome> BulkRecategorizeSplitPostingsAsync(
        Guid ledgerId, IReadOnlyList<Guid> headerIds, Guid fromCategoryId, Guid toCategoryId,
        bool dryRun, CancellationToken cancellationToken = default)
    {
        if (headerIds.Count == 0)
            return new(BulkSplitPostingResult.NoHeaders, 0, 0, null, Array.Empty<BulkSplitPostingReject>());

        // Up-front target check → a bad target fails the whole batch (nothing written),
        // rather than N identical per-row rejects.
        var toCat = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == toCategoryId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.Name })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (toCat is null || toCat.AccountType != "category")
            return new(BulkSplitPostingResult.TargetNotInLedger, 0, 0, null, Array.Empty<BulkSplitPostingReject>());

        var moved = 0;
        var unchanged = 0;
        var rejects = new List<BulkSplitPostingReject>();
        foreach (var headerId in headerIds.Distinct())
        {
            SplitPostingRecategorizeOutcome r;
            try
            {
                r = await RecategorizeSplitPostingsAsync(
                    ledgerId, headerId, fromCategoryId, toCategoryId, dryRun, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LedgerWriteBusyException)
            {
                // Each header commits on its own, so the headers before this one stay moved;
                // this one wrote nothing (its transaction rolled back at the lock) and is
                // rejected, as any header that could not be moved is.
                _db.ChangeTracker.Clear();
                rejects.Add(new(headerId, LedgerWriteBusyReject));
                continue;
            }
            switch (r.Result)
            {
                case SplitPostingRecategorizeResult.Ok: moved += r.Moved; break;
                case SplitPostingRecategorizeResult.NoChange: unchanged++; break;
                case SplitPostingRecategorizeResult.HeaderNotInLedger: rejects.Add(new(headerId, "not-in-ledger")); break;
                case SplitPostingRecategorizeResult.HeaderNotBankShape: rejects.Add(new(headerId, "investment-transaction")); break;
                case SplitPostingRecategorizeResult.NotSplit: rejects.Add(new(headerId, "not-a-split")); break;
                case SplitPostingRecategorizeResult.PostingNotFound: rejects.Add(new(headerId, "posting-not-found")); break;
                case SplitPostingRecategorizeResult.UnsupportedShape: rejects.Add(new(headerId, "unsupported-shape")); break;
                default: rejects.Add(new(headerId, "apply-failed")); break;
            }
        }
        return new(BulkSplitPostingResult.Ok, moved, unchanged, toCat.Name, rejects);
    }

    /// <summary>One header a bulk recategorize could not move, with a reason code
    /// (split / transfer / investment-transaction / not-in-ledger / category-not-in-ledger /
    /// apply-failed / <see cref="LedgerWriteBusyReject"/>).</summary>
    public sealed record BulkRecategorizeReject(Guid HeaderId, string Reason);

    /// <summary>Outcome of <see cref="BulkRecategorizeAsync"/>.</summary>
    public enum BulkRecategorizeResult { Ok, TargetNotInLedger, NoHeaders }

    public sealed record BulkRecategorizeOutcome(
        BulkRecategorizeResult Result,
        string? CategoryName,
        int Recategorized,
        int Unchanged,
        IReadOnlyList<BulkRecategorizeReject> Rejects);

    /// <summary>
    /// Recategorize MANY simple transactions to one target category — the same
    /// single-category-leg reshape as <see cref="RecategorizeAsync"/>, applied per id.
    /// <b>Best-effort</b>: every recategorizable header is moved; a header that can't be
    /// (split, transfer, investment, or not-in-ledger), or that another write to the ledger
    /// kept from its write lock past the wait (<see cref="LedgerWriteBusyReject"/>), is
    /// REJECTED and returned in <see cref="BulkRecategorizeOutcome.Rejects"/> with a reason,
    /// so one bad row never blocks the rest. Each header commits on its own, so a header
    /// moved before a rejected one stays moved. The target category is validated ONCE up front — an invalid
    /// target rejects the WHOLE call (nothing written), since it's a batch-level
    /// precondition, not a per-row one; an empty id list is likewise a no-op reject.
    /// dryRun previews the split (recategorized / unchanged / rejects) without writing.
    /// </summary>
    public async Task<BulkRecategorizeOutcome> BulkRecategorizeAsync(
        Guid ledgerId, IReadOnlyList<Guid> headerIds, Guid newCategoryId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var ids = headerIds.Distinct().ToList();
        if (ids.Count == 0)
            return new(BulkRecategorizeResult.NoHeaders, null, 0, 0, Array.Empty<BulkRecategorizeReject>());

        // Batch-level precondition: the target must be a category in this ledger.
        // Validated once here (rather than surfacing as a per-row reject on every id).
        var newCat = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == newCategoryId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.Name })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (newCat is null || newCat.AccountType != "category")
            return new(BulkRecategorizeResult.TargetNotInLedger, null, 0, 0, Array.Empty<BulkRecategorizeReject>());

        var rejects = new List<BulkRecategorizeReject>();
        int recategorized = 0, unchanged = 0;
        foreach (var headerId in ids)
        {
            RecategorizeOutcome r;
            try
            {
                r = await RecategorizeAsync(ledgerId, headerId, newCategoryId, dryRun, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LedgerWriteBusyException)
            {
                // As in the split-posting bulk: the headers before this one stay moved, and
                // this one, which wrote nothing, is rejected.
                _db.ChangeTracker.Clear();
                rejects.Add(new(headerId, LedgerWriteBusyReject));
                continue;
            }
            switch (r.Result)
            {
                case RecategorizeResult.Ok: recategorized++; break;
                case RecategorizeResult.NoChange: unchanged++; break;
                case RecategorizeResult.IsSplit: rejects.Add(new(headerId, "split")); break;
                case RecategorizeResult.NoCategoryLeg: rejects.Add(new(headerId, "transfer")); break;
                case RecategorizeResult.HeaderNotBankShape: rejects.Add(new(headerId, "investment-transaction")); break;
                case RecategorizeResult.HeaderNotInLedger: rejects.Add(new(headerId, "not-in-ledger")); break;
                case RecategorizeResult.CategoryNotInLedger: rejects.Add(new(headerId, "category-not-in-ledger")); break;
                default: rejects.Add(new(headerId, "apply-failed")); break;
            }

            // Each RecategorizeAsync commits its own reshape (best-effort — successes
            // persist independently of later rejects). Detach its tracked entities so a
            // real run over many ids stays as clean as the single-call path.
            _db.ChangeTracker.Clear();
        }

        return new(BulkRecategorizeResult.Ok, newCat.Name, recategorized, unchanged, rejects);
    }

    /// <summary>Outcome of <see cref="SetTransactionTagsAsync"/> (ADR-0081 D6).</summary>
    public enum SetTagsResult
    {
        Ok,
        HeadersNotInLedger,
        /// <summary>The write primitive refused at least one transaction (a row the
        /// account shows read-only, an account not on it, a set grown past the limit);
        /// <see cref="SetTagsOutcome.Placements"/> says which. Nothing is tagged.</summary>
        Refused,
    }

    /// <param name="Tags">The tag set, one name per tag (the first spelling given).</param>
    /// <param name="Placements">Per transaction: where the tags land, or why they do not.</param>
    public sealed record SetTagsOutcome(
        SetTagsResult Result,
        int HeaderCount,
        IReadOnlyList<string> Tags,
        IReadOnlyList<Guid> UnknownHeaderIds,
        IReadOnlyList<SetTransactionTagsPlacement> Placements);

    /// <summary>
    /// Replace the tag set on MANY transactions at once, as
    /// <paramref name="accountId"/>'s register writes it (ADR-0081 D6 — the deliberate
    /// bulk exception to ADR-0068 D4's one-entity-per-call rule; tagging is an
    /// idempotent replace-set on a junction table with low blast radius). Every header
    /// must belong to <paramref name="ledgerId"/>; if any doesn't, the WHOLE batch is
    /// rejected (all-or-nothing) with the offending ids, so a bad id fails loud rather
    /// than silently tagging a subset. <paramref name="tags"/> is a replace-set — the
    /// complete set to assign; an empty list clears it.
    /// </summary>
    /// <remarks>
    /// <para>The same transaction carries different tags on each side, so the tags
    /// are written from one account, through the write primitive: on a one-posting
    /// transaction they are that account's own; on a multi-posting one, the whole
    /// transaction's. A transaction that account shows read-only, or that it is not
    /// on, refuses the whole call, like an id outside the ledger, and nothing is
    /// tagged.</para>
    /// <para>Runs in one transaction; tag resolution (create-on-first-use) happens
    /// ONCE for the batch. dryRun plans every write and reports each transaction's
    /// placement without writing.</para>
    /// </remarks>
    public async Task<SetTagsOutcome> SetTransactionTagsAsync(
        Guid ledgerId,
        IReadOnlyList<Guid> headerIds,
        Guid accountId,
        IReadOnlyList<string> tags,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var ids = headerIds.Distinct().ToList();
        var requested = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        // Header-in-ledger guard for the WHOLE batch (RLS also scopes the query to the
        // caller's ledgers, so a foreign header simply doesn't resolve here). Any id
        // that doesn't resolve → reject the batch, tag nothing. A merged-away header does
        // not resolve either: it is reported as an unknown id (MergedAway).
        var headers = await _db.TxnHeaders.NotMergedAway()
            .Where(h => h.LedgerId == ledgerId && ids.Contains(h.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var foundSet = headers.Select(h => h.Id).ToHashSet();
        var unknown = ids.Where(id => !foundSet.Contains(id)).ToList();
        if (unknown.Count > 0)
            return new(SetTagsResult.HeadersNotInLedger, 0, requested, unknown, []);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Resolve ONCE for the batch; names that share a key are one tag, reported
        // under the first spelling given.
        var resolved = await TagDictionary.ResolveAsync(_db, ledgerId, requested, cancellationToken)
            .ConfigureAwait(false);
        var tagIds = requested.Select(n => resolved[n]).Distinct().ToList();
        var names = requested.Where((n, i) => requested.FindIndex(m => resolved[m] == resolved[n]) == i).ToList();
        var intent = new AnnotationIntent<Guid>(
            accountId,
            FieldEdit<string>.Leave,
            tagIds.Count == 0 ? FieldEdit<IReadOnlyList<Guid>>.Inherit : FieldEdit<IReadOnlyList<Guid>>.Set(tagIds));

        // Loaded and stored as one batch, so the round trips do not grow with the ids.
        var ordered = headers.OrderBy(h => ids.IndexOf(h.Id)).ToList();
        var storedAll = await AnnotationWriter.LoadManyAsync(_db, ordered, cancellationToken).ConfigureAwait(false);
        var planned = new List<AnnotationWriter.PlannedWrite>(headers.Count);
        var placements = new List<SetTransactionTagsPlacement>(headers.Count);
        foreach (var stored in storedAll)
        {
            var plan = AnnotationPlanner.Plan(ShapeChange<Guid>.Unchanged(stored.Shape, stored.Notes), intent);
            placements.Add(plan.IsValid
                ? new SetTransactionTagsPlacement(stored.Header.Id, SetTransactionTagsContract.LandsOn(stored.Shape), null)
                : new SetTransactionTagsPlacement(stored.Header.Id, null, plan.Violations[0].Code));
            planned.Add(new AnnotationWriter.PlannedWrite(stored.Header, stored.Legs, plan));
        }

        if (placements.Any(p => p.Refused is not null))
            return new(SetTagsResult.Refused, 0, names, [], placements);
        if (dryRun)
            return new(SetTagsResult.Ok, ids.Count, names, [], placements);

        await AnnotationWriter.ApplyManyAsync(_db, planned, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SetTagsResult.Ok, ids.Count, names, [], placements);
    }

    /// <summary>
    /// Returns true when a header with <paramref name="headerId"/>
    /// exists *and* belongs to <paramref name="ledgerId"/>.
    /// </summary>
    public async Task<bool> HeaderBelongsToLedgerAsync(
        Guid ledgerId, Guid headerId, CancellationToken cancellationToken = default) =>
        await _db.TxnHeaders.AsNoTracking()
            .AnyAsync(h => h.Id == headerId && h.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// True iff a header exists in this ledger AND its <c>action</c>
    /// is <c>NULL</c> (the bank-shape identity — the bank
    /// <c>/transactions</c> endpoint asks about the shape it owns).
    /// Investment-shape headers fail this check and route the user
    /// to <c>/investment-transactions</c>.
    /// </summary>
    /// <remarks>
    /// Each topic owns its own positive identity check; the bank
    /// endpoints query this repository (their own), and the
    /// investment endpoints query <see cref="InvestmentTransactionsRepository"/>
    /// — neither inspects the other's domain (ADR-0029).
    /// </remarks>
    public async Task<bool> HeaderIsBankShapeInLedgerAsync(
        Guid ledgerId, Guid headerId, CancellationToken cancellationToken = default) =>
        await _db.TxnHeaders.AsNoTracking()
            .AnyAsync(h => h.Id == headerId
                        && h.LedgerId == ledgerId
                        && h.Action == null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Slice 2c.6c — Tier 1 similar-payee recall. For a row imported
    /// from any provider, find prior approved rows in the same ledger
    /// from the SAME provider whose raw bank payee EXACTLY matches the
    /// current row's, then surface the <c>(payee, counterparty)</c>
    /// pairs the user previously chose on those rows so the editor can
    /// offer one-click categorization of recurring charges.
    ///
    /// <para>Provider scope: anchored on <see
    /// cref="Entities.TxnHeaderRow.ProviderKey"/>. The anchor must
    /// have a non-null provider_key (i.e. a non-manual ingest);
    /// candidates are restricted to the same provider_key. Manual
    /// rows have NULL provider_key per the mig-107 CHECK and are
    /// excluded from both sides — recall is a feed-row concern.</para>
    ///
    /// <para>Account scope: recall is oriented around the anchor's
    /// MONEY-SIDE account — the leg whose account isn't a category.
    /// An un-accepted feed row is always
    /// <c>(real account, Uncategorized)</c> by construction (the
    /// ingest orchestrator parks every counterparty leg on
    /// Uncategorized; only the Accept PATCH moves it), so exactly one
    /// leg qualifies. Candidates must post to that same account, and
    /// the suggested counterparty is the candidate's OTHER leg.</para>
    ///
    /// <para>Keying off the money side — rather than filtering
    /// candidates to an <c>account_type = 'category'</c> leg — is what
    /// lets a TRANSFER participate. A recurring charge the user
    /// settles as a transfer to a spending account ("→ FSA") has two real-account legs and no
    /// category leg at all; under the old filter such a row could
    /// never be recalled, which silently blanked the panel for anyone
    /// whose history for a payee was all transfers. Both shapes are
    /// valid counterparties in the editor's AccountCategoryPicker, so
    /// both belong here.</para>
    ///
    /// <para>Returns empty when: the header doesn't exist or
    /// doesn't belong to the ledger; the header is a manual row
    /// (null provider_key); the bank payee is null/empty; or no
    /// matching prior rows exist.</para>
    ///
    /// <para>Only single-posting prior rows participate (exactly two
    /// legs). Split-target recall is out of scope for this slice —
    /// the multi-leg structure doesn't fit the one-chip = one-pair
    /// shape.</para>
    ///
    /// <para>Aggregation: GROUP BY
    /// <c>(resolved_payee, counterparty_account_id)</c>; sort by use
    /// count desc, last_used_at desc; cap at
    /// <paramref name="limit"/>.</para>
    /// </summary>
    public async Task<IReadOnlyList<SimilarPayeeDto>> GetSimilarPayeesAsync(
        Guid ledgerId,
        Guid headerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // 1. Read the anchor — the current row's raw bank payee, its
        //    provider_key (the recall scope), its money-side account
        //    (the recall axis), and its resolved (payee, counterparty)
        //    so we can dedupe suggestions that already match the saved
        //    state on THIS row (no point suggesting what's already
        //    there).
        //
        //    ANCHORED ON THE FEED'S PAYEE, which since mig 230 lives in
        //    txn_header_originals — the header itself now holds the curated
        //    name, and that is what we are trying to RECALL, not the key we
        //    search by. A header with no original has never been edited, so its
        //    own payee IS the feed's: `original ?? h.Payee` reads correctly
        //    either way. Getting this backwards would key recall on the curated
        //    name and match only rows nobody renamed — the panel would go
        //    quietly empty. Manual rows (null provider_key)
        //    are excluded — recall is a feed-row concern, and the
        //    candidate scope below would have nothing to match against
        //    anyway.
        var anchor = await (
            // A merged-away anchor is not found (MergedAway): no suggestions, as for an
            // id that does not exist.
            from h in _db.TxnHeaders.AsNoTracking().NotMergedAway()
            where h.Id == headerId && h.LedgerId == ledgerId
                && h.ProviderKey != null
                // Accept-flow gates — match merge-candidates'
                // target-side validation. Server enforces these
                // independent of the SPA's UI filtering per the
                // server-side-concurrency principle.
                && h.NeedsReview
                // is_hidden is canonical since mig 230 — no override layer left
                // to resolve through.
                && !h.IsHidden
            // The money side — the leg that isn't a category. The
            // NeedsReview gate above guarantees an un-accepted feed
            // row, which lands as (real account, Uncategorized), so
            // exactly one leg qualifies.
            from moneyLeg in _db.TxnLegs
            where moneyLeg.HeaderId == h.Id
            join moneyAccount in _db.Accounts on moneyLeg.AccountId equals moneyAccount.Id
            where moneyAccount.AccountType != "category"
            let feedPayee = _db.TxnHeaderOriginals
                .Where(o => o.HeaderId == h.Id)
                .Select(o => o.Payee)
                .FirstOrDefault()
            // "The anchor has a bank payee to search by" — asked of the FEED's
            // payee, not the header's. Since mig 230 h.Payee is not the feed's: on a needs_review row
            // someone edited without approving (MCP, or a direct PATCH — the SPA
            // always pairs a save with approve), h.Payee is the curated name, and
            // a CLEARED one would fail the gate while the bank's payee sat in the
            // sidecar with recall still perfectly possible.
            where (feedPayee ?? h.Payee) != null && (feedPayee ?? h.Payee) != ""
            select new
            {
                // The FEED's payee — the original where one was captured, else
                // the header's own (never edited, so it is still the feed's).
                BankPayee = (feedPayee ?? h.Payee)!,
                ProviderKey = h.ProviderKey!,
                // What this row reads as now: simply the canonical value.
                ResolvedPayee = h.Payee!,
                SourceAccountId = moneyAccount.Id,
                // Whatever the other leg currently points at —
                // Uncategorized on a freshly ingested row.
                CurrentCounterpartyAccountId = _db.TxnLegs
                    .Where(l => l.HeaderId == h.Id && l.AccountId != moneyAccount.Id)
                    .Select(l => (Guid?)l.AccountId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (anchor is null) return Array.Empty<SimilarPayeeDto>();

        // 2. Pull the matching prior rows' raw projection, then
        //    aggregate in C#. Practical row volumes for a single
        //    online payee are ≤30 in normal use — well under the
        //    threshold where server-side GroupBy starts to pay
        //    for itself. Keeping the LINQ shape simple also keeps
        //    EF translation predictable.
        var rows = await (
            from h in _db.TxnHeaders.AsNoTracking()
            where h.LedgerId == ledgerId
                && h.Id != headerId
                // Recall is bounded to the anchor's provider so
                // each feed's payee vocabulary stays separate
                // (a SimpleFIN-cleaned name and an OFX raw payee
                // can refer to the same merchant but rarely
                // string-match).
                && h.ProviderKey == anchor.ProviderKey
                // Match on the candidate's FEED payee too, for the same reason
                // the anchor uses it: two rows are "the same payee" when the
                // BANK called them the same thing.
                && (_db.TxnHeaderOriginals
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => o.Payee).FirstOrDefault() ?? h.Payee) == anchor.BankPayee
                && !h.NeedsReview
                && !h.IsHidden
                && h.IsMergedInto == null
            // Single-posting prior rows only (exactly 2 legs).
            where _db.TxnLegs.Count(l => l.HeaderId == h.Id) == 2
            // ...posted on the anchor's money-side account, so
            // "counterparty" means the same thing on both rows.
            where _db.TxnLegs.Any(l => l.HeaderId == h.Id
                                    && l.AccountId == anchor.SourceAccountId)
            // The candidate's OTHER leg is the suggestion: a category
            // account on an ordinary expense, a real account on a
            // transfer. The editor's AccountCategoryPicker takes both.
            from leg in _db.TxnLegs
            where leg.HeaderId == h.Id && leg.AccountId != anchor.SourceAccountId
            join account in _db.Accounts on leg.AccountId equals account.Id
            // A Holdings SUB-ACCOUNT can be the counterparty here — on a
            // brokerage, a prior settled buy's "other leg" is exactly that.
            // Such a suggestion is NOT dropped, even though "categorise it as
            // Holdings" is meaningless: the pair's other half, the PAYEE, is
            // the part worth recalling, and a settled buy is often the only
            // row that carries the name the user curated. The caller decides
            // which half it can apply (the investment editor applies the payee
            // always and the counterparty only into a category slot); throwing
            // the whole row away here to avoid an unusable half also threw away
            // the useful one.
            select new
            {
                // The suggestion IS the curated name, which is the canonical
                // value now.
                ResolvedPayee = h.Payee!,
                CounterpartyAccountId = account.Id,
                CounterpartyAccountName = account.Name,
                // The curated date, which is the canonical one now.
                PostedAt = h.PostedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => new { r.ResolvedPayee, r.CounterpartyAccountId, r.CounterpartyAccountName })
            // Drop the suggestion if it already matches THIS row's
            // saved (resolved-payee, counterparty) pair — there's
            // nothing to apply.
            .Where(g =>
                g.Key.ResolvedPayee != anchor.ResolvedPayee
                || g.Key.CounterpartyAccountId != anchor.CurrentCounterpartyAccountId)
            .Select(g => new SimilarPayeeDto(
                g.Key.ResolvedPayee,
                g.Key.CounterpartyAccountId,
                g.Key.CounterpartyAccountName,
                g.Count(),
                g.Max(r => r.PostedAt)))
            .OrderByDescending(s => s.UseCount)
            .ThenByDescending(s => s.LastUsedAt)
            .ThenBy(s => s.Payee, StringComparer.Ordinal)
            .ThenBy(s => s.CounterpartyAccountId)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Slice 2c.6d — find merge candidates for one header. A
    /// candidate is a settled row in the same ledger whose
    /// aggregated amount on the target's source account exactly
    /// matches the target's source-account amount, posted within
    /// ±7 days. Used by the editor's "Merge candidates" strip.
    ///
    /// <para>Settled = accepted (<c>needs_review=false</c>), not a
    /// loser of a prior merge (<c>is_merged_into IS NULL</c>), and not
    /// hidden. Merging into a pending row absorbs nothing the user has
    /// curated. A prior merge winner qualifies: folding another row into
    /// it keeps the graph one hop. Candidates may be manual OR
    /// bank-fed: an accepted bank-fed twin (the bank double-posted
    /// the same charge and one was already curated) is just as
    /// valid a merge target as a manual placeholder.</para>
    ///
    /// <para>Each candidate carries what its chip shows: the posted date, the
    /// resolved payee, the counterparty account of each posting (the chip
    /// names the one, or counts a split's) and the origin its tooltip names.
    /// Picking one folds the edited row into it through the merge route; the
    /// candidate keeps its own fields and nothing is copied into the
    /// editor.</para>
    ///
    /// <para>Returns empty when: the target doesn't exist or isn't
    /// in the ledger; the target has no identifiable source-side
    /// leg (no non-category-type leg); no matching settled rows.</para>
    /// </summary>
    public async Task<IReadOnlyList<MergeCandidateDto>> GetMergeCandidatesAsync(
        Guid ledgerId,
        Guid headerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // 1. Read the target's anchor data: posted_at, plus the
        //    source-side leg's account_id and the aggregated
        //    amount on that account. The "source" leg is the one
        //    whose account is NOT a category — same convention
        //    the SPA uses when it renders a row against an
        //    account view.
        // Anchor on resolved_transactions (override-aware): the target
        // must be a fresh, undecided, *effectively*-visible row, and the
        // window matches its *effective* posted_at — NOT raw txn_headers —
        // so a user-curated date/visibility is what we match on, agreeing
        // with what the register shows. Source
        // account = the non-category leg (lowest leg_index for the rare
        // multi-leg target). Ledger scope comes from the source account
        // (accounts are ledger-bound).
        var anchor = await (
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where rv.HeaderId == headerId
                && rv.NeedsReview
                && rv.IsMergedInto == null
                && !rv.IsHidden
                && rv.AccountType != "category"
            join account in _db.Accounts.AsNoTracking() on rv.AccountId equals account.Id
            where account.LedgerId == ledgerId
            orderby rv.LegIndex
            select new
            {
                SourceAccountId = rv.AccountId,
                PostedAt = rv.PostedAt,
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (anchor is null) return Array.Empty<MergeCandidateDto>();

        // 2. Effective sum-on-source-account for the target (override-
        //    aware leg amounts via the view). Splits with more than one
        //    leg on the source account aggregate naturally.
        var targetSourceAmount = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => rv.HeaderId == headerId && rv.AccountId == anchor.SourceAccountId)
            .SumAsync(rv => rv.Amount, cancellationToken)
            .ConfigureAwait(false);

        // 3. Find candidates with the same effective sum-on-source,
        //    posted (effectively) within ±7d. Read entirely through
        //    resolved_transactions so the window + visibility honour
        //    overrides: a user-curated posted_at is what the register
        //    shows, so a same-effective-day twin must be offered even
        //    when its raw posted_at is days off.
        var windowStart = anchor.PostedAt.AddDays(-7);
        var windowEnd = anchor.PostedAt.AddDays(7);

        // The ±7d window keeps the candidate set small, so we filter
        // server-side, materialise the (id, posted_at) pairs, then
        // order + Take in memory by |days delta| ASC.
        var candidates = await (
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where rv.HeaderId != headerId
                // A leg on the target's source account — both narrows the
                // per-header sum below to that account AND pins the ledger
                // (accounts are ledger-bound).
                && rv.AccountId == anchor.SourceAccountId
                // No origin filter — matching is by (account, amount,
                // date) whether the candidate is manual, bank-fed, or
                // imported. Settled-only gates (effective is_hidden):
                // only accepted, un-merged, effectively-visible rows are
                // valid. Merge-winners ARE valid candidates (folding the
                // editor into a prior winner keeps the graph one-hop).
                && !rv.NeedsReview
                && rv.IsMergedInto == null
                && !rv.IsHidden
                && rv.PostedAt >= windowStart
                && rv.PostedAt <= windowEnd
            // Group the source-account legs per header; HAVING enforces
            // the effective amount match.
            group rv by new { rv.HeaderId, rv.PostedAt } into g
            where g.Sum(x => x.Amount) == targetSourceAmount
            select new { Id = g.Key.HeaderId, g.Key.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0) return Array.Empty<MergeCandidateDto>();

        var candidateIds = candidates
            .OrderBy(c => Math.Abs((c.PostedAt - anchor.PostedAt).TotalDays))
            .ThenByDescending(c => c.PostedAt)
            .ThenBy(c => c.Id)
            .Take(limit)
            .Select(c => c.Id)
            .ToList();

        // 4. Hydrate the chosen candidates: header (with override
        //    resolution) and posting list (with counterparty account
        //    names). Keyed on the candidate id set; assembled in memory.
        // Effective header fields (payee/posted_at/origin) — one view row per
        // header carries the header-level values; Distinct collapses the
        // per-leg duplication.
        var headers = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => candidateIds.Contains(rv.HeaderId))
            .Select(rv => new
            {
                Id = rv.HeaderId,
                rv.Payee,
                rv.PostedAt,
                rv.Origin,
            })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Postings: every leg whose account_id != source account
        // becomes a "counterparty" the chip names (the source leg is
        // implied).
        var legs = await (
            from rv in _db.ResolvedTransactions.AsNoTracking()
            where candidateIds.Contains(rv.HeaderId)
                && rv.AccountId != anchor.SourceAccountId
            join a in _db.Accounts.AsNoTracking() on rv.AccountId equals a.Id
            select new
            {
                rv.HeaderId,
                PostingIndex = rv.LegIndex,
                CounterpartyAccountId = a.Id,
                CounterpartyAccountName = a.Name,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var legsByHeader = legs
            .GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(l => l.PostingIndex)
                .Select(l => new MergeCandidatePostingDto(
                    l.CounterpartyAccountId,
                    l.CounterpartyAccountName))
                .ToList() as IReadOnlyList<MergeCandidatePostingDto>);

        // Re-emit in candidateIds order so the SPA gets the
        // server-sorted list (by date-proximity then recency)
        // intact.
        var result = new List<MergeCandidateDto>(headers.Count);
        var headersById = headers.ToDictionary(h => h.Id);
        foreach (var id in candidateIds)
        {
            if (!headersById.TryGetValue(id, out var h)) continue;
            result.Add(new MergeCandidateDto(
                h.Id,
                h.Payee,
                h.PostedAt,
                legsByHeader.GetValueOrDefault(h.Id, Array.Empty<MergeCandidatePostingDto>()),
                h.Origin));
        }
        return result;
    }

    /// <summary>
    /// Apply a transaction patch atomically. Any combination of the
    /// three independent concerns the body can carry runs inside
    /// one outer Postgres transaction:
    /// <list type="bullet">
    ///   <item><b>Header edits</b> (payee, memo, posted_at,
    ///   transacted_at, check_number) → written to <c>txn_headers</c>,
    ///   with the feed's values captured to <c>txn_header_originals</c>
    ///   on the first edit (migration 230). A field the body CARRIED is
    ///   assigned, null included; a field it omitted is left alone.</item>
    ///   <item><b>Postings reshape</b> (<c>request.Postings</c>) →
    ///   reconciled against <c>txn_legs</c> per ADR-0025.</item>
    ///   <item><b>Approve</b> (slice 2c.6a) → clears
    ///   <c>needs_review</c> on the canonical header row.</item>
    /// </list>
    ///
    /// Flow is strictly <i>validate, then apply, then commit</i>:
    /// every business-rule rejection is detected up front via
    /// read-only checks against the DB; once we begin applying
    /// changes, the only way to fail is an infrastructure-level
    /// exception (which rolls the transaction back via the
    /// <c>await using</c>). No early returns mid-apply.
    /// </summary>
    /// <remarks>Balance recompute is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// final <c>SaveChangesAsync</c>. The interceptor scans every
    /// tracked mutation in this method (header-override upserts,
    /// leg reshape, merge-stamp) and recomputes every affected
    /// account. The intermediate <c>SaveChangesAsync</c> inside
    /// <see cref="ApplyPostingsReshapeAsync"/> also triggers the
    /// interceptor — each save is atomic with its own recompute.</remarks>
    public Task<PatchOutcome> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        PatchTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        PatchCoreAsync(ledgerId, headerId, request, repointTarget: null, cancellationToken);

    /// <param name="repointTarget">Set by the recategorize paths, which reuse the
    /// reshape to move legs to another account: no notes are planned or written (every
    /// kept leg is the same row, with its own memo and tags), and ownership follows the
    /// owner's legs to this account when none of them is left on it. Null for a PATCH,
    /// whose notes and ownership the write primitive plans.</param>
    private async Task<PatchOutcome> PatchCoreAsync(
        Guid ledgerId,
        Guid headerId,
        PatchTransactionRequest request,
        Guid? repointTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ledger write lock (migration 275) is the transaction's first statement, so
        // the validation reads, every save below (the reshape saves twice) and their
        // recomputes run with no other recomputing write to the ledger in flight. A
        // recategorize took it already, before its own reads; then this joins that
        // transaction and the caller commits.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // ---- VALIDATE ------------------------------------------------
        // Ledger-membership guard inside the same transaction the
        // apply step writes in — defends against a TOCTOU between
        // the endpoint's cross-ledger check and our writes. Loaded
        // tracked so the approve path can flip needs_review via
        // property assignment (batched in the single SaveChanges
        // below).
        //
        // A merged-away header is not in the ledger to an edit, an approve or a
        // recategorize (MergedAway). The merge reads it as it is: its own gate below
        // refuses a tombstone at either end, as merge-source-invalid.
        var headers = request.MergeFromHeaderId is null ? _db.TxnHeaders.NotMergedAway() : _db.TxnHeaders;
        var header = await headers
            .FirstOrDefaultAsync(h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null) return PatchResult.HeaderNotInLedger;

        // ADR-0029 — positive shape gate: this endpoint owns the
        // bank shape (action IS NULL). Investment-shape headers
        // belong on /investment-transactions; reject early so the
        // caller can route the user there.
        if (header.Action is not null) return PatchResult.HeaderNotBankShape;

        // Migration 230 — the header fields now clear on an explicit null, but
        // both date columns are NOT NULL (transacted_at since mig 189, whose
        // "no distinct tax date" is the posted date, not a null). Naming one
        // and setting it null is therefore a request the schema cannot honour;
        // say so instead of ignoring the field.
        if ((request.HasPostedAt && request.PostedAt is null)
            || (request.HasTransactedAt && request.TransactedAt is null))
        {
            return PatchResult.HeaderDateNull;
        }

        // A carried date on the stored day is not an edit (HeaderDates): the
        // editor sends midnight UTC of the picked day, and assigning it would
        // overwrite a feed row's time of day on a save that never touched the
        // date. Decided here, against the stored row, so an unchanged date
        // neither writes nor, on its own, captures originals.
        var dates = new HeaderDateEdit(
            request.HasPostedAt && HeaderDates.Changes(header.PostedAt, request.PostedAt!.Value)
                ? request.PostedAt!.Value : null,
            request.HasTransactedAt && HeaderDates.Changes(header.TransactedAt, request.TransactedAt!.Value)
                ? request.TransactedAt!.Value : null);

        PostingsReshapePlan? postingsPlan = null;
        if (request.Postings is { } postings)
        {
            var (failure, plan) = await PreparePostingsReshapeAsync(
                headerId, ledgerId, postings.SourceAccountId, postings.Items, cancellationToken)
                .ConfigureAwait(false);
            switch (failure)
            {
                case PostingsReshapeFailure.LegNotInHeader:
                    return PatchResult.PostingsLegNotInHeader;
                case PostingsReshapeFailure.DuplicateLegId:
                    return PatchResult.PostingsDuplicateLegId;
                case PostingsReshapeFailure.SourceAccountMismatch:
                    return PatchResult.PostingsSourceAccountMismatch;
                case null:
                    postingsPlan = plan;
                    break;
            }

            var (amounts, refused) = await BuildReshapeAmountsAsync(postingsPlan!, cancellationToken)
                .ConfigureAwait(false);
            if (refused is not null)
                return new PatchOutcome(PatchResult.PostingRefused, PostingRefusal: refused);
            postingsPlan = postingsPlan! with { Amounts = amounts };
        }

        // Slice 2c.6d: when a merge source is supplied, validate
        // both ends of the merge before any apply step runs. The
        // GET /merge-candidates endpoint gates on the same target
        // conditions (needs_review=true, is_merged_into=null) but
        // the API layer enforces them independently per the
        // server-side-concurrency principle — the UI's helpful
        // filtering is not the source of truth.
        //
        // Failures all surface as PatchResult.MergeSourceInvalid
        // (one 422 code; the SPA never legitimately produces them).
        TxnHeaderRow? mergeSource = null;
        if (request.MergeFromHeaderId is { } sourceId)
        {
            // Direction (post slice "merge-direction-invert"): the
            // EDITOR row becomes the loser; the candidate becomes
            // the surviving winner. The user picked a canonical row
            // in the candidates panel — that's the row that keeps
            // its identity, postings, overrides, and any prior
            // losers it already absorbed. The editor row (always a
            // fresh needs_review feed/import row per the gate
            // below) vanishes from the register; its `external_id`
            // is preserved so future syncs dedup against it.
            //
            // Editor gate: must still be a fresh needs_review row.
            // Re-folding an already-accepted row is out of scope
            // here; merging into an already-merged or effectively-
            // hidden row would mutate a tombstone. is_hidden is canonical
            // since mig 230 — there is no override layer left to resolve.
            var editorHidden = header.IsHidden;
            if (!header.NeedsReview
                || header.IsMergedInto is not null
                || editorHidden)
            {
                return PatchResult.MergeSourceInvalid;
            }

            if (sourceId == headerId)
                return PatchResult.MergeSourceInvalid;

            mergeSource = await _db.TxnHeaders
                .FirstOrDefaultAsync(h => h.Id == sourceId && h.LedgerId == ledgerId,
                    cancellationToken)
                .ConfigureAwait(false);
            // Candidate gate: must be a settled, visible row that
            // is NOT itself a loser. It may or may not already be a
            // winner of prior merges — that's the whole point of
            // the inverted direction. Allowing winners as candidates
            // lets multi-source rows (MD+ ← SimpleFIN ← OFX ← …)
            // collapse into one canonical winner without losing the
            // earlier merge work.
            if (mergeSource is null
                || mergeSource.NeedsReview
                || mergeSource.IsMergedInto is not null
                // is_hidden is canonical since mig 230.
                || mergeSource.IsHidden
                // A reminder's template is no transaction (ADR-0047): it is in no
                // register, so the candidates never offer it, and a merge into it
                // would fold a real row away into one no register shows.
                || mergeSource.IsRecurringTemplate)
            {
                return PatchResult.MergeSourceInvalid;
            }
        }

        // Notes and ownership, planned against the stored transaction before anything
        // is written, so a refusal is a clean 422. A reshape with no notes is still
        // planned, as an untouched write from the postings' source account: only the
        // originating account may reshape a split, and the plan says who owns the
        // result. A recategorize is a repoint instead (see repointTarget).
        AnnotationPlan<Guid>? notesPlan = null;
        Guid? repointOwner = null;
        if (request.Notes is not null || postingsPlan is not null)
        {
            var stored = await AnnotationWriter.LoadAsync(_db, header, cancellationToken,
                    postingsPlan?.Items.Select(i => i.CounterpartyAccountId))
                .ConfigureAwait(false);
            var after = postingsPlan is null ? stored.Shape : ShapeAfter(postingsPlan, stored);

            if (repointTarget is { } target)
            {
                repointOwner = AnnotationPlanner.Repoint(stored.Shape, after, target);
            }
            else
            {
                if (request.Notes is { } sent && postingsPlan is not null && sent.Side != postingsPlan.SourceAccountId)
                    return new PatchOutcome(PatchResult.NotesRefused, new AnnotationViolation(
                        AnnotationViolationCodes.SideNotOnTransaction,
                        "The notes must be written from the account the postings are entered from."));
                var intent = request.Notes?.Intent()
                             ?? AnnotationIntent<string>.Untouched(postingsPlan!.SourceAccountId);
                var change = postingsPlan is null
                    ? ShapeChange<Guid>.Unchanged(stored.Shape, stored.Notes)
                    : new ShapeChange<Guid>(stored.Shape, stored.Notes, after, ShapeChange<Guid>.ByLegId(stored.Shape, after));
                var resolved = await AnnotationWriter.ResolveAsync(_db, ledgerId, intent, cancellationToken)
                    .ConfigureAwait(false);
                notesPlan = AnnotationPlanner.Plan(change, resolved);
                if (!notesPlan.IsValid)
                    return new PatchOutcome(PatchResult.NotesRefused, notesPlan.Violations[0]);
            }
        }

        // ---- APPLY ---------------------------------------------------
        // From here on, no early returns. Each concern only pushes
        // changes into the EF change tracker (or, for the postings
        // shift dance, calls SaveChanges as a sub-step internal to
        // the reshape). The terminal SaveChanges + Commit at the
        // bottom is the single visible commit boundary.
        // A reshape is planned too, and can rewrite memos with no notes sent (a split
        // copies the memo to the header, a collapse joins it with the line's), so the
        // feed's memo is captured before any plan is stored.
        if (HasAnyHeaderField(request, dates) || request.Notes is not null || notesPlan is not null)
        {
            await ApplyHeaderFieldsAsync(header, request, dates, cancellationToken)
                .ConfigureAwait(false);
        }

        if (postingsPlan is not null)
        {
            await ApplyPostingsReshapeAsync(postingsPlan, cancellationToken)
                .ConfigureAwait(false);
        }

        if (notesPlan is not null)
        {
            var legsAfter = _db.TxnLegs.Local.Where(l => l.HeaderId == headerId).ToList();
            await AnnotationWriter.ApplyAsync(_db, header, legsAfter, notesPlan, cancellationToken)
                .ConfigureAwait(false);
        }
        if (repointOwner is { } followed) AnnotationWriter.ApplyRepoint(header, followed);

        if (request.Approve == true)
        {
            header.NeedsReview = false;
        }

        if (mergeSource is not null)
        {
            // Inverted direction (see editor-gate comment above):
            // editor (header) becomes loser; candidate (mergeSource)
            // becomes / stays winner. Stamp atomically with
            // everything else in the terminal SaveChanges.
            // Idempotent on the winner side — flipping an
            // already-TRUE winner is a no-op for the change tracker.
            header.IsMergedInto = mergeSource.Id;
            mergeSource.IsMergeWinner = true;
            // A merged loser is never awaiting review. Left to the SPA pairing the
            // merge with `approve: true`, the invariant would live in the caller, so
            // any other client (MCP, a direct PATCH) would merge a row and leave it
            // flagged, keeping the sidebar's review dot lit on an account whose
            // register has nothing to review. Enforced here, where the state lives,
            // so it holds for every caller. The investment branch does the same.
            header.NeedsReview = false;

            // The survivor adopts the IMPORTED (loser) row's posted date:
            // the editor row is always a fresh feed/import row (gated on
            // needs_review above), and its bank date is authoritative for
            // the merged transaction. Written straight onto the winner since
            // migration 230, with its feed date captured to
            // txn_header_originals first — so the winner's raw value is still
            // recoverable, which is what the override layer was providing.
            // `request.PostedAt` covers an in-editor date edit made on the
            // same PATCH; otherwise it's the import row's raw posted_at. Note
            // `header.PostedAt` is read AFTER ApplyHeaderFieldsAsync above, so
            // the `??` arm and the explicit arm agree on the day by construction
            // (a same-day request leaves the loser's timestamp in place; the
            // survivor still takes the request as sent).
            // A posted_at change is a header change the walks read, so the save
            // rewalks the winner's accounts and, when the winner is an investment
            // row (nothing here requires a bank one), re-sorts its positions and
            // re-derives its trade days, the one it moves to and the one it leaves —
            // same path as a normal date edit (InvestmentMergeTests holds this path
            // to it).
            var importedPostedAt = request.PostedAt ?? header.PostedAt;
            await SetPostedAtAsync(mergeSource, importedPostedAt, cancellationToken)
                .ConfigureAwait(false);

            // ADR-0082 merge → reconciling: the feed match is the bank
            // acknowledging the transaction, so on the MERGE ACCOUNT (where the
            // editor/import row has its real-account leg) the survivor's leg
            // becomes 'reconciling' — unless it is already 'cleared' (the
            // stronger, user-affirmed state wins). Only that account's leg is
            // touched; the other side of a transfer isn't bank-confirmed.
            var mergeAccountId = await _db.TxnLegs
                .Where(l => l.HeaderId == headerId)
                .Join(_db.Accounts, l => l.AccountId, a => a.Id,
                    (l, a) => new { l.AccountId, a.AccountType })
                .Where(x => x.AccountType != "category")
                .Select(x => (Guid?)x.AccountId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (mergeAccountId is { } acct)
            {
                var winnerLegId = await _db.TxnLegs
                    .Where(l => l.HeaderId == mergeSource.Id && l.AccountId == acct)
                    .Select(l => (Guid?)l.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (winnerLegId is { } legId)
                {
                    var currentStatus = await _db.TxnLegRecon
                        .Where(r => r.LegId == legId)
                        .Select(r => r.Status)
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false) ?? "uncleared";
                    if (currentStatus != "cleared")
                    {
                        await UpsertLegReconAsync(
                            ledgerId, legId, "reconciling", null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PatchResult.Ok;
    }

    /// <summary>
    /// The transaction's shape once <paramref name="plan"/> is applied: posting i is
    /// the plan's item i, a kept posting by the ids of its two stored legs, a new one
    /// with none. Legs keep their identity, which is how values follow their leg
    /// through a reorder or a recategorize.
    /// </summary>
    private static TxnShape ShapeAfter(PostingsReshapePlan plan, AnnotationWriter.Stored stored)
    {
        var legs = new List<AnnotationWriter.PlannedLeg>(plan.Items.Count * 2);
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            var kept = item.LegId is { } id && plan.SourceLegById.ContainsKey(id) ? id : (Guid?)null;
            legs.Add(new AnnotationWriter.PlannedLeg(i, plan.SourceAccountId, kept));
            legs.Add(new AnnotationWriter.PlannedLeg(i, item.CounterpartyAccountId,
                kept is { } k ? plan.CounterpartyBySourceLegId[k].Id : null));
        }
        return AnnotationWriter.Shape(stored.Header.OriginatingAccountId, stored.Header.Action, legs, stored.Facts);
    }

    /// <summary>
    /// Read-only validation of a postings reshape: confirms the
    /// supplied source account matches the existing source-side
    /// legs and that every <c>LegId</c> in <paramref name="items"/>
    /// resolves to one of those legs. Returns the prepared
    /// <see cref="PostingsReshapePlan"/> on success; the apply step
    /// consumes it without re-reading the legs.
    ///
    /// <para>The legs returned in the plan are loaded TRACKED so
    /// <see cref="ApplyPostingsReshapeAsync"/> can mutate them by
    /// property assignment and let <c>SaveChanges</c> emit the
    /// UPDATE/DELETE statements.</para>
    /// </summary>
    private async Task<(PostingsReshapeFailure? Failure, PostingsReshapePlan? Plan)>
        PreparePostingsReshapeAsync(
            Guid headerId,
            Guid ledgerId,
            Guid sourceAccountId,
            IReadOnlyList<TransactionPosting> items,
            CancellationToken cancellationToken)
    {
        // Load every existing leg on the header. Split into source-
        // side (account == sourceAccountId) and counterparty-side
        // via the schema invariant "each posting_index has exactly
        // two legs on distinct accounts."
        var existing = await _db.TxnLegs
            .Where(l => l.HeaderId == headerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sourceLegs = existing
            .Where(l => l.AccountId == sourceAccountId)
            .ToList();
        if (sourceLegs.Count == 0 || existing.Count != sourceLegs.Count * 2)
        {
            // Either no source-side legs match (SPA tried to move
            // the transaction across accounts via this endpoint),
            // or the leg-count math doesn't add up to "one source +
            // one counterparty per posting."
            return (PostingsReshapeFailure.SourceAccountMismatch, null);
        }

        var counterpartyBySourceLegId = sourceLegs.ToDictionary(
            sl => sl.Id,
            sl => existing.First(l =>
                l.PostingIndex == sl.PostingIndex && l.Id != sl.Id));
        var sourceLegById = sourceLegs.ToDictionary(l => l.Id);

        var seenLegIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (!item.LegId.HasValue) continue;
            if (!sourceLegById.ContainsKey(item.LegId.Value))
            {
                return (PostingsReshapeFailure.LegNotInHeader, null);
            }
            // Distinctness is checked here rather than left to `keepLegIds` below,
            // because that is a HashSet and would swallow the duplicate. Two items
            // sharing a legId both resolve to ONE tracked row in phase 3b: the last
            // array position wins, the earlier one writes nothing, and the header
            // silently loses a posting and gains a hole in posting_index — on a 200.
            if (!seenLegIds.Add(item.LegId.Value))
            {
                return (PostingsReshapeFailure.DuplicateLegId, null);
            }
        }

        var keepLegIds = seenLegIds;

        return (null, new PostingsReshapePlan(
            headerId, ledgerId, sourceAccountId, items,
            sourceLegs, sourceLegById, counterpartyBySourceLegId, keepLegIds));
    }

    /// <summary>
    /// Each item's two leg amounts (<see cref="PostingLegPair.Build"/>), or the first
    /// posting refused. A kept posting is checked against its stored pair, so one resent
    /// as stored, from either side, passes the nonzero and sign rules (ADR-0114 D13).
    /// </summary>
    private async Task<(IReadOnlyList<PostingLegPair.LegAmounts> Amounts, PostingLegPair.Refusal? Refused)>
        BuildReshapeAmountsAsync(PostingsReshapePlan plan, CancellationToken cancellationToken)
    {
        var sides = await PostingLegPair.LoadSidesAsync(
                _db, plan.LedgerId, plan.Items.Select(i => i.CounterpartyAccountId).Append(plan.SourceAccountId),
                cancellationToken)
            .ConfigureAwait(false);
        var stored = PostingLegPair.StoredPairs(
            plan.SourceLegs.Concat(plan.CounterpartyBySourceLegId.Values), plan.SourceAccountId);

        var amounts = new PostingLegPair.LegAmounts[plan.Items.Count];
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            PostingLegPair.StoredPair? pair = item.LegId is { } id && stored.TryGetValue(id, out var p) ? p : null;
            var (built, refused) = PostingLegPair.Build(plan.SourceAccountId, item, i, sides, pair);
            if (refused is not null) return ([], refused);
            amounts[i] = built;
        }
        return (amounts, null);
    }

    /// <summary>
    /// Execute a postings reshape against a validated plan
    /// (ADR-0025). Three phases:
    /// <list type="number">
    ///   <item>Delete the source-side leg + paired counterparty for
    ///   every posting the user dropped.</item>
    ///   <item>Shift kept legs' <c>posting_index</c> by a large
    ///   offset and flush so the final re-number in phase 3 can't
    ///   trip the UNIQUE(<c>header_id, posting_index, account_id</c>)
    ///   constraint during intermediate UPDATEs (swap-two-postings
    ///   would otherwise clash on the first UPDATE).</item>
    ///   <item>Apply the final ordering + amount / counterparty
    ///   edits and insert any fresh legs.</item>
    /// </list>
    /// </summary>
    private async Task ApplyPostingsReshapeAsync(
        PostingsReshapePlan plan,
        CancellationToken cancellationToken)
    {
        // Phase 1 — delete dropped postings.
        foreach (var sourceLeg in plan.SourceLegs)
        {
            if (plan.KeepLegIds.Contains(sourceLeg.Id)) continue;
            _db.TxnLegs.Remove(sourceLeg);
            _db.TxnLegs.Remove(plan.CounterpartyBySourceLegId[sourceLeg.Id]);
        }

        // Phase 2 — shift kept legs into a safe index range. The
        // intermediate SaveChanges is mandatory: it gets the UPDATEs
        // to the DB before the phase-3 re-number could collide on
        // the UNIQUE index.
        const int shift = 1_000_000;
        foreach (var sourceLeg in plan.SourceLegs)
        {
            if (!plan.KeepLegIds.Contains(sourceLeg.Id)) continue;
            sourceLeg.PostingIndex += shift;
            plan.CounterpartyBySourceLegId[sourceLeg.Id].PostingIndex += shift;
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Phase 3 — final ordering + insert new postings.
        //
        // No "drop overrides on kept legs" step: txn_leg_overrides went away in
        // migration 230. Nothing in the API ever wrote one, so the table was a join
        // on the hottest view and on the balance walk with no writer behind it.
        // posting_index = position in items[]. Each item's leg amounts were built before
        // the apply (plan.Amounts): the source leg's, and the counterparty's, which is the
        // source's negated unless the posting is between real accounts in two currencies.
        // A leg repointed from a category register to an account in another currency is
        // a relabel (ADR-0114 D13): the category's figure is kept, and the account's leg
        // mirrors it, read in that account's currency.
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            var amounts = plan.Amounts[i];
            if (item.LegId.HasValue && plan.SourceLegById.TryGetValue(
                item.LegId.Value, out var keep))
            {
                var counter = plan.CounterpartyBySourceLegId[keep.Id];
                keep.PostingIndex = i;
                keep.Amount = amounts.Source;
                counter.PostingIndex = i;
                counter.AccountId = item.CounterpartyAccountId;
                counter.Amount = amounts.Counterparty;
            }
            else
            {
                AddPostingLegs(plan.HeaderId, plan.LedgerId, plan.SourceAccountId, item, amounts, postingIndex: i);
            }
        }
        // Memos and tags are not touched here: a kept leg keeps its own, and the
        // write primitive's plan, applied after this, places every value.
    }

    /// <summary>
    /// The header dates a PATCH actually changes, decided against the stored row
    /// by <see cref="HeaderDates"/>: null where the request omitted the date or
    /// sent one on the stored day.
    /// </summary>
    private sealed record HeaderDateEdit(DateTime? PostedAt, DateTime? TransactedAt);

    /// <summary>
    /// Did the request CARRY any header field? Presence, not nullness
    /// (migration 230) — a body sending <c>"payee": null</c> to clear the payee
    /// is a header edit, and reading it as "no header fields" would drop it on
    /// the floor. A date counts only when it changes the day
    /// (<paramref name="dates"/>).
    /// </summary>
    private static bool HasAnyHeaderField(PatchTransactionRequest r, HeaderDateEdit dates) =>
        r.HasPayee
        || r.HasCheckNumber
        || dates.PostedAt is not null
        || dates.TransactedAt is not null;

    /// <summary>
    /// Apply the header fields the request CARRIED to the canonical row,
    /// capturing the feed's values to <c>txn_header_originals</c> first
    /// (migration 230). Called for a notes edit too, so the feed's memo is captured
    /// before the plan overwrites it.
    /// </summary>
    /// <remarks>
    /// <para>A field the body omitted is left alone; a field it carried is
    /// assigned, <c>null</c> included — that is how a payee or check
    /// number gets CLEARED, which the override layer could not express.</para>
    ///
    /// <para>The capture runs even when the assignment turns out to be a no-op
    /// (saving a row without changing anything). Making it conditional on an
    /// actual value change would mean an originals row exists only when some
    /// field currently differs, and "has been edited" would flicker off the
    /// moment the user typed a value back to what the feed said. Existence, not
    /// difference, is the signal.</para>
    ///
    /// <para>The dates are the exception, and only because the editor cannot
    /// state a time of day: a date on the stored day is the editor's
    /// representation of "unchanged", not a value the user typed, so
    /// <paramref name="dates"/> carries only the dates that change the day
    /// (<see cref="HeaderDates"/>).</para>
    ///
    /// <para>This is the same helper the investment path calls, which is the
    /// point: one capture rule, not two that agree today.</para>
    /// </remarks>
    private async Task ApplyHeaderFieldsAsync(
        TxnHeaderRow header,
        PatchTransactionRequest r,
        HeaderDateEdit dates,
        CancellationToken cancellationToken)
    {
        await AnnotationWriter.CaptureOriginalsAsync(_db, header, cancellationToken)
            .ConfigureAwait(false);

        // Normalized here rather than trusted from the request: the editors
        // disagreed about trimming and only one of them did it, so the same
        // keystrokes stored different bytes per register. See HeaderText.
        if (r.HasPayee) header.Payee = HeaderText.Normalize(r.Payee);
        if (r.HasCheckNumber) header.CheckNumber = HeaderText.Normalize(r.CheckNumber);
        // Null where the request omitted the date or sent the stored day.
        if (dates.PostedAt is { } postedAt) header.PostedAt = postedAt;
        if (dates.TransactedAt is { } transactedAt) header.TransactedAt = transactedAt;
    }

    /// <summary>
    /// Set ONLY <c>posted_at</c> on a header, capturing its original first.
    /// Used by the merge path so the surviving row adopts the imported row's
    /// date (ADR-0072 follow-up) without disturbing its other curated fields.
    /// </summary>
    private async Task SetPostedAtAsync(
        TxnHeaderRow header,
        DateTime postedAt,
        CancellationToken cancellationToken)
    {
        await AnnotationWriter.CaptureOriginalsAsync(_db, header, cancellationToken)
            .ConfigureAwait(false);
        header.PostedAt = postedAt;
    }

    /// <summary>
    /// Add one posting (a source-side leg + its counterparty) to the EF change tracker,
    /// with the amounts <see cref="PostingLegPair.Build"/> gave it: the counterparty's is
    /// the source's negated, or its own figure on a posting between real accounts in
    /// different currencies (ADR-0114 D13). Caller owns the SaveChanges + transaction
    /// commit. <paramref name="ledgerId"/> is stamped on both legs (migration 049); the DB
    /// composite FK rejects the insert if either account_id resolves to a different
    /// ledger.
    /// </summary>
    /// <remarks>
    /// Returns both legs, source first. They carry no memo and no tags: the write
    /// primitive places those once every leg of the transaction exists.
    /// </remarks>
    private TxnLegRow[] AddPostingLegs(
        Guid headerId,
        Guid ledgerId,
        Guid sourceAccountId,
        TransactionPosting posting,
        PostingLegPair.LegAmounts amounts,
        int postingIndex)
    {
        var source = new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = sourceAccountId,
            PostingIndex = postingIndex,
            Amount = amounts.Source,
        };
        var counterparty = new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = posting.CounterpartyAccountId,
            PostingIndex = postingIndex,
            Amount = amounts.Counterparty,
        };
        _db.TxnLegs.Add(source);
        _db.TxnLegs.Add(counterparty);
        return [source, counterparty];
    }

    /// <summary>
    /// Outcome of <see cref="SetReconStatusAsync"/>. <c>HeaderNotFound</c>
    /// covers both the "no such header in this ledger" and the
    /// "RLS-hidden from this user" cases — the API renders them as the
    /// same 422 since the caller has no need to distinguish.
    /// </summary>
    public enum SetReconStatusResult
    {
        Ok,
        HeaderNotFound,
    }

    /// <summary>
    /// Set the reconciliation status for one transaction ON ONE ACCOUNT
    /// (ADR-0082). Reconciliation is per-account, so the status lives on the
    /// account's leg(s) in the <c>txn_leg_recon</c> overlay — not the header.
    /// The account's leg(s) on this header get the status (usually one; a
    /// same-account split shares the row's status). The paired audit columns
    /// stay consistent with the CHECK <c>(status='cleared') ⇔ (cleared_at IS
    /// NOT NULL)</c>:
    /// <list type="bullet">
    ///   <item>any → <c>cleared</c>: set <c>cleared_at = NOW()</c>,
    ///     <c>cleared_by_user_id = currentUserId</c>.</item>
    ///   <item>any → <c>uncleared</c> / <c>reconciling</c>: clear both.</item>
    /// </list>
    /// </summary>
    /// <remarks>No balance effect — recon status doesn't shift the running
    /// balance, and <c>txn_leg_recon</c> is not watched by the recompute
    /// interceptor.</remarks>
    public async Task<SetReconStatusResult> SetReconStatusAsync(
        Guid ledgerId,
        Guid headerId,
        Guid accountId,
        string newStatus,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        // A merged-away header has no leg to set, as an id that does not exist has none
        // (MergedAway).
        var live = _db.TxnHeaders.NotMergedAway();
        var legIds = await _db.TxnLegs
            .Where(l => l.HeaderId == headerId
                && l.AccountId == accountId
                && l.LedgerId == ledgerId
                && live.Any(h => h.Id == l.HeaderId))
            .Select(l => l.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legIds.Count == 0)
            return SetReconStatusResult.HeaderNotFound;

        foreach (var legId in legIds)
        {
            await UpsertLegReconAsync(
                ledgerId, legId, newStatus, currentUserId, cancellationToken)
                .ConfigureAwait(false);
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SetReconStatusResult.Ok;
    }

    /// <summary>
    /// Upsert one leg's <c>txn_leg_recon</c> row to <paramref name="newStatus"/>,
    /// keeping the cleared audit pair consistent with the overlay CHECK
    /// (ADR-0082). Caller owns the SaveChanges. Shared by the single + bulk
    /// recon paths and the merge → reconciling rule.
    /// </summary>
    private async Task UpsertLegReconAsync(
        Guid ledgerId,
        Guid legId,
        string newStatus,
        Guid? currentUserId,
        CancellationToken cancellationToken)
    {
        var clearedAt = newStatus == "cleared" ? (DateTime?)_clock.GetUtcNow().UtcDateTime : null;
        var clearedBy = newStatus == "cleared" ? currentUserId : null;

        var existing = await _db.TxnLegRecon
            .FirstOrDefaultAsync(r => r.LegId == legId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.TxnLegRecon.Add(new TxnLegReconRow
            {
                LegId = legId,
                LedgerId = ledgerId,
                Status = newStatus,
                ClearedAt = clearedAt,
                ClearedByUserId = clearedBy,
            });
        }
        else
        {
            existing.Status = newStatus;
            existing.ClearedAt = clearedAt;
            existing.ClearedByUserId = clearedBy;
        }
    }

    /// <summary>
    /// Outcome of <see cref="DeleteAsync"/>. <c>HardDeleted</c> means
    /// the row was physically removed (cascading to legs + override
    /// rows); <c>SoftHidden</c> means <c>is_hidden=true</c> was set
    /// (preserving idempotency on re-source).
    /// </summary>
    public enum DeleteOutcome
    {
        HardDeleted,
        SoftHidden,
        HeaderNotFound,
        /// <summary>ADR-0029: header exists in the ledger but its
        /// <c>action</c> is non-null — investment shape; route the
        /// caller to <c>/investment-transactions</c>.</summary>
        HeaderNotBankShape,
    }

    /// <summary>
    /// Remove a transaction from the user-visible register. The
    /// underlying policy depends on whether the header carries an
    /// <c>external_id</c>:
    /// <list type="bullet">
    ///   <item><c>external_id IS NULL</c> (manual entries, ad-hoc
    ///     CSV rows): hard-delete the header. CASCADE drops the legs
    ///     and override rows.</item>
    ///   <item><c>external_id IS NOT NULL</c> (any feed-sourced or
    ///     import-keyed row): soft-hide via <c>is_hidden=true</c>.
    ///     A subsequent re-sync / re-import upserts back into the
    ///     same row but the importer's ON CONFLICT clause leaves
    ///     <c>is_hidden</c> alone, so the user's hide intent
    ///     survives.</item>
    /// </list>
    /// </summary>
    /// <remarks>Balance recompute is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// <c>SaveChangesAsync</c>. For the hard-delete branch, the
    /// interceptor's pre-save snapshot reads the doomed header's
    /// legs from the DB so the affected-accounts set survives the
    /// cascade, and the rows merged into the header, which the delete
    /// un-merges, are tracked first so the save recomputes them too
    /// (<see cref="MergeLosers"/>).
    /// <para>Both branches run in one transaction that takes the ledger write lock
    /// (migration 275) first: the hard delete's cascade row-locks the header's stored
    /// balances before its recompute runs, so without the lock a recompute of the same
    /// account that waits on those rows could be waited on in turn.</para></remarks>
    public async Task<DeleteOutcome> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // A merged-away header is not found (MergedAway). Deleting a winner still
        // un-merges its losers below.
        var header = await _db.TxnHeaders.NotMergedAway()
            .FirstOrDefaultAsync(
                h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
            return DeleteOutcome.HeaderNotFound;
        // ADR-0029 positive shape gate — investment headers route
        // to /investment-transactions.
        if (header.Action is not null)
            return DeleteOutcome.HeaderNotBankShape;

        // A REMINDER OCCURRENCE IS NEVER HARD-DELETED, even though it has no
        // external_id.
        //
        // The (recurring_transaction_id, occurrence_date) stamp on this row IS the slot's
        // idempotency key (mig 218). Hard-deleting takes the stamp with it, so the slot
        // reads un-acted again — and reminder-auto-post (mig 219) posts it straight back
        // on the next tick. Delete it again, it comes back: a loop the user cannot escape,
        // caused by a delete path that predates the existence of a timer.
        //
        // Soft-hiding keeps the stamp, so the slot stays consumed and the agenda keeps
        // showing it as scheduled (GetUpcomingAsync's fired-rows query does not filter
        // is_hidden). The transaction leaves the register exactly as a soft-deleted feed
        // transaction does, and the bulk unhide is the undo — no new concept, and no
        // one-way door needing an un-skip that does not exist yet. The rule is
        // TxnHeaderRow.IsDeletedOutright, which the relabel's refusal reads too.
        if (header.DeletesOutright())
        {
            // A manual row can be a merge winner, and deleting it un-merges its losers;
            // tracked first, the save recomputes them (MergeLosers).
            await MergeLosers.TrackAsync(_db, [header.Id], cancellationToken).ConfigureAwait(false);
            _db.TxnHeaders.Remove(header);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            return DeleteOutcome.HardDeleted;
        }

        // Soft-delete also clears needs_review (ADR-0052 D3): a deleted row is
        // resolved, not awaiting acceptance. Leaving it set strands the row as
        // is_hidden=true + needs_review=true — invisible in the register yet
        // still counted in the review queue (the hidden-but-pending limbo).
        header.IsHidden = true;
        header.NeedsReview = false;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return DeleteOutcome.SoftHidden;
    }
}
