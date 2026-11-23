using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Domain.Annotations;

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
/// selection. The bulk delete recomputes both for the rows merged into a
/// winner it deletes too, which the database un-merges
/// (<see cref="MergeLosers"/>). Status-only bulk paths (recon-status) touch no
/// balance/holdings-relevant column and skip both recompute calls. The bulk
/// delete also re-derives the trade price of each (security, day) a deleted or
/// hidden execution leaves and an un-merged one rejoins, and the bulk restore each
/// day an unhidden one rejoins, through <see cref="TradePriceRecomputeService"/>, as
/// <see cref="TradePriceFromLegInterceptor"/> does for a single delete or restore
/// (ADR-0084 D4, 2026-11-03 amendment; a hidden or merged-away row is no trade,
/// migration 273).</para>
///
/// <para><b>Posting counts</b> (mig 120) are NOT recomputed here:
/// the recon-status path is status-only (no leg structure change),
/// and bulk-delete removes / hides WHOLE headers (a vanished or
/// hidden header has no surviving legs whose counts could be stale,
/// and other headers are untouched). No posting-count recompute
/// call is needed on either path.</para>
///
/// <para><b>The ledger write lock</b> (migration 275). Every method that recomputes
/// takes it as the first statement of its transaction, before the selection or the
/// checks are read (<see cref="LedgerWriteLock"/>); the recompute functions refuse a
/// transaction that wrote without it. The status-only recon path recomputes nothing and
/// takes no lock.</para>
/// </remarks>
internal sealed class BulkTransactionsRepository
{
    private readonly AppDbContext _db;
    private readonly LegDerivedRecomputeService _balances;
    private readonly HoldingsRecomputeService _holdings;
    private readonly TimeProvider _clock;

    public BulkTransactionsRepository(
        AppDbContext db,
        LegDerivedRecomputeService balances,
        HoldingsRecomputeService holdings,
        TimeProvider clock)
    {
        _db = db;
        _balances = balances;
        _holdings = holdings;
        _clock = clock;
    }

    /// <summary>
    /// The accounts a selection's register shows: its account, and with
    /// <see cref="SelectionRequest.IncludeSubcategories"/> that category's
    /// descendants (mig 226). One account is an array of one.
    /// </summary>
    /// <remarks>
    /// A selection is one register's, and every register is one account's (the API
    /// serves only the UI): the endpoints refuse a selection with no account, so there
    /// is no ledger-wide scope to resolve.
    /// </remarks>
    private async Task<Guid[]> ScopeIdsAsync(
        Guid ledgerId, SelectionRequest selection, CancellationToken cancellationToken)
    {
        var scopeId = selection.AccountId
            ?? throw new ArgumentException("A selection is scoped to the register's account.", nameof(selection));
        return selection.IncludeSubcategories
            ? (await CategoryTree
                .DescendantsAsync(_db, ledgerId, scopeId, includeRoot: true, cancellationToken)
                .ConfigureAwait(false)).ToArray()
            : [scopeId];
    }

    /// <summary>
    /// A header the register over <paramref name="scopeIds"/> can EDIT, not merely
    /// shows: it has a leg there whose stored presentation (migration 264) is not
    /// read-only. That is the flag the register itself marks a read-only row by
    /// (<see cref="Presentation.StoredNotesQuery.RegisterLegs"/>), so the bulk paths and
    /// the register cannot disagree about ownership.
    /// </summary>
    /// <remarks>
    /// The posting counts (migration 120) are not that rule. An account on every
    /// posting of a header can still be read-only there: a split whose every line
    /// lands on one category (the header is its originating account's), and a
    /// one-posting event's counterpart (the event is the investment side's). A leg
    /// whose presentation was never stored has no flag and does not count, so a
    /// missing value can only narrow what is editable.
    /// </remarks>
    private Expression<Func<TxnHeaderRow, bool>> EditableFrom(Guid[] scopeIds) =>
        h => _db.TxnLegs.Any(
            l => l.HeaderId == h.Id
                && EF.Constant(scopeIds).Contains(l.AccountId)
                && l.NotesIsReadOnly == false);

    /// <summary>
    /// A header the register over <paramref name="scopeIds"/> shows but cannot edit:
    /// the negation of <see cref="EditableFrom"/>, so the two cannot drift.
    /// </summary>
    private Expression<Func<TxnHeaderRow, bool>> ReadOnlyFrom(Guid[] scopeIds)
    {
        var editable = EditableFrom(scopeIds);
        Expression<Func<TxnHeaderRow, bool>> readOnly = h => !editable.Inline(h);
        return readOnly.Expand();
    }

