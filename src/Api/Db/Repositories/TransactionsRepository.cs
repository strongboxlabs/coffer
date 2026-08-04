using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write gateway for creating new transactions. Inserts one
/// <c>txn_headers</c> row plus a symmetric posting pair into
/// <c>txn_legs</c> (ADR-0019) inside a single Postgres transaction.
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
/// </remarks>
public sealed class TransactionsRepository
{
    private readonly AppDbContext _db;

    public TransactionsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Create a manual transaction with one or more postings
    /// (ADR-0025). <c>postings.Count == 1</c> produces a
    /// single-row; <c>&gt; 1</c> produces a multi-split. All
    /// postings' source-side legs land on <paramref name="sourceAccountId"/>;
    /// each posting's counterparty leg is created on its
    /// <see cref="TransactionPosting.CounterpartyAccountId"/> with the
    /// negated amount so per-posting sum-to-zero holds. Inserts
    /// happen inside one Postgres transaction.
    /// </summary>
    /// <remarks>Balance recompute is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// <c>SaveChangesAsync</c>.</remarks>
    public async Task<Guid> CreateAsync(
        Guid ledgerId,
        Guid sourceAccountId,
        DateTime postedAt,
        string? payee,
        string? memo,
        string? checkNumber,
        DateTime? transactedAt,
        IReadOnlyList<TransactionPosting> postings,
        IReadOnlyList<string>? tags,
        // Adjust-at-post fire (ADR-0049): when set, the committed occurrence is
        // stamped to its series + slot. Null = a normal live bank create.
        Guid? recurringTransactionId = null,
        DateOnly? occurrenceDate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(postings);

        var headerId = Guid.NewGuid();

        // Reuse-from-fire (ADR-0049): join an ambient transaction (the reminder
        // fire path opened one to make the committed occurrence + catch-up
        // atomic) instead of nesting — and let that caller commit.
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        _db.TxnHeaders.Add(new TxnHeaderRow
        {
            Id = headerId,
            LedgerId = ledgerId,
            // 'manual' marks rows the user created in-app; importer
            // rows use 'moneydance_import' / 'simplefin' / etc.
            // The origin column also informs the future hard-delete
            // path (manual rows can be hard-deleted; imported rows
            // become is_hidden=true to preserve audit).
            Origin = "manual",
            Payee = payee,
            Memo = memo,
            CheckNumber = checkNumber,
            PostedAt = postedAt,
            TransactedAt = transactedAt,
            // Stamped only when the reminder fire path supplies them.
            RecurringTransactionId = recurringTransactionId,
            OccurrenceDate = occurrenceDate,
        });

        for (var i = 0; i < postings.Count; i++)
        {
            AddPostingLegs(headerId, ledgerId, sourceAccountId, postings[i], postingIndex: i);
        }

        // Slice 2c.6b: tags on create. Same create-on-first-use,
        // case-insensitive lookup as the PATCH surface — calling
        // ApplyTagsAsync against the freshly-added header keeps the
        // semantics symmetric.
        if (tags is not null && tags.Count > 0)
        {
            await ApplyTagsAsync(ledgerId, headerId, tags, cancellationToken)
                .ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (ownsTransaction)
            await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return headerId;
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
        IReadOnlySet<Guid> KeepLegIds);

    /// <summary>
    /// Outcome of <see cref="PatchAsync"/>. Each rejection code maps
    /// 1:1 to a 422 surface in the endpoint.
    /// </summary>
    public enum PatchResult
    {
        Ok,
        HeaderNotInLedger,
        PostingsLegNotInHeader,
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
        var header = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId && h.LedgerId == ledgerId)
            .Select(h => new { h.Action })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return new(RecategorizeResult.HeaderNotInLedger, null, null);
        if (header.Action is not null) return new(RecategorizeResult.HeaderNotBankShape, null, null);

        var legs = await (
            from l in _db.TxnLegs.AsNoTracking()
            where l.HeaderId == headerId && l.LedgerId == ledgerId
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            select new { l.Id, l.AccountId, l.PostingIndex, l.Amount, l.LegMemo, a.AccountType, a.Name })
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
        // category. PatchAsync owns the reshape + the derived-state recompute.
        var patch = await PatchAsync(ledgerId, headerId, new PatchTransactionRequest
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
                        LegMemo = sourceLeg.LegMemo,
                    },
                },
            },
        }, cancellationToken).ConfigureAwait(false);

        return patch == PatchResult.Ok
            ? new(RecategorizeResult.Ok, categoryLeg.Name, newCat.Name)
            : new(RecategorizeResult.ApplyFailed, categoryLeg.Name, newCat.Name);
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
        var header = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId && h.LedgerId == ledgerId)
            .Select(h => new { h.Action })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return new(SplitPostingRecategorizeResult.HeaderNotInLedger, 0, null, null);
        if (header.Action is not null) return new(SplitPostingRecategorizeResult.HeaderNotBankShape, 0, null, null);

        var legs = await (
            from l in _db.TxnLegs.AsNoTracking()
            where l.HeaderId == headerId && l.LedgerId == ledgerId
            join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
            select new { l.Id, l.AccountId, l.PostingIndex, l.Amount, l.LegMemo, a.AccountType, a.Name })
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
                LegMemo = src.LegMemo,
            });
        }

        if (dryRun)
            return new(SplitPostingRecategorizeResult.Ok, moved, fromName, toCat.Name);

        var patch = await PatchAsync(ledgerId, headerId, new PatchTransactionRequest
        {
            Postings = new PatchTransactionPostings
            {
                SourceAccountId = sourceAccountId,
                Items = items.ToArray(),
            },
        }, cancellationToken).ConfigureAwait(false);

        return patch == PatchResult.Ok
            ? new(SplitPostingRecategorizeResult.Ok, moved, fromName, toCat.Name)
            : new(SplitPostingRecategorizeResult.ApplyFailed, moved, fromName, toCat.Name);
    }

    public enum BulkSplitPostingResult { Ok, TargetNotInLedger, NoHeaders }

    public sealed record BulkSplitPostingReject(Guid HeaderId, string Reason);

    public sealed record BulkSplitPostingOutcome(
        BulkSplitPostingResult Result, int Moved, int Unchanged, string? ToCategory,
        IReadOnlyList<BulkSplitPostingReject> Rejects);

    /// <summary>
    /// Bulk <c>set_split_posting_category</c> (ADR-0068 slice E): across
    /// <paramref name="headerIds"/>, repoint every posting on fromCategory to
    /// toCategory via the per-header <see cref="RecategorizeSplitPostingsAsync"/>.
    /// Best-effort — a header that isn't a bank-shape split with a fromCategory posting
    /// is returned in <c>Rejects</c> (never blocks the rest). The whole call fails only
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
            var r = await RecategorizeSplitPostingsAsync(
                ledgerId, headerId, fromCategoryId, toCategoryId, dryRun, cancellationToken)
                .ConfigureAwait(false);
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
    /// (split / transfer / investment-transaction / not-in-ledger / apply-failed).</summary>
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
    /// (split, transfer, investment, or not-in-ledger) is REJECTED and returned in
    /// <see cref="BulkRecategorizeOutcome.Rejects"/> with a reason, so one bad row never
    /// blocks the rest. The target category is validated ONCE up front — an invalid
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
            var r = await RecategorizeAsync(ledgerId, headerId, newCategoryId, dryRun, cancellationToken)
                .ConfigureAwait(false);
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
    public enum SetTagsResult { Ok, HeadersNotInLedger }

    public sealed record SetTagsOutcome(
        SetTagsResult Result,
        int HeaderCount,
        IReadOnlyList<string> Tags,
        IReadOnlyList<Guid> UnknownHeaderIds);

    /// <summary>
    /// Replace the tag set on MANY transactions at once (ADR-0081 D6 — the deliberate
    /// bulk exception to ADR-0068 D4's one-entity-per-call rule; tagging is an
    /// idempotent replace-set on a junction table with low blast radius). Every header
    /// must belong to <paramref name="ledgerId"/>; if any doesn't, the WHOLE batch is
    /// rejected (all-or-nothing) with the offending ids, so a bad id fails loud rather
    /// than silently tagging a subset. <paramref name="tags"/> is a replace-set — the
    /// complete set to assign to each header; an empty list clears all tags. Runs in
    /// one transaction; tag resolution (create-on-first-use) happens ONCE for the batch
    /// (see <see cref="ResolveTagIdsAsync"/>). dryRun validates + reports the header
    /// count and normalized tag set without writing.
    /// </summary>
    public async Task<SetTagsOutcome> SetTransactionTagsAsync(
        Guid ledgerId,
        IReadOnlyList<Guid> headerIds,
        IReadOnlyList<string> tags,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var ids = headerIds.Distinct().ToList();
        var normalized = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .GroupBy(t => t.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        // Header-in-ledger guard for the WHOLE batch (RLS also scopes the query to the
        // caller's ledgers, so a foreign header simply doesn't resolve here). Any id
        // that doesn't resolve → reject the batch, tag nothing.
        var found = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.LedgerId == ledgerId && ids.Contains(h.Id))
            .Select(h => h.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var foundSet = found.ToHashSet();
        var unknown = ids.Where(id => !foundSet.Contains(id)).ToList();
        if (unknown.Count > 0)
            return new(SetTagsResult.HeadersNotInLedger, 0, normalized, unknown);

        if (dryRun)
            return new(SetTagsResult.Ok, ids.Count, normalized, Array.Empty<Guid>());

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Resolve target tag-ids ONCE for the batch, then diff each header against it.
        var targetTagIds = await ResolveTagIdsAsync(ledgerId, tags, cancellationToken)
            .ConfigureAwait(false);
        foreach (var headerId in ids)
            await DiffHeaderTagPairingsAsync(ledgerId, headerId, targetTagIds, cancellationToken)
                .ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SetTagsResult.Ok, ids.Count, normalized, Array.Empty<Guid>());
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
    /// current row's, then surface the <c>(payee, category)</c> pairs
    /// the user previously chose on those rows so the editor can
    /// offer one-click categorization of recurring charges.
    ///
    /// <para>Provider scope: anchored on <see
    /// cref="Entities.TxnHeaderRow.ProviderKey"/>. The anchor must
    /// have a non-null provider_key (i.e. a non-manual ingest);
    /// candidates are restricted to the same provider_key. Manual
    /// rows have NULL provider_key per the mig-107 CHECK and are
    /// excluded from both sides — recall is a feed-row concern.</para>
    ///
    /// <para>Returns empty when: the header doesn't exist or
    /// doesn't belong to the ledger; the header is a manual row
    /// (null provider_key); the bank payee is null/empty; or no
    /// matching prior rows exist.</para>
    ///
    /// <para>Only single-posting prior rows participate (exactly
    /// one category-type counterparty leg). Split-target recall is
    /// out of scope for this slice — the multi-leg structure
    /// doesn't fit the one-chip = one-pair shape.</para>
    ///
    /// <para>Aggregation: GROUP BY
    /// <c>(resolved_payee, category_account_id)</c>; sort by use
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
        //    provider_key (the recall scope), and its resolved (payee,
        //    category) so we can dedupe suggestions that already match
        //    the saved state on THIS row (no point suggesting what's
        //    already there). Anchored on the canonical payee, not the
        //    override: the override is what we're trying to RECALL,
        //    not the key we search by. Manual rows (null provider_key)
        //    are excluded — recall is a feed-row concern, and the
        //    candidate scope below would have nothing to match against
        //    anyway.
        var anchor = await (
            from h in _db.TxnHeaders.AsNoTracking()
            where h.Id == headerId && h.LedgerId == ledgerId
                && h.ProviderKey != null
                && h.Payee != null && h.Payee != ""
                // Accept-flow gates — match merge-candidates'
                // target-side validation. Server enforces these
                // independent of the SPA's UI filtering per the
                // server-side-concurrency principle.
                && h.NeedsReview
                // Effective visibility (override-aware), not raw is_hidden.
                && (_db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (bool?)o.IsHidden).FirstOrDefault() ?? h.IsHidden) == false
                && h.IsMergedInto == null
            from cpLeg in _db.TxnLegs
            where cpLeg.HeaderId == h.Id
            join cpAccount in _db.Accounts on cpLeg.AccountId equals cpAccount.Id
            where cpAccount.AccountType == "category"
            let overridePayee = _db.TxnHeaderOverrides
                .Where(o => o.HeaderId == h.Id)
                .Select(o => o.Payee)
                .FirstOrDefault()
            select new
            {
                BankPayee = h.Payee!,
                ProviderKey = h.ProviderKey!,
                ResolvedPayee = overridePayee ?? h.Payee!,
                CurrentCategoryAccountId = (Guid?)cpAccount.Id,
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
                && h.Payee == anchor.BankPayee
                && !h.NeedsReview
                // Effective visibility (override-aware), not raw is_hidden.
                && (_db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (bool?)o.IsHidden).FirstOrDefault() ?? h.IsHidden) == false
                && h.IsMergedInto == null
            // Single-posting prior rows only (exactly 2 legs).
            where _db.TxnLegs.Count(l => l.HeaderId == h.Id) == 2
            // Find the category-type counterparty leg on this row.
            from leg in _db.TxnLegs
            where leg.HeaderId == h.Id
            join account in _db.Accounts on leg.AccountId equals account.Id
            where account.AccountType == "category"
            // Override.payee falls back to the raw bank payee.
            let overridePayee = _db.TxnHeaderOverrides
                .Where(o => o.HeaderId == h.Id)
                .Select(o => o.Payee)
                .FirstOrDefault()
            select new
            {
                ResolvedPayee = overridePayee ?? h.Payee!,
                CategoryAccountId = account.Id,
                CategoryAccountName = account.Name,
                // Effective posted_at so the "last used" recency reflects
                // the curated date, not the raw feed date.
                PostedAt = _db.TxnHeaderOverrides
                    .Where(o => o.HeaderId == h.Id)
                    .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => new { r.ResolvedPayee, r.CategoryAccountId, r.CategoryAccountName })
            // Drop the suggestion if it already matches THIS row's
            // saved (resolved-payee, category) pair — there's
            // nothing to apply.
            .Where(g =>
                g.Key.ResolvedPayee != anchor.ResolvedPayee
                || g.Key.CategoryAccountId != anchor.CurrentCategoryAccountId)
            .Select(g => new SimilarPayeeDto(
                g.Key.ResolvedPayee,
                g.Key.CategoryAccountId,
                g.Key.CategoryAccountName,
                g.Count(),
                g.Max(r => r.PostedAt)))
            .OrderByDescending(s => s.UseCount)
            .ThenByDescending(s => s.LastUsedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Slice 2c.6d — find merge candidates for one header. A
    /// candidate is a settled row in the same ledger whose
    /// aggregated amount on the target's source account exactly
    /// matches the target's source-account amount, posted within
    /// ±7 days. Used by the editor's "Possible matches" panel.
    ///
    /// <para>Settled = accepted (<c>needs_review=false</c>), not a
    /// loser of a prior merge (<c>is_merged_into IS NULL</c>), not
    /// already a winner of a prior merge
    /// (<c>is_merge_winner=false</c>), and not hidden. Merging into
    /// a pending row absorbs nothing the user has curated; merging
    /// into an already-won row creates a two-loser graph no UI
    /// surface traverses today. Candidates may be manual OR
    /// bank-fed: an accepted bank-fed twin (the bank double-posted
    /// the same charge and one was already curated) is just as
    /// valid a merge target as a manual placeholder.</para>
    ///
    /// <para>The candidate's posting list and tags come back
    /// pre-resolved so clicking a candidate can pre-fill the
    /// editor without an additional round-trip.</para>
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
        // with what the register shows (the bug this fix closes). Source
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
        //    when its raw posted_at is days off (the bug this fixes).
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
            .Take(limit)
            .Select(c => c.Id)
            .ToList();

        // 4. Hydrate the chosen candidates: header (with override
        //    resolution), posting list (with counterparty account
        //    names), tags. Three parallel reads keyed on the
        //    candidate id set; assembled in memory.
        // Effective header fields (payee/memo/posted_at) — one view row
        // per header carries the header-level values; Distinct collapses
        // the per-leg duplication.
        var headers = await _db.ResolvedTransactions.AsNoTracking()
            .Where(rv => candidateIds.Contains(rv.HeaderId))
            .Select(rv => new
            {
                Id = rv.HeaderId,
                rv.Payee,
                Memo = rv.HeaderMemo,
                rv.PostedAt,
            })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Postings: every leg whose account_id != source account
        // becomes a "counterparty" in the editor-pre-fill shape (the
        // source leg is implied). Effective amount + leg memo via the view.
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
                rv.Amount,
                rv.LegMemo,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var tagsByHeader = await (
            from p in _db.TxnHeaderTags.AsNoTracking()
            where candidateIds.Contains(p.HeaderId)
            join t in _db.Tags.AsNoTracking() on p.TagId equals t.Id
            select new { p.HeaderId, t.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var legsByHeader = legs
            .GroupBy(l => l.HeaderId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(l => l.PostingIndex)
                .Select(l => new MergeCandidatePostingDto(
                    l.CounterpartyAccountId,
                    l.CounterpartyAccountName,
                    l.Amount,
                    l.LegMemo))
                .ToList() as IReadOnlyList<MergeCandidatePostingDto>);
        var tagsByHeaderId = tagsByHeader
            .GroupBy(t => t.HeaderId)
            .ToDictionary(g => g.Key, g => g
                .Select(t => t.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList() as IReadOnlyList<string>);

        // Re-emit in candidateIds order so the SPA gets the
        // server-sorted list (by date-proximity then recency)
        // intact.
        var result = new List<MergeCandidateDto>(headers.Count);
        var headersById = headers.ToDictionary(h => h.Id);
        foreach (var id in candidateIds)
        {
            if (!headersById.TryGetValue(id, out var h)) continue;
            var daysDelta = (int)Math.Round((h.PostedAt - anchor.PostedAt).TotalDays);
            result.Add(new MergeCandidateDto(
                h.Id,
                h.Payee,
                h.Memo,
                h.PostedAt,
                daysDelta,
                tagsByHeaderId.GetValueOrDefault(h.Id, Array.Empty<string>()),
                legsByHeader.GetValueOrDefault(h.Id, Array.Empty<MergeCandidatePostingDto>())));
        }
        return result;
    }

    /// <summary>
    /// Apply a transaction patch atomically. Any combination of the
    /// three independent concerns the body can carry runs inside
    /// one outer Postgres transaction:
    /// <list type="bullet">
    ///   <item><b>Header-override edits</b> (payee, memo, posted_at,
    ///   transacted_at, check_number) → upserted into
    ///   <c>txn_header_overrides</c> per ADR-0003.</item>
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
    public async Task<PatchResult> PatchAsync(
        Guid ledgerId,
        Guid headerId,
        PatchTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // ---- VALIDATE ------------------------------------------------
        // Ledger-membership guard inside the same transaction the
        // apply step writes in — defends against a TOCTOU between
        // the endpoint's cross-ledger check and our writes. Loaded
        // tracked so the approve path can flip needs_review via
        // property assignment (batched in the single SaveChanges
        // below).
        var header = await _db.TxnHeaders
            .FirstOrDefaultAsync(h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null) return PatchResult.HeaderNotInLedger;

        // ADR-0029 — positive shape gate: this endpoint owns the
        // bank shape (action IS NULL). Investment-shape headers
        // belong on /investment-transactions; reject early so the
        // caller can route the user there.
        if (header.Action is not null) return PatchResult.HeaderNotBankShape;

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
                case PostingsReshapeFailure.SourceAccountMismatch:
                    return PatchResult.PostingsSourceAccountMismatch;
                case null:
                    postingsPlan = plan;
                    break;
            }
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
            // hidden row would mutate a tombstone. Visibility is the
            // override-aware effective value, matching the read side.
            var editorHidden = await _db.TxnHeaderOverrides
                .Where(o => o.HeaderId == headerId)
                .Select(o => (bool?)o.IsHidden).FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false) ?? header.IsHidden;
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
            var mergeSourceHidden = mergeSource is not null
                && (await _db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == sourceId)
                        .Select(o => (bool?)o.IsHidden).FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false) ?? mergeSource.IsHidden);
            if (mergeSource is null
                || mergeSource.NeedsReview
                || mergeSource.IsMergedInto is not null
                || mergeSourceHidden)
            {
                return PatchResult.MergeSourceInvalid;
            }
        }

        // ---- APPLY ---------------------------------------------------
        // From here on, no early returns. Each concern only pushes
        // changes into the EF change tracker (or, for the postings
        // shift dance, calls SaveChanges as a sub-step internal to
        // the reshape). The terminal SaveChanges + Commit at the
        // bottom is the single visible commit boundary.
        if (HasAnyHeaderField(request))
        {
            await UpsertHeaderOverrideAsync(ledgerId, headerId, request, cancellationToken)
                .ConfigureAwait(false);
        }

        if (postingsPlan is not null)
        {
            await ApplyPostingsReshapeAsync(postingsPlan, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Approve == true)
        {
            header.NeedsReview = false;
        }

        if (request.Tags is { } tags)
        {
            await ApplyTagsAsync(ledgerId, headerId, tags, cancellationToken)
                .ConfigureAwait(false);
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

            // The survivor adopts the IMPORTED (loser) row's posted date:
            // the editor row is always a fresh feed/import row (gated on
            // needs_review above), and its bank date is authoritative for
            // the merged transaction. Stamped as a posted_at override on
            // the winner (ADR-0003 — a curated change lives in the
            // override layer, leaving the winner's raw feed value intact).
            // `request.PostedAt` covers an in-editor date edit made on the
            // same PATCH; otherwise it's the import row's raw posted_at.
            // A posted_at override change is balance-relevant, so the
            // recompute interceptor rewalks the winner's account on save —
            // same path as a normal date edit.
            var importedPostedAt = request.PostedAt ?? header.PostedAt;
            await SetPostedAtOverrideAsync(
                ledgerId, mergeSource.Id, importedPostedAt, cancellationToken)
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PatchResult.Ok;
    }

    /// <summary>
    /// Replace the tag set on a header with the supplied names
    /// (slice 2c.6b). Idempotent; create-on-first-use within the
    /// ledger; case-insensitive match, first user-supplied casing
    /// wins on insert.
    ///
    /// <para>Diff-against-current: existing pairings whose tag is
    /// not in the new set are removed; new pairings are added. Tag
    /// rows are inserted into the ledger dictionary only when no
    /// existing tag matches (lower-case comparison) — orphan tags
    /// from prior removals stay in the dictionary (they may be
    /// referenced by other transactions; dictionary cleanup is a
    /// separate concern).</para>
    /// </summary>
    private async Task ApplyTagsAsync(
        Guid ledgerId,
        Guid headerId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var targetTagIds = await ResolveTagIdsAsync(ledgerId, tags, cancellationToken)
            .ConfigureAwait(false);
        await DiffHeaderTagPairingsAsync(ledgerId, headerId, targetTagIds, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve tag NAMES to their ids within the ledger's dictionary, creating a row
    /// on first use (tracked; INSERTed on the next SaveChanges). Normalizes first:
    /// trim, drop empties, case-insensitive dedupe with the first user casing winning
    /// on insert.
    ///
    /// <para>Resolve ONCE per unit of work. The bulk path
    /// (<see cref="SetTransactionTagsAsync"/>) calls this a single time for the whole
    /// batch, so if two headers both introduce the same brand-new name it maps to ONE
    /// dictionary row — a per-header re-query would miss the prior iteration's
    /// tracker-pending insert and create a duplicate.</para>
    /// </summary>
    private async Task<HashSet<Guid>> ResolveTagIdsAsync(
        Guid ledgerId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var distinct = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .GroupBy(t => t.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
        var distinctLower = distinct
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        // Resolve every requested tag against the ledger's dictionary (one round
        // trip) — anything that doesn't come back is inserted on first use.
        var existing = await _db.Tags
            .Where(t => t.LedgerId == ledgerId
                        && distinctLower.Contains(t.Name.ToLower()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingByLower = existing.ToDictionary(
            t => t.Name.ToLowerInvariant(),
            t => t);

        var targetTagIds = new HashSet<Guid>();
        foreach (var name in distinct)
        {
            var lower = name.ToLowerInvariant();
            if (existingByLower.TryGetValue(lower, out var match))
            {
                targetTagIds.Add(match.Id);
            }
            else
            {
                var fresh = new TagRow
                {
                    Id = Guid.NewGuid(),
                    LedgerId = ledgerId,
                    Name = name, // preserve user casing on first use
                };
                _db.Tags.Add(fresh);
                targetTagIds.Add(fresh.Id);
            }
        }
        return targetTagIds;
    }

    /// <summary>
    /// Replace ONE header's tag pairings with <paramref name="targetTagIds"/>
    /// (diff-against-current: drop pairings not in the target, add the missing ones).
    /// Idempotent; only pushes changes into the change tracker — the caller owns the
    /// SaveChanges / commit boundary.
    /// </summary>
    private async Task DiffHeaderTagPairingsAsync(
        Guid ledgerId,
        Guid headerId,
        HashSet<Guid> targetTagIds,
        CancellationToken cancellationToken)
    {
        var current = await _db.TxnHeaderTags
            .Where(t => t.HeaderId == headerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentTagIds = current.Select(t => t.TagId).ToHashSet();

        foreach (var existingPair in current)
        {
            if (!targetTagIds.Contains(existingPair.TagId))
                _db.TxnHeaderTags.Remove(existingPair);
        }
        foreach (var targetTagId in targetTagIds)
        {
            if (!currentTagIds.Contains(targetTagId))
            {
                _db.TxnHeaderTags.Add(new TxnHeaderTagRow
                {
                    HeaderId = headerId,
                    TagId = targetTagId,
                    LedgerId = ledgerId,
                });
            }
        }
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

        foreach (var item in items)
        {
            if (item.LegId.HasValue && !sourceLegById.ContainsKey(item.LegId.Value))
            {
                return (PostingsReshapeFailure.LegNotInHeader, null);
            }
        }

        var keepLegIds = items
            .Where(i => i.LegId.HasValue)
            .Select(i => i.LegId!.Value)
            .ToHashSet();

        return (null, new PostingsReshapePlan(
            headerId, ledgerId, sourceAccountId, items,
            sourceLegs, sourceLegById, counterpartyBySourceLegId, keepLegIds));
    }

    /// <summary>
    /// Execute a postings reshape against a validated plan
    /// (ADR-0025). Three phases:
    /// <list type="number">
    ///   <item>Delete the source-side leg + paired counterparty for
    ///   every posting the user dropped. Cascades to
    ///   <c>txn_leg_overrides</c> via FK ON DELETE CASCADE.</item>
    ///   <item>Shift kept legs' <c>posting_index</c> by a large
    ///   offset and flush so the final re-number in phase 3 can't
    ///   trip the UNIQUE(<c>header_id, posting_index, account_id</c>)
    ///   constraint during intermediate UPDATEs (swap-two-postings
    ///   would otherwise clash on the first UPDATE).</item>
    ///   <item>Drop overrides on kept legs (canonical re-save
    ///   supersedes the override layer per ADR-0003), then apply
    ///   the final ordering + amount / counterparty edits and
    ///   insert any fresh legs.</item>
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

        // Phase 3a — drop overrides on kept legs.
        if (plan.KeepLegIds.Count > 0)
        {
            var keepCounterLegIds = plan.KeepLegIds
                .Select(id => plan.CounterpartyBySourceLegId[id].Id)
                .ToArray();
            var allKeptLegIds = plan.KeepLegIds.Concat(keepCounterLegIds).ToArray();
            await _db.TxnLegOverrides
                .Where(o => allKeptLegIds.Contains(o.LegId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // Phase 3b — final ordering + insert new postings.
        // posting_index = position in items[].
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (item.LegId.HasValue && plan.SourceLegById.TryGetValue(
                item.LegId.Value, out var keep))
            {
                var counter = plan.CounterpartyBySourceLegId[keep.Id];
                keep.PostingIndex = i;
                keep.Amount = item.Amount;
                keep.LegMemo = item.LegMemo;
                counter.PostingIndex = i;
                counter.AccountId = item.CounterpartyAccountId;
                counter.Amount = -item.Amount;
            }
            else
            {
                AddPostingLegs(plan.HeaderId, plan.LedgerId, plan.SourceAccountId, item, postingIndex: i);
            }
        }
    }

    private static bool HasAnyHeaderField(PatchTransactionRequest r) =>
        r.Payee is not null
        || r.Memo is not null
        || r.CheckNumber is not null
        || r.PostedAt is not null
        || r.TransactedAt is not null;

    /// <summary>
    /// Upsert the header-override row with the supplied non-null
    /// fields (ADR-0003). Null on a request field means "leave that
    /// column alone."
    /// </summary>
    private async Task UpsertHeaderOverrideAsync(
        Guid ledgerId,
        Guid headerId,
        PatchTransactionRequest r,
        CancellationToken cancellationToken)
    {
        var existing = await _db.TxnHeaderOverrides
            .FirstOrDefaultAsync(o => o.HeaderId == headerId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.TxnHeaderOverrides.Add(new TxnHeaderOverrideRow
            {
                HeaderId = headerId,
                LedgerId = ledgerId,
                Payee = r.Payee,
                Memo = r.Memo,
                CheckNumber = r.CheckNumber,
                PostedAt = r.PostedAt,
                TransactedAt = r.TransactedAt,
            });
        }
        else
        {
            var rowUpdate = new TxnHeaderOverrideRow
            {
                HeaderId = headerId,
                LedgerId = ledgerId,
                Payee = r.Payee ?? existing.Payee,
                Memo = r.Memo ?? existing.Memo,
                CheckNumber = r.CheckNumber ?? existing.CheckNumber,
                PostedAt = r.PostedAt ?? existing.PostedAt,
                TransactedAt = r.TransactedAt ?? existing.TransactedAt,
                IsHidden = existing.IsHidden,
            };
            _db.Entry(existing).CurrentValues.SetValues(rowUpdate);
        }
    }

    /// <summary>
    /// Set ONLY the <c>posted_at</c> override on a header, preserving any
    /// other override fields (payee / memo / check# / transacted_at /
    /// is_hidden). Used by the merge path so the surviving row adopts the
    /// imported row's date (ADR-0072 follow-up) without disturbing the
    /// winner's other curated fields.
    /// </summary>
    private async Task SetPostedAtOverrideAsync(
        Guid ledgerId,
        Guid headerId,
        DateTime postedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _db.TxnHeaderOverrides
            .FirstOrDefaultAsync(o => o.HeaderId == headerId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.TxnHeaderOverrides.Add(new TxnHeaderOverrideRow
            {
                HeaderId = headerId,
                LedgerId = ledgerId,
                PostedAt = postedAt,
            });
        }
        else
        {
            var rowUpdate = new TxnHeaderOverrideRow
            {
                HeaderId = headerId,
                LedgerId = ledgerId,
                Payee = existing.Payee,
                Memo = existing.Memo,
                CheckNumber = existing.CheckNumber,
                PostedAt = postedAt,
                TransactedAt = existing.TransactedAt,
                IsHidden = existing.IsHidden,
            };
            _db.Entry(existing).CurrentValues.SetValues(rowUpdate);
        }
    }

    /// <summary>
    /// Add one posting (a source-side leg + its negated
    /// counterparty) to the EF change tracker. Caller owns the
    /// SaveChanges + transaction commit. <paramref name="ledgerId"/>
    /// is stamped on both legs (migration 049); the DB composite FK
    /// rejects the insert if either account_id resolves to a
    /// different ledger.
    /// </summary>
    private void AddPostingLegs(
        Guid headerId,
        Guid ledgerId,
        Guid sourceAccountId,
        TransactionPosting posting,
        int postingIndex)
    {
        _db.TxnLegs.Add(new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = sourceAccountId,
            PostingIndex = postingIndex,
            Amount = posting.Amount,
            LegMemo = posting.LegMemo,
        });
        _db.TxnLegs.Add(new TxnLegRow
        {
            Id = Guid.NewGuid(),
            HeaderId = headerId,
            LedgerId = ledgerId,
            AccountId = posting.CounterpartyAccountId,
            PostingIndex = postingIndex,
            Amount = -posting.Amount,
        });
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
        var legIds = await _db.TxnLegs
            .Where(l => l.HeaderId == headerId
                && l.AccountId == accountId
                && l.LedgerId == ledgerId)
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
        var clearedAt = newStatus == "cleared" ? (DateTime?)DateTime.UtcNow : null;
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
    /// cascade.</remarks>
    public async Task<DeleteOutcome> DeleteAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        var header = await _db.TxnHeaders
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

        if (header.ExternalId is null)
        {
            _db.TxnHeaders.Remove(header);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DeleteOutcome.HardDeleted;
        }

        // Soft-delete also clears needs_review (ADR-0052 D3): a deleted row is
        // resolved, not awaiting acceptance. Leaving it set strands the row as
        // is_hidden=true + needs_review=true — invisible in the register yet
        // still counted in the review queue (the hidden-but-pending limbo).
        header.IsHidden = true;
        header.NeedsReview = false;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DeleteOutcome.SoftHidden;
    }

    /// <summary>Outcome of <see cref="UnhideAsync"/>.</summary>
    public enum UnhideOutcome { HeaderNotFound, NotHidden, Unhidden }

    /// <summary>
    /// Un-hide a soft-hidden transaction (ADR-0072 D2): flip <c>is_hidden</c>
    /// back to false so the row re-enters the register + the balance walk. The
    /// recompute interceptor fires on SaveChanges (is_hidden is balance-relevant,
    /// mig 103). Idempotent — a row that isn't hidden is a NotHidden no-op.
    /// </summary>
    public async Task<UnhideOutcome> UnhideAsync(
        Guid ledgerId,
        Guid headerId,
        CancellationToken cancellationToken = default)
    {
        var header = await _db.TxnHeaders
            .FirstOrDefaultAsync(
                h => h.Id == headerId && h.LedgerId == ledgerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (header is null) return UnhideOutcome.HeaderNotFound;
        if (!header.IsHidden) return UnhideOutcome.NotHidden;

        header.IsHidden = false;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UnhideOutcome.Unhidden;
    }
}
