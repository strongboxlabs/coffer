using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read-only gateway to the <c>accounts</c> table. PR 3.7 only exposes
/// per-ledger listing; mutating operations (create / rename / archive)
/// land in a later PR alongside the UI surfaces that need them.
/// </summary>
/// <remarks>
/// Per-ledger filtering is the authoritative scope: the caller must have
/// already proven the user can see <paramref name="ledgerId"/> (the
/// endpoint does this via <see cref="LedgersRepository.GetVisibleByIdAsync"/>
/// before reaching the repo). Once Phase D RLS lands (PR 3.8), the
/// database itself enforces the same predicate as a defence-in-depth
/// layer. Until then, the WHERE clause here is the gate.
/// </remarks>
public sealed class AccountsRepository
{
    private readonly AppDbContext _db;
    private readonly LegDerivedRecomputeService _recompute;

    public AccountsRepository(AppDbContext db, LegDerivedRecomputeService recompute)
    {
        _db = db;
        _recompute = recompute;
    }

    /// <summary>
    /// User-facing accounts in the supplied ledger. Sorted by name
    /// (ascii, case-sensitive — matches Postgres default collation)
    /// so the picker UI gets a stable order without an extra client-
    /// side sort.
    /// </summary>
    /// <param name="includeInactive">When false (default), accounts
    /// with <c>is_active = false</c> are filtered out. Same layer as
    /// the holdings-sibling filter — keeps the endpoint's contract
    /// clean ("active user-facing accounts"). Set true for the SPA's
    /// "Show inactive" toggle and the account-settings dialog (which
    /// surfaces inactive accounts for re-activation). Pickers
    /// (transfer / category / fee / feed mapping) always use the
    /// default, getting clean lists for free.</param>
    public async Task<IReadOnlyList<AccountSummary>> ListByLedgerAsync(
        Guid ledgerId,
        bool includeInactive = false,
        CancellationToken cancellationToken = default) =>
        // Slice 2c.2 adds two derived fields:
        //   FeedConnectionId — already on the entity; surfaced so the
        //     mapping wizard can filter already-bound accounts.
        //   NeedsReviewCount — aggregated over txn_legs ↔ txn_headers
        //     for bank-feed rows flagged needs_review=true. Postgres
        //     translates the correlated Count() to a LATERAL JOIN; the
        //     partial index `txn_headers (ledger_id) WHERE needs_review`
        //     (migration 037) keeps the join cheap because only flagged
        //     headers participate.
        //
        // Holdings siblings (the system-managed shadow accounts that
        // hold per-security positions on every investment account)
        // are filtered out at this layer. They're internal accounting
        // machinery — not user-facing accounts — and exposing them
        // here leaked them into every picker (transfer destinations,
        // account groups, sidebar, etc.). Any view that legitimately
        // needs to address a holdings sibling does so by following
        // the brokerage's HoldingsAccountId pointer, NOT by listing
        // accounts. NOT EXISTS rather than a join so we don't accidentally
        // produce duplicate rows.
        //
        // Inactive accounts (is_active = false) filter at the same
        // layer (slice for ADR-0032 inactive-account lifecycle). Opt-in
        // via includeInactive=true for the SPA's "Show inactive"
        // toggle and the account-settings dialog.
        await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && (includeInactive || a.IsActive)
                        && !_db.Accounts.Any(o =>
                            o.LedgerId == ledgerId
                            && o.HoldingsAccountId == a.Id))
            .OrderBy(a => a.Name)
            .Select(a => new AccountSummary(
                a.Id,
                a.LedgerId,
                a.ParentId,
                a.Name,
                a.AccountType,
                a.CategoryKind,
                a.CurrencyCode,
                a.IsActive,
                a.IsSystem,
                a.FeedConnectionId,
                // Needs-review count via resolved_transactions so the
                // visibility gate is the override-aware effective value.
                _db.ResolvedTransactions.Count(rv => rv.AccountId == a.Id
                    && rv.NeedsReview
                    && !rv.IsHidden),
                a.HoldingsAccountId,
                a.IsTradeCommission,
                a.InstitutionName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Returns true when an account with <paramref name="accountId"/>
    /// exists *and* belongs to <paramref name="ledgerId"/>. The register
    /// endpoint uses this to reject cross-ledger account filters: without
    /// the check, a caller with access to ledger A could probe whether
    /// any given uuid is an account in ledger B by watching the response
    /// (empty vs. populated). 422 short-circuits before the page query
    /// runs.
    /// </summary>
    public async Task<bool> BelongsToLedgerAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default) =>
        await _db.Accounts.AsNoTracking()
            .AnyAsync(a => a.Id == accountId && a.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Most-used counterparties of <paramref name="accountId"/>,
    /// derived from transaction history (ADR-0043). For every
    /// non-hidden, non-merged header that touches the source account,
    /// the OTHER legs' accounts are ranked by how many of the source
    /// account's transactions posted against them, recency-weighted and
    /// diluted by split size (see below). Split into asset accounts vs
    /// categories; each list capped at <paramref name="perKind"/>.
    /// System placeholders (Uncategorized etc.) and inactive accounts
    /// are excluded — they're noise, not useful "frequent" picks. No
    /// usage table: a pure read over what the user has already booked,
    /// so it's always accurate and cacheable per (ledger, account).
    /// Dilution: each transaction contributes ~1 unit of ranking weight
    /// spread across the distinct counterparties the source touched on
    /// it, so a recurring multi-counterparty split (e.g. payroll) can't
    /// crowd out the counterparties picked on simple one-off
    /// transactions — which the split set rarely overlaps.
    /// </summary>
    // Recency tiers for the frecency score (ADR-0043). Recent use
    // outweighs old use so the picker tracks how the user banks now,
    // not five years ago. A heavily-used-then-abandoned counterparty
    // sinks below one used a few times lately.
    private const int RecencyWeightRecentDays = 90;
    private const int RecencyWeightRecent = 4;
    private const int RecencyWeightYearDays = 365;
    private const int RecencyWeightYear = 2;
    private const int RecencyWeightOlder = 1;

    public async Task<FrequentCounterpartiesResponse> GetFrequentCounterpartiesAsync(
        Guid ledgerId,
        Guid accountId,
        int perKind,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        // Pull one row per (counterparty, source-account transaction):
        // the counterparty meta + the header's posted date, for every
        // non-hidden/merged header touching the source account whose
        // counterparty is an active, non-system account. Volume is
        // bounded (one account's counterparties) and the read is
        // cached per (ledger, account), so materialising + scoring in
        // C# is fine — mirrors GetSimilarPayeesAsync.
        var rows = await (
            from leg in _db.TxnLegs.AsNoTracking()
            where leg.LedgerId == ledgerId
                  && leg.AccountId != accountId
                  // True counterparty = the leg sharing the source
                  // account's leg's posting_index (the symmetric-
                  // posting pair, same definition resolved_transactions
                  // uses). NOT just any leg on the header — on a
                  // paycheck split the 401(k) contribution posting
                  // pairs with the funding bank account; the tax /
                  // insurance / wage legs are separate postings on the
                  // same header and must NOT count as this account's
                  // counterparties (ADR-0043).
                  && _db.TxnLegs.Any(s => s.HeaderId == leg.HeaderId
                                          && s.AccountId == accountId
                                          && s.PostingIndex == leg.PostingIndex)
            join h in _db.TxnHeaders.AsNoTracking() on leg.HeaderId equals h.Id
            // Effective visibility (override-aware), not raw is_hidden.
            where (_db.TxnHeaderOverrides
                       .Where(o => o.HeaderId == h.Id)
                       .Select(o => (bool?)o.IsHidden).FirstOrDefault() ?? h.IsHidden) == false
                  && h.IsMergedInto == null
            join a in _db.Accounts.AsNoTracking() on leg.AccountId equals a.Id
            where a.IsActive && !a.IsSystem
            select new
            {
                a.Id,
                a.Name,
                a.AccountType,
                a.CategoryKind,
                HeaderId = h.Id,
                // Effective posted_at so recency weighting matches the register.
                PostedAt = _db.TxnHeaderOverrides
                    .Where(o => o.HeaderId == h.Id)
                    .Select(o => (DateTime?)o.PostedAt).FirstOrDefault() ?? h.PostedAt,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int Weight(DateTime postedAt)
        {
            var days = (asOfUtc - postedAt).TotalDays;
            if (days <= RecencyWeightRecentDays) return RecencyWeightRecent;
            if (days <= RecencyWeightYearDays) return RecencyWeightYear;
            return RecencyWeightOlder;
        }

        // Dilution: one transaction contributes ~1 unit of "intent" toward
        // counterparty ranking, spread across however many distinct
        // counterparties the source account touched on it. A singleton
        // (1 counterparty) carries full weight; an 8-way paycheck split
        // gives each of its categories 1/8. This keeps split-only
        // counterparties (e.g. payroll categories) from drowning out the
        // counterparties a user actually picks on simple one-off
        // transactions, which the split set rarely overlaps (ADR-0043).
        var splitSize = rows
            .GroupBy(r => r.HeaderId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().Count());

        var ranked = rows
            .GroupBy(r => r.Id)
            .Select(g =>
            {
                // Dedupe per header so a split touching the same
                // counterparty twice counts once.
                var byHeader = g
                    .GroupBy(x => x.HeaderId)
                    .Select(hg => hg.First())
                    .ToList();
                var first = g.First();
                return new
                {
                    first.Id,
                    first.Name,
                    first.AccountType,
                    first.CategoryKind,
                    // Honest raw usage for display; ranking uses the
                    // dilution-aware Score below.
                    UseCount = byHeader.Count,
                    Score = byHeader.Sum(x => (double)Weight(x.PostedAt) / splitSize[x.HeaderId]),
                    LastUsed = byHeader.Max(x => x.PostedAt),
                };
            })
            // Frecency: recency-weighted score first, then most-recent
            // use, then name for a stable tiebreak.
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.LastUsed)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<FrequentCounterpartyDto> Take(bool categories) => ranked
            .Where(r => categories
                ? r.AccountType == "category"
                : r.AccountType != "category")
            .Take(perKind)
            .Select(r => new FrequentCounterpartyDto(
                r.Id, r.Name, r.AccountType, r.CategoryKind, r.UseCount))
            .ToList();

        return new FrequentCounterpartiesResponse(
            Accounts: Take(categories: false),
            Categories: Take(categories: true));
    }

    /// <summary>
    /// Membership + activity for a batch of account ids in one round
    /// trip. Powers the inactive-account gate (PR #132 follow-up): the
    /// caller resolves "in this ledger?" and "currently active?" in a
    /// single query, avoiding N+1 against <see cref="BelongsToLedgerAsync"/>.
    /// Returns a dictionary keyed by the requested id; ids missing from
    /// the dictionary do not exist in this ledger.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AccountActivity>> LookupAccountActivityAsync(
        Guid ledgerId,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken = default)
    {
        if (accountIds.Count == 0)
            return new Dictionary<Guid, AccountActivity>();

        var rows = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && accountIds.Contains(a.Id))
            .Select(a => new { a.Id, a.IsActive })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => new AccountActivity(r.IsActive));
    }

    /// <summary>
    /// Membership + activity snapshot for one account. <c>Exists=false</c>
    /// (signalled by the dictionary miss in
    /// <see cref="LookupAccountActivityAsync"/>) maps to
    /// <c>account-not-in-ledger</c>; <c>IsActive=false</c> on an existing
    /// row maps to <c>account-inactive</c>.
    /// </summary>
    public readonly record struct AccountActivity(bool IsActive);

    /// <summary>
    /// True iff the account exists in this ledger AND its
    /// <c>account_type</c> is in the bank-shape set (i.e., anything
    /// the bank-shape <c>/transactions</c> endpoint supports as a
    /// source account: <c>bank</c>, <c>credit_card</c>, <c>cash</c>,
    /// <c>asset</c>, <c>liability</c>). Used by <c>/transactions</c>
    /// to assert positive identity at the request gate; investment-
    /// typed accounts fail this check and route the user to
    /// <c>/investment-transactions</c>.
    /// </summary>
    /// <remarks>
    /// This is the bank endpoint asking "is this an account I
    /// support?" — not "is this an investment account?" Each topic
    /// owns its own positive identity check; neither endpoint
    /// inspects the other's domain (ADR-0029).
    /// </remarks>
    public async Task<bool> IsBankShapeInLedgerAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default) =>
        await _db.Accounts.AsNoTracking()
            .AnyAsync(a => a.Id == accountId
                        && a.LedgerId == ledgerId
                        && (a.AccountType == "bank"
                         || a.AccountType == "credit_card"
                         || a.AccountType == "cash"
                         || a.AccountType == "asset"
                         || a.AccountType == "liability"), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Feed binding for one account — the pair the slice 2c.3
    /// per-account sync endpoint needs to dispatch a sync against
    /// the right connection narrowed to the right SimpleFIN
    /// account. Returns the binding when set, <c>null</c> when the
    /// account doesn't exist, doesn't belong to the ledger, or
    /// hasn't been bound to a feed yet.
    /// </summary>
    public sealed record FeedBinding(Guid FeedConnectionId, string ExternalId);

    /// <summary>
    /// Look up an account's <c>(feed_connection_id, external_id)</c>
    /// pair. Returns <c>null</c> when either column is null or the
    /// account doesn't belong to the ledger (RLS-equivalent guard).
    /// </summary>
    public async Task<FeedBinding?> GetFeedBindingAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default) =>
        await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId
                        && a.LedgerId == ledgerId
                        && a.FeedConnectionId != null
                        && a.ExternalId != null)
            .Select(a => new FeedBinding(a.FeedConnectionId!.Value, a.ExternalId!))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Outcome of <see cref="BindFeedMappingAsync"/>. Distinct
    /// codes so the API can surface a precise 422.
    /// </summary>
    public enum BindFeedMappingResult
    {
        Ok,
        /// <summary>The supplied feed_connection_id doesn't exist
        /// in this ledger.</summary>
        ConnectionMismatch,
    }

    /// <summary>
    /// Bind a Coffer account to one SimpleFIN-side account on a
    /// feed connection so future syncs route that
    /// <c>simplefin_account_id</c>'s transactions here
    /// (slice 2b — option 2 in the mapping design discussion).
    /// Idempotent: re-binding the same pair is a no-op.
    /// </summary>
    public async Task<BindFeedMappingResult> BindFeedMappingAsync(
        Guid ledgerId,
        Guid accountId,
        Guid feedConnectionId,
        string simpleFinAccountId,
        CancellationToken cancellationToken = default)
    {
        // Caller has already proven the user can see the ledger
        // and that the account belongs to it. Verify the
        // connection also lives in this ledger to prevent a
        // cross-ledger bind via a stolen connection id.
        var connectionInLedger = await _db.FeedConnections
            .AsNoTracking()
            .AnyAsync(c => c.Id == feedConnectionId && c.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (!connectionInLedger) return BindFeedMappingResult.ConnectionMismatch;

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.FeedConnectionId, feedConnectionId)
                .SetProperty(a => a.ExternalId, simpleFinAccountId),
                cancellationToken)
            .ConfigureAwait(false);
        return BindFeedMappingResult.Ok;
    }

    /// <summary>
    /// Clear the feed binding on one account (slice 2c.4). NULLs
    /// both <c>feed_connection_id</c> and <c>external_id</c> so the
    /// account drops out of all sync-time mapping lookups. Returns
    /// the count of rows affected — 0 when the account doesn't
    /// belong to the ledger (RLS-equivalent guard) or wasn't
    /// bound to begin with; 1 on success.
    ///
    /// <para>Caller has already proven the user can see the ledger
    /// and that the account belongs to it.</para>
    /// </summary>
    public async Task<int> UnbindFeedMappingAsync(
        Guid ledgerId,
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.FeedConnectionId, (Guid?)null)
                .SetProperty(a => a.ExternalId, (string?)null)
                // Slice 2c.5: re-mapping (to the same or a different
                // connection) should start a fresh 90-day window. The
                // old watermark belonged to the prior binding's data
                // flow and would silently narrow the new binding's
                // first sync.
                .SetProperty(a => a.LastSimpleFinSyncAt, (DateTime?)null),
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Outcome of <see cref="SetSyncFromDateAsync"/>. Distinct codes
    /// so the endpoint can surface a precise 422.
    /// </summary>
    public enum SetSyncFromDateResult
    {
        Ok,
        /// <summary>The account isn't bound to a SimpleFIN
        /// connection — there's no sync to anchor a watermark on.</summary>
        AccountNotBoundToFeed,
        /// <summary>The supplied date is in the future, which would
        /// silently narrow the next sync's window past now() — never
        /// what the user means by "sync from".</summary>
        DateInFuture,
    }

    /// <summary>
    /// Set the per-account SimpleFIN sync watermark (slice 2c.5).
    /// The next sync against this account's connection will ask
    /// SimpleFIN for transactions from <c>(syncFromDate − 7d)</c>
    /// forward — the same 7-day overlap the auto-watermark path
    /// uses. SimpleFIN caps history at 90 days, so a value older
    /// than that will be capped by the bank's response (the gen.api
    /// warning fires; sync still completes).
    ///
    /// <para>Pass <c>null</c> to clear the watermark — the next
    /// sync will then ask for the full 90 days.</para>
    /// </summary>
    public async Task<SetSyncFromDateResult> SetSyncFromDateAsync(
        Guid ledgerId,
        Guid accountId,
        DateTime? syncFromDate,
        CancellationToken cancellationToken = default)
    {
        if (syncFromDate is { } d && d > DateTime.UtcNow)
            return SetSyncFromDateResult.DateInFuture;

        // Account must be feed-bound for this to mean anything — a
        // watermark on an unmapped account is just a stale write.
        var isBound = await _db.Accounts
            .AsNoTracking()
            .AnyAsync(a => a.Id == accountId
                            && a.LedgerId == ledgerId
                            && a.FeedConnectionId != null
                            && a.ExternalId != null,
                cancellationToken)
            .ConfigureAwait(false);
        if (!isBound) return SetSyncFromDateResult.AccountNotBoundToFeed;

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastSimpleFinSyncAt, syncFromDate),
                cancellationToken)
            .ConfigureAwait(false);
        return SetSyncFromDateResult.Ok;
    }

    /// <summary>
    /// Outcome of <see cref="SetIsTradeCommissionAsync"/>. The CHECK
    /// constraint <c>accounts_is_trade_commission_only_on_investment</c>
    /// (migration 056) makes the flag meaningful only on investment
    /// accounts — every other type returns AccountNotInvestment.
    /// </summary>
    public enum SetIsTradeCommissionResult
    {
        Ok,
        AccountNotInvestment,
    }

    /// <summary>
    /// Slice A4.a: flip the per-brokerage <c>is_trade_commission</c>
    /// flag. When TRUE on an investment account, the recompute function
    /// adds fee-marked postings (<c>posting_role='fee'</c>) in that
    /// account's transactions to cost basis; when FALSE they're ignored.
    ///
    /// The flag's effect on existing data is realised by calling
    /// <c>recompute_holdings_cost_basis(ledgerId)</c> in the same
    /// transaction, so the caller sees converged <c>holdings.cost_basis</c>
    /// and <c>lots.unit_cost</c> by the time the response returns.
    /// </summary>
    public async Task<SetIsTradeCommissionResult> SetIsTradeCommissionAsync(
        Guid ledgerId,
        Guid accountId,
        bool isTradeCommission,
        CancellationToken cancellationToken = default)
    {
        var brokerage = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new { a.AccountType, a.HoldingsAccountId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (brokerage is null || brokerage.AccountType != "investment")
            return SetIsTradeCommissionResult.AccountNotInvestment;

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IsTradeCommission, isTradeCommission),
                cancellationToken)
            .ConfigureAwait(false);

        // Cost-basis recompute is now an explicit API call (ADR-0032,
        // migration 088). The old AFTER UPDATE trigger
        // trg_accounts_recompute_on_commission_flip was removed in
        // favor of this call site so the data flow ("flip the flag,
        // then refresh derived state") is visible in code.
        // Brokerage rows always have a holdings sibling (enforced by
        // schema on insert); skip the call defensively if it's null.
        if (brokerage.HoldingsAccountId is { } holdingsAccountId)
        {
            _ = await _db.RecomputeHoldingsForBrokerage(holdingsAccountId)
                .Select(r => r.RecomputedCount)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return SetIsTradeCommissionResult.Ok;
    }

    /// <summary>
    /// Outcome of <see cref="SetIsActiveAsync"/>.
    /// </summary>
    public enum SetIsActiveResult
    {
        Ok,
        AccountNotInLedger,
        AccountIsSystem,
    }

    /// <summary>
    /// Inactive-accounts slice: flip <c>accounts.is_active</c>. When
    /// set to false the account stays in the DB (historical
    /// transactions remain) but disappears from the default
    /// list endpoint, pickers (transfer / category / fee /
    /// feed-mapping), and the sidebar's default rendering. Re-
    /// activation (true) is symmetric. System accounts (holdings
    /// siblings, Uncategorized) are not user-deactivatable —
    /// returns <see cref="SetIsActiveResult.AccountIsSystem"/>.
    /// </summary>
    public async Task<SetIsActiveResult> SetIsActiveAsync(
        Guid ledgerId,
        Guid accountId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var account = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new { a.IsSystem })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return SetIsActiveResult.AccountNotInLedger;
        if (account.IsSystem) return SetIsActiveResult.AccountIsSystem;

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IsActive, isActive),
                cancellationToken)
            .ConfigureAwait(false);
        return SetIsActiveResult.Ok;
    }

    /// <summary>Name + type + parent + system flag for one account (or null). A
    /// lightweight single-row read for the MCP write tools' before-echo and guards,
    /// without the reporting repo's aggregate dependencies.</summary>
    public async Task<AccountBasics?> GetBasicAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default) =>
        await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == accountId)
            .Select(a => new AccountBasics(a.Name, a.AccountType, a.CategoryKind, a.ParentId, a.IsSystem))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public sealed record AccountBasics(
        string Name, string AccountType, string? CategoryKind, Guid? ParentId, bool IsSystem);

    // ---- ADR-0068: category merge + delete (MCP write surface; REST-shared) -------

    /// <summary>
    /// All categories in the ledger for the management tree (Slice A), each with
    /// the usage counts the UI needs: <c>TransactionCount</c> (legs posting to it)
    /// and <c>ChildCount</c> (sub-categories). Counts mirror the
    /// <see cref="DeleteCategoryAsync"/> gate — every referencing leg and every
    /// child, regardless of active state — so the UI can pre-disable Delete while
    /// the server stays authoritative. <paramref name="includeInactive"/> only
    /// filters which categories are returned, not how children are counted.
    /// </summary>
    public async Task<IReadOnlyList<CategoryNode>> ListCategoriesWithUsageAsync(
        Guid ledgerId, bool includeInactive, CancellationToken cancellationToken = default)
    {
        var cats = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category"
                        && (includeInactive || a.IsActive))
            .Select(a => new { a.Id, a.Name, a.CategoryKind, a.ParentId, a.IsActive, a.IsSystem })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Per-category leg count AND signed amount sum in one grouped pass —
        // same WHERE as the delete gate (every referencing leg), so the
        // displayed total + count stay consistent with what blocks a delete.
        var txnAggs = await _db.TxnLegs.AsNoTracking()
            .Where(l => l.LedgerId == ledgerId)
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count(), Total = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => (x.Count, x.Total), cancellationToken)
            .ConfigureAwait(false);

        var childCounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.ParentId != null)
            .GroupBy(a => a.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        return cats
            .Select(c =>
            {
                var (count, total) = txnAggs.GetValueOrDefault(c.Id);
                return new CategoryNode(
                    c.Id, c.Name, c.CategoryKind!, c.ParentId, c.IsActive, c.IsSystem,
                    count, childCounts.GetValueOrDefault(c.Id), total);
            })
            .ToList();
    }

    /// <summary>Outcome of <see cref="MergeCategoryAsync"/>.</summary>
    public enum MergeCategoryResult
    {
        Ok, SourceNotInLedger, TargetNotInLedger, NotCategory, KindMismatch,
        SameCategory, SourceIsSystem,
    }

    /// <summary>Counts moved by a (non-dry-run) merge, for the API / MCP echo.</summary>
    public sealed record MergeCategoryOutcome(
        MergeCategoryResult Result, int TransactionsMoved, int ChildrenReparented);

    /// <summary>
    /// Merge category <paramref name="sourceId"/> into <paramref name="targetId"/>
    /// (ADR-0068). Repoints every leg that references the source — committed
    /// transactions AND recurring-template legs (reminder category postings live on
    /// their template <c>txn_legs</c>) — to the target, reparents the source's child
    /// categories to the target, then <b>deactivates</b> the now-empty source
    /// (reversible; the row + its history are preserved, not deleted). Both must be
    /// categories of the same <c>category_kind</c> (income↔income / expense↔expense)
    /// in the ledger; the source must not be system-managed. Maintained balances for
    /// both categories are rebuilt. <paramref name="dryRun"/> reports the counts that
    /// would move without writing. Atomic.
    /// </summary>
    public async Task<MergeCategoryOutcome> MergeCategoryAsync(
        Guid ledgerId, Guid sourceId, Guid targetId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == targetId)
            return new MergeCategoryOutcome(MergeCategoryResult.SameCategory, 0, 0);

        var source = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == sourceId)
            .Select(a => new { a.AccountType, a.CategoryKind, a.IsSystem })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
            return new MergeCategoryOutcome(MergeCategoryResult.SourceNotInLedger, 0, 0);

        var target = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == targetId)
            .Select(a => new { a.AccountType, a.CategoryKind })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null)
            return new MergeCategoryOutcome(MergeCategoryResult.TargetNotInLedger, 0, 0);

        if (source.AccountType != "category" || target.AccountType != "category")
            return new MergeCategoryOutcome(MergeCategoryResult.NotCategory, 0, 0);
        if (source.IsSystem)
            return new MergeCategoryOutcome(MergeCategoryResult.SourceIsSystem, 0, 0);
        if (source.CategoryKind != target.CategoryKind)
            return new MergeCategoryOutcome(MergeCategoryResult.KindMismatch, 0, 0);

        var txnCount = await _db.TxnLegs
            .Where(l => l.LedgerId == ledgerId && l.AccountId == sourceId)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        var childCount = await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.ParentId == sourceId)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        if (dryRun)
            return new MergeCategoryOutcome(MergeCategoryResult.Ok, txnCount, childCount);

        // Capture the affected legs' headers + EFFECTIVE posted_at
        // (COALESCE(override, header) — mig 103) BEFORE the move: the recompute
        // anchors on the effective date, and ExecuteUpdate bypasses the
        // ChangeTracker so the LegDerivedRecompute interceptor never sees it — the
        // same explicit-call-site pattern as BulkTransactionsRepository.
        var moved = await (
            from l in _db.TxnLegs.AsNoTracking()
            where l.LedgerId == ledgerId && l.AccountId == sourceId
            join h in _db.TxnHeaders on l.HeaderId equals h.Id
            select new
            {
                l.HeaderId,
                PostedAt = _db.TxnHeaderOverrides
                    .Where(o => o.HeaderId == h.Id)
                    .Select(o => (DateTime?)o.PostedAt)
                    .FirstOrDefault() ?? h.PostedAt,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Repoint every referencing leg (committed txns + reminder templates) → target.
        await _db.TxnLegs
            .Where(l => l.LedgerId == ledgerId && l.AccountId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, targetId), cancellationToken)
            .ConfigureAwait(false);

        // Reparent the source's child categories to the target.
        await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.ParentId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ParentId, (Guid?)targetId), cancellationToken)
            .ConfigureAwait(false);

        // Deactivate the now-empty source (reversible; preserves the row).
        await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsActive, false), cancellationToken)
            .ConfigureAwait(false);

        // Re-derive BOTH leg-derived denormalizations via the canonical service
        // (ADR-0034 / ADR-0036): running balances for the source (legs left) and the
        // target (legs arrived), and the posting counts on every touched header (a
        // moved leg changes which postings touch the source / target on that header).
        var anchors = moved.Select(m => (sourceId, m.PostedAt))
            .Concat(moved.Select(m => (targetId, m.PostedAt)));
        await _recompute.RecomputeAsync(anchors, cancellationToken).ConfigureAwait(false);
        await _recompute.RecomputePostingCountsAsync(
            moved.Select(m => m.HeaderId), cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MergeCategoryOutcome(MergeCategoryResult.Ok, txnCount, childCount);
    }

    /// <summary>Outcome of <see cref="DeleteCategoryAsync"/>.</summary>
    public enum DeleteCategoryResult { Ok, NotInLedger, NotCategory, IsSystem, InUse }

    public sealed record DeleteCategoryOutcome(
        DeleteCategoryResult Result, int TransactionCount, int ChildCount);

    /// <summary>
    /// Hard-delete an EMPTY category (ADR-0068): only when it has zero referencing
    /// legs and zero child categories and is not system-managed; otherwise returns
    /// <see cref="DeleteCategoryResult.InUse"/> (relocate its transactions with
    /// <see cref="MergeCategoryAsync"/> first). <paramref name="dryRun"/> reports
    /// deletability without writing.
    /// </summary>
    public async Task<DeleteCategoryOutcome> DeleteCategoryAsync(
        Guid ledgerId, Guid categoryId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var cat = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == categoryId)
            .Select(a => new { a.AccountType, a.IsSystem })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (cat is null)
            return new DeleteCategoryOutcome(DeleteCategoryResult.NotInLedger, 0, 0);
        if (cat.AccountType != "category")
            return new DeleteCategoryOutcome(DeleteCategoryResult.NotCategory, 0, 0);
        if (cat.IsSystem)
            return new DeleteCategoryOutcome(DeleteCategoryResult.IsSystem, 0, 0);

        var txnCount = await _db.TxnLegs
            .Where(l => l.LedgerId == ledgerId && l.AccountId == categoryId)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        var childCount = await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.ParentId == categoryId)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        if (txnCount > 0 || childCount > 0)
            return new DeleteCategoryOutcome(DeleteCategoryResult.InUse, txnCount, childCount);

        if (dryRun)
            return new DeleteCategoryOutcome(DeleteCategoryResult.Ok, 0, 0);

        await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.Id == categoryId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return new DeleteCategoryOutcome(DeleteCategoryResult.Ok, 0, 0);
    }

    /// <summary>Outcome of <see cref="ReparentCategoryAsync"/>.</summary>
    public enum ReparentCategoryResult
    {
        Ok, NotInLedger, NotCategory, IsSystem, ParentNotCategory, WouldCycle, SameParent,
    }

    /// <summary>
    /// Move category <paramref name="categoryId"/> under <paramref name="newParentId"/>
    /// (or to the root when null) — ADR-0068. Guards: it's a non-system category in the
    /// ledger; the new parent is a category in the same ledger; the move doesn't create a
    /// cycle (parent is not the category itself or one of its descendants). <paramref
    /// name="dryRun"/> validates without writing.
    /// </summary>
    public async Task<ReparentCategoryResult> ReparentCategoryAsync(
        Guid ledgerId, Guid categoryId, Guid? newParentId, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var cat = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == categoryId)
            .Select(a => new { a.AccountType, a.IsSystem, a.ParentId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (cat is null) return ReparentCategoryResult.NotInLedger;
        if (cat.AccountType != "category") return ReparentCategoryResult.NotCategory;
        if (cat.IsSystem) return ReparentCategoryResult.IsSystem;
        if (newParentId == cat.ParentId) return ReparentCategoryResult.SameParent; // no-op (incl. both null)

        if (newParentId is { } pid)
        {
            if (pid == categoryId) return ReparentCategoryResult.WouldCycle;
            var parent = await _db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledgerId && a.Id == pid)
                .Select(a => new { a.AccountType, a.ParentId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.AccountType != "category")
                return ReparentCategoryResult.ParentNotCategory;

            // Cycle guard: walk up from the new parent; reaching categoryId means the move
            // would close a loop. Category trees are shallow; bound the walk defensively.
            var ancestor = parent.ParentId;
            for (var hops = 0; ancestor is { } a && hops < 64; hops++)
            {
                if (a == categoryId) return ReparentCategoryResult.WouldCycle;
                ancestor = await _db.Accounts.AsNoTracking()
                    .Where(x => x.LedgerId == ledgerId && x.Id == a)
                    .Select(x => x.ParentId)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (dryRun) return ReparentCategoryResult.Ok;

        await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.Id == categoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ParentId, newParentId), cancellationToken)
            .ConfigureAwait(false);
        return ReparentCategoryResult.Ok;
    }

    // ADR-0050: account create + edit. The discriminator catalog (ADR-0017):
    // real accounts carry no category_kind; a category requires one.
    private static readonly HashSet<string> RealAccountTypes = new()
    {
        "bank", "credit_card", "investment", "asset", "liability", "loan",
    };

    public enum CreateAccountFailure
    {
        None, NameRequired, TypeInvalid, CategoryKindInvalid, CurrencyInvalid, ParentInvalid,
        OpeningBalanceInvalid, LoanTermsRequired, LoanTermsInvalid, LoanTermsNotAllowed,
    }

    public sealed record CreateAccountOutcome(CreateAccountFailure Failure, AccountSummary? Account);

    /// <summary>
    /// Create an account of any type (ADR-0050). Enforces the ADR-0017
    /// discriminator invariants (category ⇔ category_kind) and the
    /// category-only parent rule. An <c>investment</c> account also gets its
    /// system-managed Holdings sibling (ADR-0019), mirroring the importer's
    /// <c>EnsureHoldingsSiblingAsync</c> in the EF layer, with the
    /// <c>holdings_account_id</c> FK wired in the same transaction.
    /// </summary>
    public async Task<CreateAccountOutcome> CreateAsync(
        Guid ledgerId, CreateAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return new(CreateAccountFailure.NameRequired, null);

        var type = request.AccountType?.Trim() ?? string.Empty;
        var isCategory = type == "category";
        if (!isCategory && !RealAccountTypes.Contains(type))
            return new(CreateAccountFailure.TypeInvalid, null);

        // Category-kind invariant (ADR-0017): set IFF category.
        string? categoryKind = null;
        if (isCategory)
        {
            categoryKind = request.CategoryKind?.Trim();
            if (categoryKind is not ("income" or "expense"))
                return new(CreateAccountFailure.CategoryKindInvalid, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.CategoryKind))
        {
            return new(CreateAccountFailure.CategoryKindInvalid, null);
        }

        var currency = string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? "USD" : request.CurrencyCode.Trim().ToUpperInvariant();
        if (currency.Length != 3) return new(CreateAccountFailure.CurrencyInvalid, null);

        // Only a category may have a parent, and it must itself be a category here.
        Guid? parentId = null;
        if (request.ParentId is { } pid)
        {
            if (!isCategory) return new(CreateAccountFailure.ParentInvalid, null);
            var parentOk = await _db.Accounts.AsNoTracking().AnyAsync(
                a => a.Id == pid && a.LedgerId == ledgerId && a.AccountType == "category",
                cancellationToken).ConfigureAwait(false);
            if (!parentOk) return new(CreateAccountFailure.ParentInvalid, null);
            parentId = pid;
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        var institution = Clean(request.InstitutionName);
        var accountNumber = Clean(request.AccountNumber);
        var routingNumber = Clean(request.RoutingNumber);
        var accountUrl = Clean(request.AccountUrl);
        var notes = Clean(request.Notes);

        // Opening balance: categories carry none (DB CHECK forces 0).
        if (isCategory && request.OpeningBalance != 0m)
            return new(CreateAccountFailure.OpeningBalanceInvalid, null);
        var openingBalance = isCategory ? 0m : request.OpeningBalance;

        // Loan terms: REQUIRED on loan accounts (user decision), forbidden on
        // every other type (ADR-0050 slice 3).
        if (type == "loan")
        {
            if (request.LoanTerms is not { } t) return new(CreateAccountFailure.LoanTermsRequired, null);
            if (!LoanTermsAreValid(t)
                || !await LoanTermAccountsValidAsync(ledgerId, t, cancellationToken).ConfigureAwait(false))
                return new(CreateAccountFailure.LoanTermsInvalid, null);
        }
        else if (request.LoanTerms is not null)
        {
            return new(CreateAccountFailure.LoanTermsNotAllowed, null);
        }

        var accountId = Guid.NewGuid();
        Guid? holdingsSiblingId = null;

        // Account first, then loan_terms (which FK-references it) — one
        // transaction so a loan account + its terms commit atomically.
        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (type == "investment")
        {
            holdingsSiblingId = Guid.NewGuid();
            _db.Accounts.Add(new AccountRow
            {
                Id = holdingsSiblingId.Value,
                LedgerId = ledgerId,
                ParentId = null,
                Name = $"{name} Holdings",
                AccountType = "investment",
                CategoryKind = null,
                CurrencyCode = currency,
                OpeningBalance = 0m,
                IsActive = true,
                ExternalId = null,
                IsSystem = true,
                HoldingsAccountId = null,
                CreatedAt = DateTime.UtcNow,
            });
        }

        _db.Accounts.Add(new AccountRow
        {
            Id = accountId,
            LedgerId = ledgerId,
            ParentId = parentId,
            Name = name,
            AccountType = type,
            CategoryKind = categoryKind,
            CurrencyCode = currency,
            OpeningBalance = openingBalance,
            OpenedOn = request.OpenedOn,
            IsActive = request.IsActive,
            ExternalId = null,
            IsSystem = false,
            HoldingsAccountId = holdingsSiblingId,
            InstitutionName = institution,
            AccountNumber = accountNumber,
            RoutingNumber = routingNumber,
            AccountUrl = accountUrl,
            Notes = notes,
            CreatedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (type == "loan" && request.LoanTerms is { } terms)
        {
            _db.LoanTerms.Add(BuildLoanTermsRow(accountId, ledgerId, terms));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        var summary = new AccountSummary(
            accountId, ledgerId, parentId, name, type, categoryKind, currency,
            request.IsActive, false, null, 0, holdingsSiblingId, false, institution);
        return new(CreateAccountFailure.None, summary);
    }

    // ----- loan-terms write helpers (ADR-0050 slice 3) ----------------------

    /// <summary>Field-level validity, mirroring the DB CHECKs on
    /// <c>loan_terms</c>. A non-computed payment requires a positive fixed
    /// payment.</summary>
    private static bool LoanTermsAreValid(LoanTermsDto t) =>
        t.OriginalPrincipal > 0m
        && t.AnnualInterestRate >= 0m
        && t.Points >= 0m
        && t.PaymentCount > 0
        && t.PaymentsPerYear > 0
        && (t.PaymentIsComputed || (t.FixedPayment is { } fp && fp > 0m));

    /// <summary>The interest / escrow target accounts (when set) must belong to
    /// this ledger — the DB composite FK enforces it too, but this returns a
    /// clean 422 instead of a constraint violation.</summary>
    private async Task<bool> LoanTermAccountsValidAsync(
        Guid ledgerId, LoanTermsDto t, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        if (t.InterestAccountId is { } i) ids.Add(i);
        if (t.EscrowAccountId is { } e) ids.Add(e);
        if (ids.Count == 0) return true;
        var found = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && ids.Contains(a.Id))
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.All(found.Contains);
    }

    private static LoanTermsRow BuildLoanTermsRow(Guid accountId, Guid ledgerId, LoanTermsDto t) => new()
    {
        AccountId = accountId,
        LedgerId = ledgerId,
        OriginalPrincipal = t.OriginalPrincipal,
        AnnualInterestRate = t.AnnualInterestRate,
        Points = t.Points,
        PaymentCount = t.PaymentCount,
        PaymentsPerYear = t.PaymentsPerYear,
        FirstPaymentDate = t.FirstPaymentDate,
        EscrowAmount = t.EscrowAmount,
        InterestAccountId = t.InterestAccountId,
        EscrowAccountId = t.EscrowAccountId,
        PaymentIsComputed = t.PaymentIsComputed,
        // Ignore any fixed payment when the payment is computed (keeps the row
        // consistent with payment_is_computed).
        FixedPayment = t.PaymentIsComputed ? null : t.FixedPayment,
    };

    public enum UpdateAccountResult
    {
        Ok, NotInLedger, IsSystem, PatchEmpty, NameRequired, CategoryKindInvalid, CurrencyInvalid,
        OpeningBalanceInvalid, LoanTermsInvalid, LoanTermsNotAllowed, TaxStatusInvalid,
    }

    /// <summary>
    /// Edit an account's general attributes (ADR-0050). PARTIAL: a null scalar
    /// leaves the field unchanged. <c>account_type</c> is immutable (not in the
    /// request) — changing it would invalidate register rendering / holdings /
    /// existing postings. System accounts are not editable. Applied as one
    /// <c>ExecuteUpdateAsync</c> (no tracked mutation — the row type is
    /// init-only, matching <see cref="SetIsActiveAsync"/>).
    /// </summary>
    public async Task<UpdateAccountResult> UpdateAsync(
        Guid ledgerId, Guid accountId, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cur = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new
            {
                a.Name, a.AccountType, a.CategoryKind, a.CurrencyCode, a.InstitutionName,
                a.AccountNumber, a.RoutingNumber, a.AccountUrl, a.Notes, a.IsActive, a.IsSystem,
                a.OpeningBalance, a.OpenedOn, a.TaxStatus,
            })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (cur is null) return UpdateAccountResult.NotInLedger;
        if (cur.IsSystem) return UpdateAccountResult.IsSystem;

        var touches = request.Name is not null
            || request.CurrencyCode is not null
            || request.InstitutionName is not null
            || request.AccountNumber is not null
            || request.RoutingNumber is not null
            || request.AccountUrl is not null
            || request.Notes is not null
            || request.IsActive is not null
            || request.CategoryKind is not null
            || request.OpeningBalance is not null
            || request.OpenedOn is not null
            || request.ClearOpenedOn
            || request.LoanTerms is not null
            || request.TaxStatus is not null;
        if (!touches) return UpdateAccountResult.PatchEmpty;

        var name = cur.Name;
        if (request.Name is not null)
        {
            var trimmed = request.Name.Trim();
            if (trimmed.Length == 0) return UpdateAccountResult.NameRequired;
            name = trimmed;
        }

        var currency = cur.CurrencyCode;
        if (request.CurrencyCode is not null)
        {
            var c = request.CurrencyCode.Trim().ToUpperInvariant();
            if (c.Length != 3) return UpdateAccountResult.CurrencyInvalid;
            currency = c;
        }

        var categoryKind = cur.CategoryKind;
        if (request.CategoryKind is not null)
        {
            if (cur.AccountType != "category") return UpdateAccountResult.CategoryKindInvalid;
            var k = request.CategoryKind.Trim();
            if (k is not ("income" or "expense")) return UpdateAccountResult.CategoryKindInvalid;
            categoryKind = k;
        }

        // Text metadata: a provided value (incl. "") sets it (blank → null);
        // a null property leaves the field unchanged.
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
        var institution = request.InstitutionName is not null ? Clean(request.InstitutionName) : cur.InstitutionName;
        var accountNumber = request.AccountNumber is not null ? Clean(request.AccountNumber) : cur.AccountNumber;
        var routingNumber = request.RoutingNumber is not null ? Clean(request.RoutingNumber) : cur.RoutingNumber;
        var accountUrl = request.AccountUrl is not null ? Clean(request.AccountUrl) : cur.AccountUrl;
        var notes = request.Notes is not null ? Clean(request.Notes) : cur.Notes;

        // tax_status (ADR-0066): null = unchanged; "" clears; else validate the
        // enum (the DB CHECK is the backstop).
        var taxStatus = cur.TaxStatus;
        if (request.TaxStatus is not null)
        {
            var t = Clean(request.TaxStatus);
            if (t is not (null or "taxable" or "tax_deferred" or "tax_free" or "other"))
                return UpdateAccountResult.TaxStatusInvalid;
            taxStatus = t;
        }

        var isActive = request.IsActive ?? cur.IsActive;

        // Opening balance: a provided value wins; categories must stay 0.
        var openingBalance = cur.OpeningBalance;
        if (request.OpeningBalance is { } ob)
        {
            if (cur.AccountType == "category" && ob != 0m) return UpdateAccountResult.OpeningBalanceInvalid;
            openingBalance = ob;
        }

        // Opened-on: explicit clear wins; else a provided value; else unchanged.
        var openedOn = request.ClearOpenedOn ? (DateOnly?)null : (request.OpenedOn ?? cur.OpenedOn);

        // Loan terms: only on loan accounts; validated before any write.
        if (request.LoanTerms is { } terms)
        {
            if (cur.AccountType != "loan") return UpdateAccountResult.LoanTermsNotAllowed;
            if (!LoanTermsAreValid(terms)
                || !await LoanTermAccountsValidAsync(ledgerId, terms, cancellationToken).ConfigureAwait(false))
                return UpdateAccountResult.LoanTermsInvalid;
        }

        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Name, name)
                .SetProperty(a => a.CurrencyCode, currency)
                .SetProperty(a => a.InstitutionName, institution)
                .SetProperty(a => a.AccountNumber, accountNumber)
                .SetProperty(a => a.RoutingNumber, routingNumber)
                .SetProperty(a => a.AccountUrl, accountUrl)
                .SetProperty(a => a.Notes, notes)
                .SetProperty(a => a.CategoryKind, categoryKind)
                .SetProperty(a => a.IsActive, isActive)
                .SetProperty(a => a.OpeningBalance, openingBalance)
                .SetProperty(a => a.OpenedOn, openedOn)
                .SetProperty(a => a.TaxStatus, taxStatus),
                cancellationToken)
            .ConfigureAwait(false);

        if (request.LoanTerms is { } loanTerms)
            await UpsertLoanTermsAsync(ledgerId, accountId, loanTerms, cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return UpdateAccountResult.Ok;
    }

    /// <summary>Insert or update the loan account's <c>loan_terms</c> row
    /// (ADR-0050 slice 3). The account already exists, so the FK is satisfied;
    /// the caller wraps this in its transaction.</summary>
    private async Task UpsertLoanTermsAsync(
        Guid ledgerId, Guid accountId, LoanTermsDto t, CancellationToken cancellationToken)
    {
        var fixedPayment = t.PaymentIsComputed ? (decimal?)null : t.FixedPayment;

        var exists = await _db.LoanTerms.AsNoTracking()
            .AnyAsync(lt => lt.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            _db.LoanTerms.Add(BuildLoanTermsRow(accountId, ledgerId, t));
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await _db.LoanTerms
            .Where(lt => lt.AccountId == accountId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OriginalPrincipal, t.OriginalPrincipal)
                .SetProperty(x => x.AnnualInterestRate, t.AnnualInterestRate)
                .SetProperty(x => x.Points, t.Points)
                .SetProperty(x => x.PaymentCount, t.PaymentCount)
                .SetProperty(x => x.PaymentsPerYear, t.PaymentsPerYear)
                .SetProperty(x => x.FirstPaymentDate, t.FirstPaymentDate)
                .SetProperty(x => x.EscrowAmount, t.EscrowAmount)
                .SetProperty(x => x.InterestAccountId, t.InterestAccountId)
                .SetProperty(x => x.EscrowAccountId, t.EscrowAccountId)
                .SetProperty(x => x.PaymentIsComputed, t.PaymentIsComputed)
                .SetProperty(x => x.FixedPayment, fixedPayment),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Full editable shape of one account (ADR-0050) for the editor's edit
    /// mode — includes the metadata the lean <see cref="AccountSummary"/>
    /// omits. Null when the account isn't in this ledger.
    /// </summary>
    public async Task<AccountDetail?> GetDetailAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default) =>
        await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new AccountDetail(
                a.Id, a.LedgerId, a.ParentId, a.Name, a.AccountType, a.CategoryKind,
                a.CurrencyCode, a.IsActive, a.IsSystem, a.InstitutionName,
                a.AccountNumber, a.RoutingNumber, a.AccountUrl, a.Notes,
                a.OpeningBalance, a.OpenedOn, a.TaxStatus,
                // Loan accounts: their loan_terms row (null otherwise).
                _db.LoanTerms.Where(lt => lt.AccountId == a.Id)
                    .Select(lt => new LoanTermsDto
                    {
                        OriginalPrincipal = lt.OriginalPrincipal,
                        AnnualInterestRate = lt.AnnualInterestRate,
                        Points = lt.Points,
                        PaymentCount = lt.PaymentCount,
                        PaymentsPerYear = lt.PaymentsPerYear,
                        FirstPaymentDate = lt.FirstPaymentDate,
                        EscrowAmount = lt.EscrowAmount,
                        InterestAccountId = lt.InterestAccountId,
                        EscrowAccountId = lt.EscrowAccountId,
                        PaymentIsComputed = lt.PaymentIsComputed,
                        FixedPayment = lt.FixedPayment,
                    })
                    .FirstOrDefault(),
                // Managed payment reminder (mig 168): the active loan reminder
                // linked to this loan account, if one is set up.
                _db.RecurringTransactions
                    .Where(r => r.LoanAccountId == a.Id && r.IsActive)
                    .Select(r => new ManagedReminderDto(r.Id, r.Rrule, r.NextDueDate))
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