    /// <summary>
    /// True when an explicit selection names a transaction the register cannot edit
    /// (<see cref="EditableFrom"/>). The endpoints that write refuse such a selection
    /// whole, before anything is written, as <c>set_transaction_tags</c> refuses a
    /// read-only target; the SPA's own gate is a convenience, not the check. An id that
    /// names no live transaction of the ledger is not one: every bulk path ignores it.
    /// An all-mode selection is never refused: its predicate already leaves read-only
    /// rows out.
    /// </summary>
    public async Task<bool> HasReadOnlyRowAsync(
        Guid ledgerId, SelectionRequest selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Kind != "explicit" || selection.HeaderIds.Count == 0) return false;
        var scopeIds = await ScopeIdsAsync(ledgerId, selection, cancellationToken).ConfigureAwait(false);
        var ids = selection.HeaderIds.ToArray();
        return await _db.TxnHeaders.NotMergedAway()
            .Where(h => h.LedgerId == ledgerId && !h.IsRecurringTemplate
                && EF.Constant(ids).Contains(h.Id))
            .Where(ReadOnlyFrom(scopeIds))
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
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
    /// <param name="ownLegOnly">
    /// The action writes only the register's own leg (reconciliation, ADR-0082), so an
    /// all-mode selection covers every row the register shows, read-only rows included,
    /// rather than only the headers it can edit.
    /// </param>
    private async Task<IQueryable<TxnHeaderRow>?> BuildSelectionQueryAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default,
        bool ownLegOnly = false)
    {
        // Ledger scope is always enforced — RLS will also block
        // cross-ledger rows, but keying the LINQ predicate on
        // ledger_id keeps the query plan tight (and survives if the
        // app.user_id session var is ever misconfigured).
        //
        // A recurring template is never selected, in either mode. It has legs
        // on its accounts like any header, but no register lists it (every
        // register reads live_txn_headers), and a selection covers only rows a
        // register can show: an unfiltered select-all would otherwise count it
        // and hand it to delete, unhide, move and recon status. Nor is a merged-away
        // header: an explicit id that names one is ignored, as an unknown id is
        // (MergedAway).
        IQueryable<TxnHeaderRow> q = _db.TxnHeaders.NotMergedAway()
            .Where(h => h.LedgerId == ledgerId && !h.IsRecurringTemplate);

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
        // recovery view; every other filter selects visible rows. is_hidden is
        // canonical since mig 230, so this matches what the register shows by
        // reading the same column the register reads.
        var hiddenScope = string.Equals(selection.StatusFilter, "hidden", StringComparison.Ordinal);
        q = q.Where(h => h.IsHidden == hiddenScope);

        // This is resolved BEFORE the ownership predicate on purpose. The
        // register-filter intersection further down is an INTERSECTION: it can
        // only ever remove headers, so widening it alone left the widened
        // select-all empty, because the headers of a rollup's children never
        // entered this query in the first place.
        var scopeIds = await ScopeIdsAsync(ledgerId, selection, cancellationToken).ConfigureAwait(false);

        // Applying the ownership rule here makes all-mode summary + delete,
        // unhide and move act ONLY on headers this account can edit: the
        // typed-confirm count the user sees matches exactly what gets
        // deleted, and those bulk ops can never reach a row the register
        // shows read-only. That server-side guarantee is what lets the SPA
        // drop its all-mode delete gate — the client no longer has to audit
        // unloaded rows for read-only ones. (An explicit selection is not
        // narrowed: the endpoints that write refuse one that names a
        // read-only row, HasReadOnlyRowAsync, so the user is told rather
        // than given fewer rows than they checked.)
        //
        // Reconciliation ignores the read-only flag. Its status is the
        // account's own leg (ADR-0082), not the shared transaction, so a
        // read-only row is the register's to clear: the single-row PUT and an
        // explicit selection allow it, and select-all reaches the same rows,
        // within the register's scope.
        if (ownLegOnly)
            q = q.Where(h => _db.TxnLegs.Any(
                l => l.HeaderId == h.Id && EF.Constant(scopeIds).Contains(l.AccountId)));
        else
            q = q.Where(EditableFrom(scopeIds));

        // The recon-status views and "scheduled" are the register's own rule, applied
        // by the register's own primitive below (RegisterQuery.Filtered, ADR-0076),
        // not restated here. A restatement drifts: judging "future" by an instant
        // where the register uses the user's local date, or keeping pending rows the
        // register leaves out, lets a bulk clear over "all uncleared" act on rows
        // the reader was not shown. Today is the client's local date, sent
        // with the selection like SelectedAt; the app clock's UTC date stands in for a
        // caller that sends none, as it does for the register's counts and every current
        // balance: one fallback, ScheduledBoundary.Today.
        var today = ScheduledBoundary.Today(selection.Today, _clock);
        string? registerStatus = null;
        switch (selection.StatusFilter)
        {
            case "all":
                // No status restriction.
                break;
            case "cleared":
            case "uncleared":
            case "reconciling":
                // Per-account (ADR-0082), over the account set resolved above.
            case "scheduled":
                registerStatus = selection.StatusFilter;
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
        // the SAME filter the register page / rail / counts use
        // (RegisterQuery.Filtered, ADR-0076), so the selection set can't drift from
        // the displayed set. The recon-status views and "scheduled" go through it too
        // (see the switch above); needs_review and hidden are applied there.
        var registerFilter = new RegisterFilter(
            Search: selection.Search,
            DateFrom: selection.DateFrom,
            DateTo: selection.DateTo,
            AmountMin: selection.AmountMin,
            AmountMax: selection.AmountMax,
            SecurityId: selection.SecurityId,
            Tag: selection.Tag,
            CategoryId: selection.CategoryId,
            Status: registerStatus,
            Today: today,
            // Carried, not dropped: `IsActive` counts it, so a select-all whose
            // ONLY active dimension is the subtree still takes this branch.
            IncludeSubcategories: selection.IncludeSubcategories);
        if (registerFilter.IsActive)
        {
            // The SAME scope resolved above — the selected set is defined to be
            // what the reader is looking at.
            // hidden = null: visibility scope is already applied by `q` above.
            var matchingHeaderIds = RegisterQuery.Filtered(_db, scopeIds, ledgerId, hidden: null, registerFilter, today)
                .Select(l => l.Row.HeaderId)
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
    /// Resolve the selection, count it, and sum its legs in the register's
    /// scope (a selection always names an account; the endpoints refuse one
    /// that does not).
    /// </summary>
    /// <remarks>
    /// Two sets are counted. The rows the selection covers, read-only rows
    /// included, are what the footer names (<see cref="SelectionSummary.TotalCount"/>,
    /// with <see cref="SelectionSummary.ReadOnlyCount"/> beside it); they come from
    /// the reconciliation scope, which reaches every row the register shows. The Σ
    /// covers that same set, each row contributing its own legs in the register's
    /// scope, so it always matches the count beside it. The rows a bulk change acts
    /// on (<see cref="SelectionSummary.Count"/>) are that set narrowed by
    /// <see cref="EditableFrom"/> for a select-all, exactly as the delete, unhide and
    /// move paths narrow it. An explicit selection is not narrowed: those endpoints
    /// refuse one naming a read-only row instead.
    /// <para>The Σ is in one currency (ADR-0114 D7). A real account's legs are in its
    /// own, so its sum is native. A category's legs are each in the currency of the real
    /// account on the other side of their posting, or the category's own between two
    /// categories, as its register's rows show them. When the selected ones are in one
    /// currency the sum is in it; when they are in several, each converts at its
    /// transaction's date into the reporting currency (<see cref="RateDay.Of(DateOnly, DateOnly)"/>),
    /// summed and rounded once, as the category register's Balance converts. A missing
    /// rate makes the sum null and named (ADR-0114 D9).</para>
    /// </remarks>
    public async Task<SelectionSummary> GetSelectionSummaryAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default)
    {
        var scopeId = selection.AccountId
            ?? throw new ArgumentException("A selection summary is scoped to the register's account.", nameof(selection));
        var scope = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == scopeId)
            .Select(a => new { a.AccountType, a.CurrencyCode })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var ownCurrency = scope?.CurrencyCode
            ?? await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);

        var shown = await BuildSelectionQueryAsync(ledgerId, selection, cancellationToken, ownLegOnly: true)
            .ConfigureAwait(false);
        if (shown is null) return new SelectionSummary(0, 0, 0, 0m, ownCurrency);
        var scopeIds = await ScopeIdsAsync(ledgerId, selection, cancellationToken).ConfigureAwait(false);

        // One statement for every count: each row's read-only flag is projected
        // by the shared ownership rule, then tallied.
        var counts = await shown
            .Select(ReadOnlyFrom(scopeIds))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                ReadOnly = g.Count(isReadOnly => isReadOnly),
                Editable = g.Count(isReadOnly => !isReadOnly),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var total = counts?.Total ?? 0;
        var readOnlyCount = counts?.ReadOnly ?? 0;

        var count = selection.Kind == "explicit" ? total : counts?.Editable ?? 0;
        if (total == 0)
        {
            return new SelectionSummary(0, 0, 0, 0m, ownCurrency);
        }

        if (scope?.AccountType == "category")
        {
            var (sum, currency, why) = await CategorySumAsync(ledgerId, shown, scopeIds, ownCurrency, cancellationToken)
                .ConfigureAwait(false);
            return new SelectionSummary(count, total, readOnlyCount, sum, currency, why.Count > 0 ? why : null);
        }

        // Sum the legs in the register's scope across every selected header,
        // read-only rows included, so the Σ covers the rows TotalCount counts.
        // Each leg is summed once, read straight off txn_legs: resolved_transactions
        // has a row per leg AND counterpart at its posting index, so an event with
        // several legs at one index (a dividend's cash, income and security legs)
        // would be counted once per counterpart. Splits contribute one amount per
        // leg in scope.
        var sumOnAccount = await _db.TxnLegs
            .Where(l => EF.Constant(scopeIds).Contains(l.AccountId)
                && shown.Any(h => h.Id == l.HeaderId))
            .SumAsync(l => (decimal?)l.Amount, cancellationToken)
            .ConfigureAwait(false)
            ?? 0m;

        return new SelectionSummary(count, total, readOnlyCount, sumOnAccount, ownCurrency);
    }

    /// <summary>
    /// The Σ of a category register's selected legs, in one currency (ADR-0114 D7, D9):
    /// see <see cref="GetSelectionSummaryAsync"/>.
    /// </summary>
    /// <remarks>
    /// One statement reads the legs, each once off <c>txn_legs</c> as the real-account sum
    /// reads them, grouped by the currency each is in and, outside the reporting
    /// currency, by its transaction's day: the rule the category totals read
    /// (<see cref="AccountsRepository.ListCategoriesWithUsageAsync"/>). The rates are read
    /// only when the legs are in more than one currency.
    /// </remarks>
    private async Task<(decimal? Sum, string CurrencyCode, IReadOnlyList<RateUnavailable> Why)> CategorySumAsync(
        Guid ledgerId,
        IQueryable<TxnHeaderRow> shown,
        Guid[] scopeIds,
        string ownCurrency,
        CancellationToken cancellationToken)
    {
        var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var sums = await (
                from l in _db.TxnLegs.AsNoTracking()
                join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where EF.Constant(scopeIds).Contains(l.AccountId) && shown.Any(s => s.Id == l.HeaderId)
                let paying = _db.TxnLegs
                    .Where(o => o.HeaderId == l.HeaderId && o.PostingIndex == l.PostingIndex && o.Id != l.Id)
                    .Join(_db.Accounts, o => o.AccountId, oa => oa.Id, (o, oa) => oa)
                    .Where(oa => oa.AccountType != "category")
                    .Select(oa => oa.CurrencyCode)
                    .FirstOrDefault()
                let currency = paying ?? a.CurrencyCode
                group l.Amount by new
                {
                    Currency = currency,
                    Day = currency == reporting ? (DateTime?)null : h.PostedAt.Date,
                } into g
                select new { g.Key.Currency, g.Key.Day, Total = g.Sum() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var currencies = sums.Select(s => s.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count <= 1)
            return (sums.Sum(s => s.Total), currencies.SingleOrDefault() ?? ownCurrency, RateReasons.None);

        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var s in sums.Where(s => s.Day is not null))
            needs.Add(s.Currency, reporting, DateOnly.FromDateTime(s.Day!.Value));
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        var converted = rates.NewTotal(reporting);
        foreach (var s in sums)
        {
            if (s.Day is { } day) converted.Add(s.Total, s.Currency, DateOnly.FromDateTime(day));
            else converted.Add(s.Total, reporting, RateDay.Latest());   // the identity
        }
        return (converted.Amount, reporting, converted.Unavailable);
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
        // ownLegOnly: a read-only row's status is still this account's to set.
        var query = await BuildSelectionQueryAsync(ledgerId, selection, cancellationToken, ownLegOnly: true)
            .ConfigureAwait(false);
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

        var clearedAt = newStatus == "cleared" ? (DateTime?)_clock.GetUtcNow().UtcDateTime : null;
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
    /// <summary>
    /// Remove every transaction a given import created — the answer to "I uploaded that
    /// twice".
    /// </summary>
    /// <remarks>
    /// <para>
    /// File imports have no dedup and will not get one. CSV rows carry no issuer id
    /// (unlike OFX's FITID), so any per-row identity has to be derived from content, and
    /// content cannot separate two genuinely identical transactions — same date, same
    /// amount, same merchant. A content hash collapses them and silently eats a real
    /// row; folding in the row ordinal duplicates the whole file the moment a download
    /// window shifts. Both failure modes are silent, in a ledger reconciled against real
    /// balances.
    /// </para>
    /// <para>
    /// So the mistake is not prevented by guessing, it is UNDONE exactly. Mig 221 stamps
    /// <c>ledger_operation_id</c> on every row an import writes, and this deletes that
    /// set and nothing else. No inference anywhere.
    /// </para>
    /// <para>
    /// Delegates to <see cref="BulkDeleteAsync"/> rather than issuing its own delete:
    /// that method already captures (account, effective posted_at) pairs BEFORE the
    /// cascade fires so the balance recompute anchors correctly, and duplicating a
    /// money-touching delete to save one call is how the two copies drift.
    /// </para>
    /// </remarks>
    public async Task<UndoImportResult> UndoImportAsync(
        Guid ledgerId,
        Guid ledgerOperationId,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) before the import's rows are read, so the
        // delete removes exactly the set read; the bulk delete joins this transaction. A
        // dry run writes nothing and takes no lock.
        await using var write = dryRun
            ? null
            : await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);

        var ids = await _db.TxnHeaders
            .AsNoTracking()
            .Where(h => h.LedgerId == ledgerId
                        && h.LedgerOperationId == ledgerOperationId
                        && h.IsMergedInto == null)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // No "how many have been edited" count here any more. Undo is offered in a
        // modal immediately after the import that created these rows, so nobody has
        // edited them yet — the number was reliably zero, and the only thing that
        // made it non-zero was a merge stamping the winner's date. See
        // UndoImportResult for the rest of the reasoning, including why reading
        // txn_header_originals as an "edited in Coffer" signal is a trap.

        // A PARTIAL undo is worse than none — it leaves a half-removed import that no
        // longer matches its own operation and cannot be finished by repeating the call.
        // Refuse loudly instead. MaxIds is 10_000, far above any real statement.
        if (ids.Count > SelectionLimits.MaxIds)
        {
            return new UndoImportResult(
                Found: ids.Count, Deleted: 0, TooLarge: true);
        }

        if (dryRun || ids.Count == 0)
        {
            return new UndoImportResult(
                Found: ids.Count, Deleted: 0, TooLarge: false);
        }

        // AN UNDO REMOVES THE ROWS OUTRIGHT, not hides them. Hiding is reversible and
        // deleting is not, but that is the wrong recovery to optimise for.
        //
        // The thing a person does after undoing an import is import the file again —
        // wrong account, wrong file, wrong day. A hidden row keeps its external_id, the
        // dedup lookup matches on external_id and never on is_hidden, and the soft-hide
        // path deliberately leaves is_hidden alone so a re-sync cannot undo a manual
        // hide. So a re-import over hidden rows matches every one, counts it
        // already-known, inserts nothing, and leaves the register empty while reporting
        // success. Hiding would not preserve recoverability; it would block the only
        // recovery there is.
        //
        // For a file import the FILE is the backup, which is a better recovery story
        // than an invisible row nothing can bring back.
        var (hard, soft) = await BulkDeleteAsync(
            ledgerId,
            new SelectionRequest { Kind = "explicit", HeaderIds = ids },
            hardDeleteSourcedRows: true,
            cancellationToken).ConfigureAwait(false);
        await write!.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Zero unless a reminder occurrence somehow carried an import's stamp, which an
        // import cannot produce. Surfaced rather than silently added to the total: a row
        // that survived as hidden when the caller was told it was deleted is exactly the
        // discrepancy that would send someone re-importing into a duplicate.
        if (soft != 0)
        {
            throw new InvalidOperationException(
                "Undo hid " + soft + " row(s) instead of removing them; a re-import "
                + "would then skip them as already-known. Check what wrote a reminder "
                + "occurrence under an import's ledger_operation_id.");
        }

        return new UndoImportResult(
            Found: ids.Count, Deleted: hard, TooLarge: false);
    }

    /// <param name="hardDeleteSourcedRows">
    /// Remove feed-sourced rows (<c>external_id</c> set) outright instead of hiding
    /// them. Used ONLY by undo-import, and load-bearing there.
    /// <para>
    /// The default is to hide such a row, so a later re-sync upserts into it rather than
    /// resurrecting something the user deleted. That rule is wrong for an undo, because
    /// the thing a person does after undoing an import is IMPORT THE FILE AGAIN — and a
    /// hidden row still matches the dedup lookup (which filters on external_id and never
    /// on is_hidden), so the re-import counts every row already-known, inserts nothing,
    /// and leaves the register empty while reporting success. Hiding does not preserve
    /// recoverability for an import; it blocks the only recovery there is.
    /// </para>
    /// <para>
    /// Reminder occurrences are still never hard-deleted, even here. Their
    /// (recurring_transaction_id, occurrence_date) stamp IS the slot's idempotency key
    /// (mig 218), and removing it makes reminder-auto-post re-post the occurrence on the
    /// next tick — a loop the user cannot escape. An import cannot create one, so the
    /// carve-out is unreachable from undo; it is kept because "unreachable today" is not
    /// a reason to arm a footgun.
    /// </para>
    /// </param>
    public async Task<(int HardDeleted, int SoftHidden)> BulkDeleteAsync(
        Guid ledgerId,
        SelectionRequest selection,
        bool hardDeleteSourcedRows = false,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) is the transaction's first statement,
        // before the selection is resolved: the delete's cascade row-locks the stored
        // balances and lots its recomputes then rewrite, which another recompute of the
        // ledger must not be holding or waiting on. Undo-import took it already, and this
        // joins its transaction.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var query = await BuildSelectionQueryAsync(ledgerId, selection, cancellationToken)
            .ConfigureAwait(false);
        if (query is null) return (0, 0);

        // The selection is read once, in this transaction, and every step below acts on
        // exactly those headers, as the bulk move does. Asked again at each step under
        // READ COMMITTED, a filtered select-all would also take a row that came to match
        // between two of them (one another request re-dated, tagged or unhid): deleted or
        // hidden without the reads before it, so with none of its balances, positions or
        // trade days recomputed.
        var selected = await query
            .Select(h => new
            {
                h.Id,
                Sourced = h.ExternalId != null,
                Occurrence = h.RecurringTransactionId != null && h.OccurrenceDate != null,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedIds = selected.Select(h => h.Id).ToList();
        var selectedHeaders = _db.TxnHeaders.Where(h => selectedIds.Contains(h.Id));

        // Hard-delete manual rows (external_id IS NULL). CASCADE on
        // txn_legs / txn_header_originals /
        // txn_header_tags handles the cleanup.
        // A REMINDER OCCURRENCE IS NEVER HARD-DELETED, here as on the single-delete
        // path (TransactionsRepository.DeleteAsync).
        //
        // The (recurring_transaction_id, occurrence_date) stamp on the row IS the slot's
        // idempotency key (mig 218). Hard-deleting takes the stamp with it, the slot
        // reads un-acted, and reminder-auto-post (mig 219) posts it straight back on the
        // next tick — a loop the user cannot escape. Both delete paths therefore carry
        // the same carve-out; bulk-deleting auto-posted occurrences soft-hides them.
        // The reminder-occurrence carve-out holds in BOTH modes; only the external_id
        // half relaxes. Everything else soft-hides: feed-sourced rows (external_id set)
        // AND reminder occurrences, which keep their stamp so the slot stays consumed.
        // In hard mode only the reminder occurrences are left to hide.
        var doomedIds = selected
            .Where(h => (hardDeleteSourcedRows || !h.Sourced) && !h.Occurrence)
            .Select(h => h.Id)
            .ToList();
        var hiddenIds = selectedIds.Except(doomedIds).ToList();
        var doomed = _db.TxnHeaders.Where(h => doomedIds.Contains(h.Id));

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
        // (mig 103), and bank date edits land on txn_headers.posted_at
        // (ADR-0003). Anchoring on the raw header date would leave the
        // [effective, raw) range unrecomputed when an override moved the
        // header earlier than its raw date. The single-row interceptor
        // resolves the same COALESCE in CaptureSnapshotAsync.
        var selectionAffected = await selectedHeaders
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new
                {
                    l.AccountId,
                    PostedAt = h.PostedAt,
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
        var holdingsAffected = await selectedHeaders
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null && l.Quantity != null),
                (h, l) => new { l.AccountId, l.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Trade prices, for the third interceptor the bulk path bypasses
        // (TradePriceFromLegInterceptor): a deleted execution leaves the (security,
        // day) it priced, and that day is re-derived from the executions that remain
        // (ADR-0084 D4, 2026-11-03 amendment). Both branches: a hidden row is no trade
        // either (migration 273), so a soft-hidden execution leaves its day as a
        // hard-deleted one does. Read before the cascade, from the selection's rows.
        var tradeDays = await TradeDaysAsync(selectedHeaders, cancellationToken).ConfigureAwait(false);

        // The rows merged into a winner the delete takes. The database un-merges them
        // (ON DELETE SET NULL, migration 121), so each rejoins its accounts' balances and,
        // for an investment line, its position, and nothing tracked says so (MergeLosers):
        // their lines are read here, before the delete, beside the selection's own. A
        // selection never covers a merged row, so none of them is deleted. An un-merged
        // row is a trade again (migration 273), so each day it has an execution on is
        // re-derived with the selection's.
        var losers = _db.TxnHeaders
            .Where(h => h.IsMergedInto != null && doomedIds.Contains(h.IsMergedInto.Value));
        tradeDays.AddRange(await TradeDaysAsync(losers, cancellationToken).ConfigureAwait(false));
        var loserAffected = await losers
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new { l.AccountId, h.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var loserHoldings = await losers
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null && l.Quantity != null),
                (h, l) => new { l.AccountId, l.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hardDeleted = await doomed
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        // Soft-hide everything else. Re-source / re-sync upserts
        // back into the same row but leaves is_hidden alone.
        var softHidden = await _db.TxnHeaders
            .Where(h => hiddenIds.Contains(h.Id))
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
        // interceptor (ExecuteDelete + ExecuteUpdate) — and the un-merged
        // losers' accounts with them.
        var affected = selectionAffected
            .Select(a => (a.AccountId, a.PostedAt))
            .Concat(loserAffected.Select(a => (a.AccountId, a.PostedAt)))
            .ToList();
        if (affected.Count > 0)
        {
            await _balances.RecomputeAsync(affected, cancellationToken).ConfigureAwait(false);
        }

        // Explicit holdings/lots recompute — the bulk path bypasses the
        // HoldingsRecomputeInterceptor for the same reason (ExecuteDelete /
        // ExecuteUpdate don't touch the ChangeTracker). The service dedupes
        // (account, security) pairs; a non-investment selection captures
        // none and this is a no-op.
        var positions = holdingsAffected.Concat(loserHoldings)
            .Select(a => (a.AccountId, a.SecurityId!.Value))
            .ToList();
        if (positions.Count > 0)
        {
            await _holdings.RecomputeAsync(positions, cancellationToken).ConfigureAwait(false);
        }

        // The days the deleted or hidden executions left and the un-merged ones rejoined,
        // through the interceptor's own re-derivation: rank-gated, and a day no
        // execution prices any more keeps its row.
        if (tradeDays.Count > 0)
        {
            await new TradePriceRecomputeService(_db).ReseedAsync(tradeDays, cancellationToken)
                .ConfigureAwait(false);
        }

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (hardDeleted, softHidden);
    }

    /// <summary>
    /// The (ledger, security, UTC day) of every trade line of <paramref name="headers"/>, on
    /// the day its header gives it: the days a set-based write that adds or removes those
    /// headers from the trades re-derives (<see cref="TradePriceRecomputeService"/>), as
    /// <see cref="TradePriceFromLegInterceptor"/> does for a tracked one.
    /// </summary>
    /// <remarks>
    /// Read before the write, which may delete the lines or move the rows out of a filter
    /// the query names. Every trade-shaped line is taken, hidden or merged or not:
    /// re-deriving a day no execution left writes the price the rule already names for it.
    /// </remarks>
    private async Task<List<(Guid LedgerId, Guid SecurityId, DateOnly Day)>> TradeDaysAsync(
        IQueryable<TxnHeaderRow> headers, CancellationToken cancellationToken) =>
        (await headers
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null
                    && l.Quantity != null && l.Quantity != 0m
                    && l.UnitPrice != null && l.UnitPrice > 0m),
                (h, l) => new { l.LedgerId, SecurityId = l.SecurityId!.Value, h.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .Select(d => (d.LedgerId, d.SecurityId, HeaderDates.UtcDay(d.PostedAt)))
        .ToList();

    /// <summary>
    /// Bulk un-hide the selection (ADR-0072 D2): flip <c>is_hidden</c> back to
    /// false. The selection is an explicit list of ids (a restore of one row is a
    /// selection of one) or a select-all, which <see cref="BuildSelectionQueryAsync"/>
    /// scopes to its view, usually the Hidden view; an explicit selection is not scoped
    /// to any view. Only the hidden rows among the selected ones are restored; a
    /// visible, unknown or merged-away id is left as it is. Un-hidden rows re-enter the
    /// balance + holdings walks and the trades that price a day, so all three are
    /// recomputed explicitly — <c>ExecuteUpdateAsync</c> bypasses the interceptors, same
    /// #4 call-site pattern as bulk-delete's soft-hide branch. Returns the number
    /// un-hidden: a selected row that is not hidden is not counted.
    /// </summary>
    public async Task<int> BulkUnhideAsync(
        Guid ledgerId,
        SelectionRequest selection,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) first, as the bulk delete takes it.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var query = await BuildSelectionQueryAsync(ledgerId, selection, cancellationToken)
            .ConfigureAwait(false);
        if (query is null) return 0;

        // The selection is read once, in this transaction, and every step below acts on
        // exactly those headers, as the bulk delete and the bulk move do. Asked again at
        // each step under READ COMMITTED, a select-all over the Hidden view would also
        // take a row another request hid between two of them: restored with none of its
        // balances, positions or trade days recomputed.
        //
        // Only a hidden row is restored or counted: an explicit selection is not scoped
        // to the Hidden view, and an all-mode one over another view selects visible rows,
        // which an unhide leaves as they are.
        var selectedIds = await query
            .Where(h => h.IsHidden)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var selected = _db.TxnHeaders.Where(h => selectedIds.Contains(h.Id));

        // Capture (account, EFFECTIVE posted_at) + investment (account, security)
        // pairs BEFORE the ExecuteUpdate (which bypasses the ChangeTracker), same
        // as the bulk-delete soft-hide branch. Anchor on the effective date so the
        // recompute walk (mig 103) covers the right range.
        var affected = await selected
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id),
                (h, l) => new
                {
                    l.AccountId,
                    PostedAt = h.PostedAt,
                })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var holdingsAffected = await selected
            .SelectMany(
                h => _db.TxnLegs.Where(l => l.HeaderId == h.Id
                    && l.SecurityId != null && l.Quantity != null),
                (h, l) => new { l.AccountId, l.SecurityId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // An unhidden execution is a trade again (migration 273), so the day it sits on is
        // re-derived, as a single delete's save re-derives the day it hides.
        var tradeDays = await TradeDaysAsync(selected, cancellationToken).ConfigureAwait(false);

        var unhidden = await _db.TxnHeaders
            .Where(h => selectedIds.Contains(h.Id))
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
        if (tradeDays.Count > 0)
        {
            await new TradePriceRecomputeService(_db).ReseedAsync(tradeDays, cancellationToken)
                .ConfigureAwait(false);
        }

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unhidden;
    }

    /// <summary>
    /// The relabel a move makes (ADR-0114 D13, D15): the moved legs, read in the target's
    /// currency instead of the source's. A move between two accounts in one currency
    /// relabels nothing. A move between categories relabels only the lines of postings
    /// between categories, which read in the category's own code (ADR-0114 D15), and judges
    /// them for places alone; a category's line beside a real line takes the real side's
    /// currency and is left alone. Read before the legs move, under the write lock.
    /// </summary>
    private Task<RelabelCheck.Verdict> JudgeRelabelAsync(
        Guid ledgerId, IReadOnlyCollection<Guid> headerIds, Guid sourceAccountId,
        string sourceCurrency, string targetCurrency, CancellationToken cancellationToken)
    {
        if (string.Equals(sourceCurrency, targetCurrency, StringComparison.Ordinal))
            return Task.FromResult(RelabelCheck.Verdict.None(targetCurrency));
        // A move names no account-level figure: the source's opening balance and loan terms
        // stay where they are.
        return RelabelCheck.JudgeAsync(
            _db, ledgerId, [new RelabelCheck.Relabel(sourceAccountId, sourceCurrency, targetCurrency)],
            headerIds, targetCurrency, [], cancellationToken);
    }

    /// <summary>Outcome of a bulk move-to-account (ADR-0072 D3).</summary>
    public enum BulkMoveOutcome
    {
        TargetInvalid, SourceScopeRequired, TargetSameAsSource,
        SplitToInvestment, Collision, InvestmentShape, Moved,
        /// <summary>A category leg would land on a money account, or a money
        /// leg on a category — see <see cref="CrossesCategoryBoundary"/>.</summary>
        AccountDomainCrossed,

        /// <summary>The target has been deactivated. Moving rows OFF an
        /// inactive account stays legal — that is how one is cleaned up.</summary>
        TargetInactive,

        /// <summary>A split cannot leave an investment account for a bank —
        /// the reverse of <see cref="SplitToInvestment"/>.</summary>
        SplitFromInvestment,

        /// <summary>The move into another currency is refused for at least one selected
        /// row (ADR-0114 D13, D15), so nothing moves; the result's
        /// <see cref="BulkMoveResult.Relabel"/> says why.</summary>
        RelabelRefused,
    }

    /// <summary>What a bulk move did, or why it was refused.</summary>
    /// <param name="Outcome">The outcome.</param>
    /// <param name="Moved">Legs repointed, on <see cref="BulkMoveOutcome.Moved"/>.</param>
    /// <param name="BecameTwoAmount">On <see cref="BulkMoveOutcome.Moved"/>, how many
    /// mirrored pairs the move left across currencies (ADR-0114 D13).</param>
    /// <param name="Relabel">On <see cref="BulkMoveOutcome.RelabelRefused"/>, the verdict
    /// that refused it.</param>
    public sealed record BulkMoveResult(
        BulkMoveOutcome Outcome, int Moved = 0, int BecameTwoAmount = 0, RelabelCheck.Verdict? Relabel = null)
    {
        public static implicit operator BulkMoveResult(BulkMoveOutcome outcome) => new(outcome);

        /// <summary>The outcome and the legs moved, as the bulk move answered before it
        /// relabelled.</summary>
        public void Deconstruct(out BulkMoveOutcome outcome, out int moved)
        {
            outcome = Outcome;
            moved = Moved;
        }
    }

    /// <summary>
    /// The three kinds of thing a leg can sit on, for the purpose of moving it.
    /// </summary>
    /// <remarks>
    /// Mirrors the split the register router already makes — investment
    /// accounts get their own page, everything else shares the bank register,
    /// and a category's register is that page viewed from the other side. The
    /// banking domain is deliberately WIDE: bank, credit card, asset,
    /// liability, loan and cash all render the same row shape, so a leg means
    /// the same thing on any of them.
    /// </remarks>
    private static string MoveDomain(string accountType) => accountType switch
    {
        "category" => "category",
        "investment" => "investment",
        _ => "banking",
    };

    // =========================================================================
    // THE MOVE RULE — one implementation, asked by the bulk move (ADR-0072 D3),
    // which moves a selection of one row as it moves forty. The single-row move
    // route was removed (settled by Claude, 2026-11-18): no screen called it.
    //
    // The rule lives here once, over the selection's header query, and the SPA's
    // picker only offers what it allows.
    //
    //   * A category and an account are not interchangeable. The move is
    //     `UPDATE txn_legs SET account_id = target WHERE account_id = source`;
    //     crossing that boundary leaves a transaction with no category at all.
    //   * Banking and investment accounts ARE interchangeable, both ways, for a
    //     cash row — a brokerage deposit filed against checking, or the reverse.
    //     A split may not cross in either direction.
    //   * An investment event's money leg stays where it is, because its
    //     holdings would stay behind — with one exception, below.
    // =========================================================================

    private enum MoveVerdict
    {
        Allowed, InvestmentShape, SplitToInvestment, SplitFromInvestment, Collision,
    }

    /// <summary>
    /// The account-level half of the rule: would the move put a category leg on
    /// a money account, or a money leg on a category? Checked before any
    /// selection is built, so an empty selection still reports it.
    /// </summary>
    private static bool CrossesCategoryBoundary(string sourceType, string targetType) =>
        (MoveDomain(sourceType) == "category") != (MoveDomain(targetType) == "category");

    /// <summary>
    /// THE EXCEPTION: investment <c>transfer</c>s that are only cash, between the
    /// brokerage and ONE banking account — no security leg, no second posting,
    /// and nothing on the far side but a banking account.
    /// </summary>
    /// <remarks>
    /// An MD-imported contribution from checking arrives as exactly this. Moved
    /// off the brokerage onto a bank, it touches no investment account, so it
    /// stops being an investment event and is converted to a plain transfer
    /// (<see cref="ClearInvestmentEventAsync"/>). A brokerage → brokerage
    /// transfer does not qualify: after the move it would still touch one.
    /// Nothing else derived is stranded — holdings, lots and realized gains are
    /// all keyed off security legs, which this has none of by definition.
    /// </remarks>
    private IQueryable<TxnHeaderRow> CashOnlyTransfers(
        IQueryable<TxnHeaderRow> headers, Guid sourceAccountId) =>
        headers.Where(h => h.Action == "transfer"
            && !_db.TxnLegs.Any(l => l.HeaderId == h.Id
                && (l.SecurityId != null || l.PostingIndex > 0))
            && !_db.TxnLegs.Any(l => l.HeaderId == h.Id
                && l.AccountId != sourceAccountId
                && _db.Accounts.Any(a => a.Id == l.AccountId
                    && (a.AccountType == "investment" || a.AccountType == "category"))));

    /// <summary>True when the move goes from an investment account to a bank,
    /// which is the only direction that can convert a transfer.</summary>
    private static bool InvestmentToBanking(string sourceType, string targetType) =>
        MoveDomain(sourceType) == "investment" && MoveDomain(targetType) == "banking";

    /// <summary>The header-level half of the rule, for one header or many.</summary>
    private async Task<MoveVerdict> CheckHeadersAsync(
        IQueryable<TxnHeaderRow> headers, string sourceType, string targetType,
        Guid sourceAccountId, Guid targetAccountId, CancellationToken cancellationToken)
    {
        var sourceDomain = MoveDomain(sourceType);
        var targetDomain = MoveDomain(targetType);

        // An investment event's MONEY leg stays put. Its CATEGORY leg does not
        // need to: holdings and lots derive only from legs carrying a
        // security_id (migration 068), and a security leg lives on the holdings
        // sibling, which no one browses — so recategorising a dividend touches
        // no holding at all. Refusing it left investment income stuck in
        // whatever category it imported under.
        //
        // The cash leg of an investment event that landed in a BANK stays
        // refused too, and that is not about holdings: the register treats such
        // a row as read-only there ("edit + delete belong on the brokerage
        // register"), and a move that bypassed it would contradict the surface.
        if (sourceDomain != "category")
        {
            var blocked = headers.Where(h => h.Action != null);
            if (InvestmentToBanking(sourceType, targetType))
            {
                var convertible = CashOnlyTransfers(headers, sourceAccountId).Select(h => h.Id);
                blocked = blocked.Where(h => !convertible.Contains(h.Id));
            }
            if (await blocked.AnyAsync(cancellationToken).ConfigureAwait(false))
                return MoveVerdict.InvestmentShape;
        }

        // posting_index > 0 ⇒ the header is a split. The "single banking leg"
        // half of the rule, and it binds BOTH directions: a split's postings
        // cannot all be re-pointed as one cash movement either way.
        var splits = headers.Where(h => _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.PostingIndex > 0));
        if (targetDomain == "investment"
            && await splits.AnyAsync(cancellationToken).ConfigureAwait(false))
            return MoveVerdict.SplitToInvestment;
        if (sourceDomain == "investment" && targetDomain == "banking"
            && await splits.AnyAsync(cancellationToken).ConfigureAwait(false))
            return MoveVerdict.SplitFromInvestment;

        // The target is already a leg on the transaction (a self-transfer).
        if (await headers.AnyAsync(
                h => _db.TxnLegs.Any(l => l.HeaderId == h.Id && l.AccountId == targetAccountId),
                cancellationToken).ConfigureAwait(false))
            return MoveVerdict.Collision;

        return MoveVerdict.Allowed;
    }

    /// <summary>
    /// The transfers this move will convert. Resolved BEFORE the legs move: a
    /// bulk selection is scoped to the source account, so asked afterwards it
    /// would match nothing and the conversion would silently not happen.
    /// </summary>
    private async Task<List<Guid>> TransfersToConvertAsync(
        IQueryable<TxnHeaderRow> headers, string sourceType, string targetType,
        Guid sourceAccountId, CancellationToken cancellationToken) =>
        InvestmentToBanking(sourceType, targetType)
            ? await CashOnlyTransfers(headers, sourceAccountId).Select(h => h.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

    /// <summary>
    /// Take the investment event off transfers that no longer touch an
    /// investment account: <c>action</c> on the header and <c>posting_role</c> on
    /// EVERY leg, together.
    /// </summary>
    /// <remarks>
    /// Both or neither — <c>posting_role IS NOT NULL ⇔ action IS NOT NULL</c>
    /// (migration 057). Its trigger was dropped in migration 084, so nothing in
    /// the database checks it any more; it holds only because every writer keeps
    /// it. The SPA's investment aggregator dispatches on posting_role, so a leg
    /// left carrying 'transfer' on a bank-to-bank transaction would read as half
    /// of an investment event with no other half.
    /// </remarks>
    private async Task ClearInvestmentEventAsync(
        IReadOnlyCollection<Guid> headerIds, CancellationToken cancellationToken)
    {
        if (headerIds.Count == 0) return;
        await _db.TxnLegs
            .Where(l => headerIds.Contains(l.HeaderId))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PostingRole, _ => (string?)null),
                cancellationToken).ConfigureAwait(false);
        await _db.TxnHeaders
            .Where(h => headerIds.Contains(h.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Action, _ => (string?)null),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Move the whole selection to <paramref name="targetAccountId"/> (ADR-0072
    /// D3). The selection is account-scoped — its AccountId is the source. The move rule
    /// (<see cref="CheckHeadersAsync"/>) and the relabel judgement
    /// (<see cref="RelabelCheck"/>, ADR-0114 D13, D15) are applied ALL-OR-NOTHING: if any
    /// selected row is refused, nothing moves. ExecuteUpdate bypasses the interceptor, so
    /// the balance recompute of source + target is explicit.
    /// </summary>
    public async Task<BulkMoveResult> BulkMoveAccountAsync(
        Guid ledgerId, SelectionRequest selection, Guid targetAccountId,
        CancellationToken cancellationToken = default)
    {
        // The ledger write lock (migration 275) before the checks, so what they read is
        // what the move writes over: no other recomputing write to the ledger lands
        // between them.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var target = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == targetAccountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.IsActive, a.CurrencyCode })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null)
            return BulkMoveOutcome.TargetInvalid;

        // A move is a RE-TARGET, and the inactive-account rule refuses those
        // onto a deactivated account: "new transactions and re-targeting
        // PATCHes cannot land on accounts that have been deactivated."
        // Only the picker enforced it, so MCP — or any caller that is not the
        // SPA — could move rows onto an account the user had retired, which is
        // the one direction the rule exists to prevent. The SOURCE is not
        // checked, deliberately: moving rows OFF a deactivated account is how
        // you clean one up, and refusing that would strand them there.
        if (!target.IsActive)
            return BulkMoveOutcome.TargetInactive;
        if (selection.AccountId is not { } sourceAccountId)
            return BulkMoveOutcome.SourceScopeRequired;
        if (sourceAccountId == targetAccountId)
            return BulkMoveOutcome.TargetSameAsSource;

        // "Target must not be a category" reads as a rule about targets and is
        // really half of a rule about PAIRS; the source half matters too. A
        // selection made on a CATEGORY's register carries the category as its
        // AccountId, so without it the move re-points the category leg onto a
        // money account and leaves the transaction with two money-account legs
        // and no category at all. Silent, and two clicks from that screen.
        var source = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == sourceAccountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.CurrencyCode })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
            return BulkMoveOutcome.SourceScopeRequired;
        var sourceType = source.AccountType;
        if (CrossesCategoryBoundary(sourceType, target.AccountType))
            return BulkMoveOutcome.AccountDomainCrossed;

        var selected = await BuildSelectionQueryAsync(ledgerId, selection, cancellationToken)
            .ConfigureAwait(false);
        if (selected is null) return BulkMoveOutcome.Moved;

        // The selection is read once, and every step below acts on exactly those headers:
        // re-evaluated, a select-all would also take a transaction committed after this
        // read, which the checks never saw and the recomputes below would not cover.
        var movedHeaderIds = await selected.Select(h => h.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var query = _db.TxnHeaders.Where(h => movedHeaderIds.Contains(h.Id));

        var verdict = await CheckHeadersAsync(
            query, sourceType, target.AccountType, sourceAccountId, targetAccountId,
            cancellationToken).ConfigureAwait(false);
        switch (verdict)
        {
            case MoveVerdict.InvestmentShape: return BulkMoveOutcome.InvestmentShape;
            case MoveVerdict.SplitToInvestment: return BulkMoveOutcome.SplitToInvestment;
            case MoveVerdict.SplitFromInvestment: return BulkMoveOutcome.SplitFromInvestment;
            case MoveVerdict.Collision: return BulkMoveOutcome.Collision;
        }

        // The whole selection is judged at once, so one row that cannot be relabelled
        // refuses them all.
        var relabel = await JudgeRelabelAsync(
            ledgerId, movedHeaderIds, sourceAccountId, source.CurrencyCode, target.CurrencyCode,
            cancellationToken).ConfigureAwait(false);
        if (relabel.Refused) return new BulkMoveResult(BulkMoveOutcome.RelabelRefused, Relabel: relabel);

        // Both read before the legs move — the selection is source-scoped.
        var affectedDates = await query
            .Select(h => h.PostedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var converting = await TransfersToConvertAsync(
            query, sourceType, target.AccountType, sourceAccountId,
            cancellationToken).ConfigureAwait(false);

        var moved = await _db.TxnLegs
            .Where(l => l.AccountId == sourceAccountId
                && query.Select(h => h.Id).Contains(l.HeaderId))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, _ => targetAccountId),
                cancellationToken).ConfigureAwait(false);
        // Every leg of the source moved, so a transaction it owned follows to the target
        // (mig 263) — including a cash transfer whose action the next line clears: the
        // bank now owns the plain one-split it became.
        await AnnotationWriter.FollowRepointAsync(
                _db, ledgerId, sourceAccountId, targetAccountId, movedHeaderIds, cancellationToken)
            .ConfigureAwait(false);
        await ClearInvestmentEventAsync(converting, cancellationToken).ConfigureAwait(false);
        // The legs, the owner and the action changed without the change tracker.
        await LegNotes.RecomputeAsync(_db, movedHeaderIds, cancellationToken).ConfigureAwait(false);

        if (affectedDates.Count > 0)
        {
            var pairs = affectedDates
                .SelectMany(d => new[] { (sourceAccountId, d), (targetAccountId, d) });
            await _balances.RecomputeAsync(pairs, cancellationToken).ConfigureAwait(false);
        }
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BulkMoveResult(BulkMoveOutcome.Moved, moved, relabel.BecameTwoAmount);
    }
}
