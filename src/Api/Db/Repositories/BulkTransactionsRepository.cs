using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Write gateway for register-scale bulk operations (ADR-0024).
/// Resolves <see cref="SelectionRequest"/> into a header-level LINQ
/// predicate, then issues one Postgres statement per operation via EF
/// <c>ExecuteUpdateAsync</c> / <c>ExecuteDeleteAsync</c>. Atomic in
/// one transaction; no row-by-row round-trips even when the
/// selection covers tens of thousands of headers.
/// </summary>
/// <remarks>
/// <para>Distinct from <see cref="TransactionsRepository"/> (single-
/// header writes) because the surface and the SQL shape are different
/// — the single-row endpoints stay simple, the bulk endpoints live
/// here. Both paths hit the same RLS-bound <see cref="AppDbContext"/>
/// (coffer_app role), so the predicate's ledger guard relies on the
/// session's <c>app.user_id</c> grant filtering inaccessible rows
/// out before the UPDATE/DELETE applies.</para>
///
/// <para>The bulk-recon-status path is all-or-nothing inside one
/// <c>UPDATE</c> — Postgres applies it as a single atomic statement
/// (ADR-0024 §"Partial failure"). The bulk-delete path splits hard-
/// delete vs soft-hide along the <c>external_id IS NULL</c> seam,
/// running both branches inside one EF transaction so the user's
/// "Delete N rows" stays atomic even when N spans the two policies.
/// </para>
///
/// <para><b>Balance + holdings recompute</b>. <c>ExecuteUpdateAsync</c> /
/// <c>ExecuteDeleteAsync</c> BYPASS the <c>ChangeTracker</c>, so BOTH
/// derived-data interceptors (<see cref="LegDerivedRecomputeInterceptor"/>
/// for balances and <see cref="HoldingsRecomputeInterceptor"/> for
/// holdings/lots) are blind to these writes. Every content-mutating bulk
/// method below therefore invokes <see cref="LegDerivedRecomputeService"/>
/// AND <see cref="HoldingsRecomputeService"/> explicitly after the
/// statement runs — the one place in the API where the call-site pattern
/// (#4) applies because of EF's bulk-method semantics. The balance anchor
/// is the EFFECTIVE date (<c>COALESCE(override.posted_at, header.posted_at)</c>)
/// to match the recompute function's walk; the holdings recompute targets
/// the (account, security) pairs of any investment-shape legs in the
/// selection. Status-only bulk paths (recon-status) touch no
/// balance/holdings-relevant column and skip both recompute calls.</para>
///
/// <para><b>Posting counts</b> (mig 120) are NOT recomputed here:
/// the recon-status path is status-only (no leg structure change),
/// and bulk-delete removes / hides WHOLE headers (a vanished or
/// hidden header has no surviving legs whose counts could be stale,
/// and other headers are untouched). No posting-count recompute
/// call is needed on either path.</para>
/// </remarks>
internal sealed class BulkTransactionsRepository
{
    private readonly AppDbContext _db;
    private readonly LegDerivedRecomputeService _balances;
    private readonly HoldingsRecomputeService _holdings;

    public BulkTransactionsRepository(
        AppDbContext db,
        LegDerivedRecomputeService balances,
        HoldingsRecomputeService holdings)
    {
        _db = db;
        _balances = balances;
        _holdings = holdings;
    }

    /// <summary>
    /// Build the IQueryable that materialises the user's
    /// selection over <see cref="AppDbContext.TxnHeaders"/>. Applied
    /// uniformly across summary / bulk-update / bulk-delete so the
    /// three operations see exactly the same row set.
    /// </summary>
    /// <remarks>
    /// Returns null when the selection is structurally empty — an
    /// "explicit" mode with no ids passed. The caller surfaces this
    /// as the <c>selection-empty</c> 422 rather than firing a noop
    /// UPDATE.
    /// </remarks>
    private IQueryable<TxnHeaderRow>? BuildSelectionQuery(
        Guid ledgerId,
        SelectionRequest selection)
    {
        // Ledger scope is always enforced — RLS will also block
        // cross-ledger rows, but keying the LINQ predicate on
        // ledger_id keeps the query plan tight (and survives if the
        // app.user_id session var is ever misconfigured).
        IQueryable<TxnHeaderRow> q = _db.TxnHeaders
            .Where(h => h.LedgerId == ledgerId && h.IsMergedInto == null);

        if (selection.Kind == "explicit")
        {
            // Explicit = exactly the header ids the user checked, regardless of
            // visibility. Hidden-view rows carry is_hidden=true; applying the
            // all-mode visibility scope here (an explicit selection has no
            // statusFilter, so the scope would default to visible-only) would
            // drop them from the summary + bulk ops — the "check a hidden row
            // and the bulk bar vanishes" bug (ADR-0072 D1). The ids already come
            // from what the user sees, so no visibility filter is needed.
            if (selection.HeaderIds.Count == 0) return null;
            var ids = selection.HeaderIds.ToArray();
            q = q.Where(h => EF.Constant(ids).Contains(h.Id));
            return q;
        }

        // "all" mode — predicate over the current view filter, including the
        // visibility scope (ADR-0072 D1): the "hidden" filter selects the hidden
        // recovery view; every other filter selects visible rows. Effective
        // visibility (override-aware), not raw is_hidden — the selection set
        // must match what the register shows.
        var hiddenScope = string.Equals(selection.StatusFilter, "hidden", StringComparison.Ordinal);
        q = q.Where(h => (_db.TxnHeaderOverrides
                .Where(o => o.HeaderId == h.Id)
                .Select(o => (bool?)o.IsHidden).FirstOrDefault() ?? h.IsHidden) == hiddenScope);

        // Reconciliation status is per-account (ADR-0082): the cleared /
        // uncleared cases below evaluate the account's own leg. The register
        // always scopes those filters to an account; when it isn't set the
        // per-account predicate matches nothing (defensive — no wrong select).
        var scopeAccountId = selection.AccountId;

        if (selection.AccountId.HasValue)
        {
            var accountId = selection.AccountId.Value;
            // Restrict to headers the account ORIGINATES (ADR-0036), not
            // merely touches: the header has a leg on `accountId` whose
            // denormalized posting counts (migration 120) show the account
            // is touched by EVERY posting of the header
            // (account_postings_on_header == header_total_postings).
            //
            // A header where account_postings_on_header <
            // header_total_postings is a target-split — its canonical
            // owner is another account, and deleting / re-statusing it
            // from THIS account's register would wrongly mutate the
            // owning account's transaction (ADR-0036 read-only rule).
            // Using the denormalized counts keeps this an Any()/EXISTS
            // (cheap), not a per-row COUNT(DISTINCT) subquery.
            //
            // Applying it uniformly here makes all-mode summary +
            // recon-status + delete act ONLY on headers this account
            // owns: the typed-confirm count the user sees matches exactly
            // what gets deleted, and bulk ops can never reach a header
            // owned by another account. That server-side guarantee is
            // what lets the SPA drop its all-mode delete gate — the
            // client no longer has to audit unloaded rows for read-only
            // target-splits. (The 'explicit' branch above stays
            // unchanged: explicit selections are audited row-by-row on
            // the client for read-only rows before they reach here.)
            q = q.Where(h => _db.TxnLegs.Any(
                l => l.HeaderId == h.Id
                    && l.AccountId == accountId
                    && l.AccountPostingsOnHeader == l.HeaderTotalPostings));
        }

        switch (selection.StatusFilter)
        {
            case "all":
                // No status restriction.
                break;
            case "cleared":
                // Per-account (ADR-0082): the account's leg is cleared.
                q = q.Where(h => _db.TxnLegs.Any(l =>
                    l.HeaderId == h.Id && l.AccountId == scopeAccountId
                    && _db.TxnLegRecon.Any(r => r.LegId == l.Id && r.Status == "cleared")));
                break;
            case "uncleared":
                // Per-account (ADR-0082): the account's leg is uncleared — no
                // recon row, or one that is neither cleared nor reconciling — and
                // not future-dated. Reconciling is its own case below (mig 164/165),
                // so this matches the register's "Uncleared" view exactly.
                q = q.Where(h =>
                    _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == scopeAccountId
                        && !_db.TxnLegRecon.Any(r => r.LegId == l.Id
                            && (r.Status == "cleared" || r.Status == "reconciling")))
                    && (_db.TxnHeaderOverrides
                            .Where(o => o.HeaderId == h.Id)
                            .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt) <= DateTime.UtcNow);
                break;
            case "reconciling":
                // Per-account (ADR-0082): the account's leg is reconciling (mig 165),
                // not future-dated. Mirrors the register's Reconciling view.
                q = q.Where(h =>
                    _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == scopeAccountId
                        && _db.TxnLegRecon.Any(r => r.LegId == l.Id && r.Status == "reconciling"))
                    && (_db.TxnHeaderOverrides
                            .Where(o => o.HeaderId == h.Id)
                            .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt) <= DateTime.UtcNow);
                break;
            case "scheduled":
                q = q.Where(h => (_db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt) > DateTime.UtcNow);
                break;
            case "needs_review":
                // The bank-feed review FLAG (migration 037, ADR-0031
                // Phase 3c) — a separate dimension from the recon
                // status, mirroring the register's "Needs review" tab
                // (passesStatusFilter → txn.needsReview). Without this
                // case a select-all on that tab fell back to "all" and
                // silently widened the selection to the whole account.
                q = q.Where(h => h.NeedsReview);
                break;
            case "hidden":
                // Visibility scope, not a recon status — the base predicate above
                // already restricted to hidden rows (ADR-0072 D1). No further
                // status restriction.
                break;
            default:
                // Filtered out at endpoint validation, but defensive:
                // an unknown filter shouldn't silently match
                // everything.
                return q.Where(_ => false);
        }

        // Structured/search filter (mig 164/167): narrow the 'all' selection to
        // headers with a matching leg, so a select-all under an active filter
        // covers exactly what the register shows — not the whole account. Reuses
        // the SAME register_filtered_entries primitive the register page / rail /
        // counts use (ADR-0076), so the selection set can't drift from the
        // displayed set. Status is already applied by the switch above, so only
        // the non-status dimensions go here.
        var registerFilter = new RegisterFilter(
            Search: selection.Search,
            DateFrom: selection.DateFrom,
            DateTo: selection.DateTo,
            AmountMin: selection.AmountMin,
            AmountMax: selection.AmountMax,
            SecurityId: selection.SecurityId,
            Tag: selection.Tag,
            CategoryId: selection.CategoryId);
        if (selection.AccountId.HasValue && registerFilter.IsActive)
        {
            var filterAccountId = selection.AccountId.Value;
            // hidden = null: visibility scope is already applied by `q` above;
            // here we only intersect on the non-status filter dimensions.
            var matchingHeaderIds = _db.RegisterFilteredEntries(
                    filterAccountId, ledgerId, null,
                    registerFilter.Search, registerFilter.DateFrom, registerFilter.DateTo,
                    registerFilter.AmountMin, registerFilter.AmountMax, registerFilter.SecurityId,
                    registerFilter.Tag, registerFilter.CategoryId, registerFilter.Status, registerFilter.Today)
                .Select(r => r.HeaderId)
                .Distinct();
            q = q.Where(h => matchingHeaderIds.Contains(h.Id));
        }

        // The selection-time anchor: rows created after the user
        // clicked select-all are not part of the predicate. Captures
        // Gmail's "everything I had selected at click time" semantics
        // without client-side id tracking.
        var selectedAt = selection.SelectedAt;
        q = q.Where(h => h.CreatedAt <= selectedAt);

        if (selection.ExcludeIds.Count > 0)
        {
            var excluded = selection.ExcludeIds.ToArray();
            q = q.Where(h => !EF.Constant(excluded).Contains(h.Id));
        }

        return q;
    }

    /// <summary>
    /// Resolve the selection, count it, and (for account-scoped
    /// selections) sum the source-leg amount. Account-scope is the
    /// precondition for a meaningful sum — across mixed currencies
    /// the sum has no single denomination, so we return null.
    /// </summary>
    public async Task<SelectionSummary?> GetSelectionSummaryAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default)
    {
        var query = BuildSelectionQuery(ledgerId, selection);
        if (query is null) return null;

        var count = await query
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (count == 0)
        {
            return new SelectionSummary(0, selection.AccountId.HasValue ? 0m : null);
        }

        decimal? sumOnAccount = null;
        if (selection.AccountId.HasValue)
        {
            var accountId = selection.AccountId.Value;
            // Sum the EFFECTIVE leg amounts on the source account across
            // every matching header (override-aware, via the view — the
            // total must match what the user sees). One row per leg on
            // this account; splits contribute multiple legs.
            sumOnAccount = await _db.ResolvedTransactions
                .Where(rv => rv.AccountId == accountId
                    && query.Any(h => h.Id == rv.HeaderId))
                .SumAsync(rv => (decimal?)rv.Amount, cancellationToken)
                .ConfigureAwait(false)
                ?? 0m;
        }

        return new SelectionSummary(count, sumOnAccount);
    }

    /// <summary>
    /// Set <c>status</c> on every header in the selection inside one
    /// atomic UPDATE. Audit columns (<c>cleared_at</c>,
    /// <c>cleared_by_user_id</c>) move in lockstep with the new
    /// status so the DB CHECK
    /// <c>(status='cleared') ⇔ (cleared_at IS NOT NULL)</c> stays
    /// satisfied. Returns the number of rows affected.
    /// </summary>
    /// <remarks>Status / cleared_at / cleared_by_user_id are not
    /// balance-affecting columns; no recompute needed on this path.
    /// The bypass-the-interceptor concern called out in the class
    /// comment doesn't apply here.</remarks>
    public async Task<int> BulkSetReconStatusAsync(
        Guid ledgerId,
        SelectionRequest selection,
        Guid accountId,
        string newStatus,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var query = BuildSelectionQuery(ledgerId, selection);
        if (query is null) return 0;

        // Reconciliation is per-account (ADR-0082): bulk-recon targets the
        // selected transactions' legs on `accountId` (the register's account —
        // supplied explicitly since an 'explicit' selection carries no account).
        var legIds = await _db.TxnLegs
            .Where(l => l.AccountId == accountId
                && query.Select(h => h.Id).Contains(l.HeaderId))
            .Select(l => l.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (legIds.Count == 0) return 0;

        var clearedAt = newStatus == "cleared" ? (DateTime?)DateTime.UtcNow : null;
        var clearedBy = newStatus == "cleared" ? (Guid?)currentUserId : null;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Update the legs that already carry an overlay row...
        await _db.TxnLegRecon
            .Where(r => legIds.Contains(r.LegId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.Status, _ => newStatus)
                    .SetProperty(r => r.ClearedAt, _ => clearedAt)
                    .SetProperty(r => r.ClearedByUserId, _ => clearedBy),
                cancellationToken)
            .ConfigureAwait(false);

        // ...and insert one for each leg that doesn't have it yet.
        var existing = (await _db.TxnLegRecon
            .Where(r => legIds.Contains(r.LegId))
            .Select(r => r.LegId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        foreach (var legId in legIds)
        {
            if (existing.Contains(legId)) continue;
            _db.TxnLegRecon.Add(new TxnLegReconRow
            {
                LegId = legId,
                LedgerId = ledgerId,
                Status = newStatus,
                ClearedAt = clearedAt,
                ClearedByUserId = clearedBy,
            });
        }
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return legIds.Count;
    }

    /// <summary>
    /// Apply the per-row hard-delete vs soft-hide policy across the
    /// entire selection inside one transaction. Manual entries
    /// (<c>external_id IS NULL</c>) are physically removed; feed /
    /// import rows are flagged <c>is_hidden=true</c>.
    /// </summary>
    /// <remarks>Balance recompute is invoked explicitly via
    /// <see cref="LegDerivedRecomputeService"/> at the end for both
    /// branches. Reason: <c>ExecuteDeleteAsync</c> AND
    /// <c>ExecuteUpdateAsync</c> bypass the EF ChangeTracker, so
    /// <see cref="LegDerivedRecomputeInterceptor"/> doesn't see them.
    /// The soft-hide branch flips <c>is_hidden</c>, which became
    /// balance-relevant in mig 103 (hidden rows are excluded from the
    /// balance walk) — same #4 call-site pattern as the hard-delete
    /// branch.</remarks>
    public async Task<(int HardDeleted, int SoftHidden)> BulkDeleteAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default)
    {
        var query = BuildSelectionQuery(ledgerId, selection);
        if (query is null) return (0, 0);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Capture (account_id, EFFECTIVE posted_at) pairs for BOTH
        // branches BEFORE the ExecuteDelete / ExecuteUpdate runs —
        // once the hard-delete cascade fires the legs are gone, and
        // ExecuteUpdate (the soft-hide branch) bypasses the
        // ChangeTracker so the interceptor can't see it. Mig 103 made
        // is_hidden balance-relevant, so the soft-hide path now needs
        // the same explicit recompute as the hard-delete path.
        //
        // The anchor MUST be the EFFECTIVE date —
        // COALESCE(override.posted_at, header.posted_at) — because
        // fn_recompute_balances_for_account walks by the effective date
        // (mig 103), and bank date edits live in txn_header_overrides
        // (ADR-0003). Anchoring on the raw header date would leave the
        // [effective, raw) range unrecomputed when an override moved the
        // header earlier than its raw date. The single-row interceptor
        // resolves the same COALESCE in CaptureSnapshotAsync.
        var hardAffected = await query
            .Where(h => h.ExternalId == null)
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new
                {
                    l.AccountId,
                    PostedAt = _db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (DateTime?)o.PostedAt)
                        .FirstOrDefault() ?? h.PostedAt,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var softAffected = await query
            .Where(h => h.ExternalId != null)
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new
                {
                    l.AccountId,
                    PostedAt = _db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (DateTime?)o.PostedAt)
                        .FirstOrDefault() ?? h.PostedAt,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Holdings + lots are ALSO derived from these legs, by a SEPARATE
        // recompute interceptor that the bulk path likewise bypasses.
        // Capture the (holdings-account, security) pairs of every
        // investment-shape leg in the WHOLE selection — across BOTH
        // branches: a hard delete cascades the leg + its lot away
        // (lots.leg_id ON DELETE CASCADE, mig 123) and a soft-hide makes
        // the leg invisible to the holdings walk (mig 117). Either way the
        // holding must be rebuilt from the surviving legs, or it keeps the
        // removed buy's shares/cost-basis (silent holdings drift).
        var holdingsAffected = await query
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null && l.Quantity != null),
                (h, l) => new { l.AccountId, l.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Hard-delete manual rows (external_id IS NULL). CASCADE on
        // txn_legs / txn_header_overrides / txn_leg_overrides /
        // txn_header_tags handles the cleanup.
        var hardDeleted = await query
            .Where(h => h.ExternalId == null)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // Soft-hide everything else. Re-source / re-sync upserts
        // back into the same row but leaves is_hidden alone.
        var softHidden = await query
            .Where(h => h.ExternalId != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(h => h.IsHidden, _ => true)
                    // Soft-delete also clears needs_review (ADR-0052 D3) so a
                    // deleted row can't linger in the review queue as
                    // hidden-but-pending.
                    .SetProperty(h => h.NeedsReview, _ => false),
                cancellationToken)
            .ConfigureAwait(false);

        // Explicit balance recompute — both branches bypass the
        // interceptor (ExecuteDelete + ExecuteUpdate).
        var affected = hardAffected.Concat(softAffected).ToList();
        if (affected.Count > 0)
        {
            await _balances.RecomputeAsync(
                affected.Select(a => (a.AccountId, a.PostedAt)),
                cancellationToken).ConfigureAwait(false);
        }

        // Explicit holdings/lots recompute — the bulk path bypasses the
        // HoldingsRecomputeInterceptor for the same reason (ExecuteDelete /
        // ExecuteUpdate don't touch the ChangeTracker). The service dedupes
        // (account, security) pairs; a non-investment selection captures
        // none and this is a no-op.
        if (holdingsAffected.Count > 0)
        {
            await _holdings.RecomputeAsync(
                holdingsAffected.Select(a => (a.AccountId, a.SecurityId!.Value)),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (hardDeleted, softHidden);
    }

    /// <summary>
    /// Bulk un-hide the selection (ADR-0072 D2): flip <c>is_hidden</c> back to
    /// false. The selection is expected to carry <c>StatusFilter = "hidden"</c>
    /// (the Hidden view), so <see cref="BuildSelectionQuery"/> already scopes to
    /// hidden rows. Un-hidden rows re-enter the balance + holdings walks, so both
    /// are recomputed explicitly — <c>ExecuteUpdateAsync</c> bypasses the
    /// interceptor, same #4 call-site pattern as bulk-delete's soft-hide branch.
    /// Returns the number un-hidden.
    /// </summary>
    public async Task<int> BulkUnhideAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default)
    {
        var query = BuildSelectionQuery(ledgerId, selection);
        if (query is null) return 0;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Capture (account, EFFECTIVE posted_at) + investment (account, security)
        // pairs BEFORE the ExecuteUpdate (which bypasses the ChangeTracker), same
        // as the bulk-delete soft-hide branch. Anchor on the effective date so the
        // recompute walk (mig 103) covers the right range.
        var affected = await query
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new
                {
                    l.AccountId,
                    PostedAt = _db.TxnHeaderOverrides
                        .Where(o => o.HeaderId == h.Id)
                        .Select(o => (DateTime?)o.PostedAt)
                        .FirstOrDefault() ?? h.PostedAt,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var holdingsAffected = await query
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null && l.Quantity != null),
                (h, l) => new { l.AccountId, l.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var unhidden = await query
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(h => h.IsHidden, _ => false),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected.Count > 0)
        {
            await _balances.RecomputeAsync(
                affected.Select(a => (a.AccountId, a.PostedAt)),
                cancellationToken).ConfigureAwait(false);
        }
        if (holdingsAffected.Count > 0)
        {
            await _holdings.RecomputeAsync(
                holdingsAffected.Select(a => (a.AccountId, a.SecurityId!.Value)),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unhidden;
    }

    /// <summary>Outcome of a move-to-account (ADR-0072 D3).</summary>
    public enum MoveAccountOutcome
    {
        HeaderNotFound, NotBankShape, NotOnSourceAccount,
        TargetInvalid, TargetSameAsSource, SplitToInvestment, Collision, Moved,
    }

    /// <summary>
    /// Move a single transaction from <paramref name="sourceAccountId"/> to
    /// <paramref name="targetAccountId"/> (ADR-0072 D3): repoint the source-side
    /// leg(s) — one per posting — then recompute both accounts. Bank-shape only.
    /// Guards reject a category/other-ledger target, a self-transfer collision
    /// (the target is already a leg on the transaction), and a split moved to an
    /// investment account. ExecuteUpdate bypasses the interceptor, so the
    /// balance recompute is explicit for both accounts.
    /// </summary>
    public async Task<MoveAccountOutcome> MoveAccountAsync(
        Guid ledgerId, Guid headerId, Guid sourceAccountId, Guid targetAccountId,
        CancellationToken cancellationToken = default)
    {
        if (sourceAccountId == targetAccountId) return MoveAccountOutcome.TargetSameAsSource;

        var target = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == targetAccountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null || target.AccountType == "category")
            return MoveAccountOutcome.TargetInvalid;

        var header = await _db.TxnHeaders.AsNoTracking()
            .Where(h => h.Id == headerId && h.LedgerId == ledgerId)
            .Select(h => new
            {
                h.Action,
                EffectivePostedAt = _db.TxnHeaderOverrides
                    .Where(o => o.HeaderId == h.Id)
                    .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null) return MoveAccountOutcome.HeaderNotFound;
        if (header.Action is not null) return MoveAccountOutcome.NotBankShape;

        var legs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.HeaderId == headerId)
            .Select(l => new { l.AccountId, l.PostingIndex })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (legs.All(l => l.AccountId != sourceAccountId)) return MoveAccountOutcome.NotOnSourceAccount;
        // posting_index > 0 ⇒ the header is a split (has more than one posting).
        if (legs.Any(l => l.PostingIndex > 0) && target.AccountType == "investment")
            return MoveAccountOutcome.SplitToInvestment;
        if (legs.Any(l => l.AccountId == targetAccountId)) return MoveAccountOutcome.Collision;

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _db.TxnLegs
            .Where(l => l.HeaderId == headerId && l.AccountId == sourceAccountId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, _ => targetAccountId), cancellationToken)
            .ConfigureAwait(false);
        await _balances.RecomputeAsync(
            new[] { (sourceAccountId, header.EffectivePostedAt), (targetAccountId, header.EffectivePostedAt) },
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MoveAccountOutcome.Moved;
    }

    /// <summary>Outcome of a bulk move-to-account (ADR-0072 D3).</summary>
    public enum BulkMoveOutcome
    {
        TargetInvalid, SourceScopeRequired, TargetSameAsSource,
        SplitToInvestment, Collision, InvestmentShape, Moved,
    }

    /// <summary>
    /// Move the whole selection to <paramref name="targetAccountId"/> (ADR-0072
    /// D3). The selection is account-scoped — its AccountId is the source. Same
    /// guards as the single move, applied ALL-OR-NOTHING: nothing moves if ANY
    /// selected row is investment-shape (holdings-tied — moving those is out of
    /// scope), would collide (target already on it), or is a split headed to an
    /// investment account. Recomputes source + target.
    /// </summary>
    public async Task<(BulkMoveOutcome Outcome, int Moved)> BulkMoveAccountAsync(
        Guid ledgerId, SelectionRequest selection, Guid targetAccountId,
        CancellationToken cancellationToken = default)
    {
        var target = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == targetAccountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null || target.AccountType == "category")
            return (BulkMoveOutcome.TargetInvalid, 0);
        if (selection.AccountId is not { } sourceAccountId)
            return (BulkMoveOutcome.SourceScopeRequired, 0);
        if (sourceAccountId == targetAccountId)
            return (BulkMoveOutcome.TargetSameAsSource, 0);

        var query = BuildSelectionQuery(ledgerId, selection);
        if (query is null) return (BulkMoveOutcome.Moved, 0);

        // Guard: bank-shape only. An investment-shape header (action != null)
        // is tied to holdings + lots, which this leg-repoint doesn't carry — so
        // moving one, in either direction, is out of scope. Mirrors the single-
        // row NotBankShape guard so the endpoint enforces the invariant no
        // matter which UI calls it.
        var anyInvestment = await query
            .AnyAsync(h => h.Action != null, cancellationToken)
            .ConfigureAwait(false);
        if (anyInvestment) return (BulkMoveOutcome.InvestmentShape, 0);

        // Guard: any selected split headed to an investment account.
        if (target.AccountType == "investment")
        {
            var anySplit = await query
                .AnyAsync(h => _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.PostingIndex > 0),
                    cancellationToken).ConfigureAwait(false);
            if (anySplit) return (BulkMoveOutcome.SplitToInvestment, 0);
        }

        // Guard: any selected header already has a leg on the target (self-transfer).
        var anyCollision = await query
            .AnyAsync(h => _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == targetAccountId),
                cancellationToken).ConfigureAwait(false);
        if (anyCollision) return (BulkMoveOutcome.Collision, 0);

        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var affectedDates = await query
            .Select(h => _db.TxnHeaderOverrides.Where(o => o.HeaderId == h.Id)
                .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var moved = await _db.TxnLegs
            .Where(l => l.AccountId == sourceAccountId
                && query.Select(h => h.Id).Contains(l.HeaderId))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, _ => targetAccountId),
                cancellationToken).ConfigureAwait(false);

        if (affectedDates.Count > 0)
        {
            var pairs = affectedDates
                .SelectMany(d => new[] { (sourceAccountId, d), (targetAccountId, d) });
            await _balances.RecomputeAsync(pairs, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (BulkMoveOutcome.Moved, moved);
    }
}
