using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Quotes;
using Coffer.Domain.Money;

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
    private readonly TimeProvider _clock;

    public AccountsRepository(AppDbContext db, LegDerivedRecomputeService recompute, TimeProvider clock)
    {
        _db = db;
        _recompute = recompute;
        _clock = clock;
    }

    /// <summary>
    /// Whether a non-category account of the ledger other than
    /// <paramref name="excluding"/> is kept in <paramref name="currencyCode"/>: the test for
    /// an account created or edited "into a new currency", which starts a market-series
    /// backfill (ADR-0114 D11).
    /// </summary>
    public Task<bool> CurrencyHeldByAnotherAccountAsync(
        Guid ledgerId, string currencyCode, IReadOnlyCollection<Guid> excluding,
        CancellationToken cancellationToken = default) =>
        _db.Accounts.AsNoTracking()
            .AnyAsync(a => a.LedgerId == ledgerId
                           && a.AccountType != "category"
                           && a.CurrencyCode == currencyCode
                           && !excluding.Contains(a.Id),
                cancellationToken);

    /// <summary>One account's currency, type and holdings sibling, or null when it is not
    /// in the ledger: what an edit had before it (ADR-0114 D11).</summary>
    public async Task<(string CurrencyCode, string AccountType, Guid? HoldingsAccountId)?> CurrencyOfAsync(
        Guid ledgerId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var row = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.Id == accountId)
            .Select(a => new { a.CurrencyCode, a.AccountType, a.HoldingsAccountId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : (row.CurrencyCode, row.AccountType, row.HoldingsAccountId);
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
        CancellationToken cancellationToken = default)
    {
        // Two derived fields:
        //   FeedConnectionId — already on the entity; surfaced so the
        //     mapping wizard can filter already-bound accounts.
        //   NeedsReviewCount — the account's register entries awaiting review,
        //     by the register's own definition (below).
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
        //
        // The review count is the register's: the legs its needs_review
        // status keeps on the visible side (RegisterQuery.Filtered, here over
        // every account and tied to each by the correlation), counted as
        // DISTINCT ENTRIES the way the register's status count counts them. A
        // split on its own account is one entry; each of its lines on another
        // account is an entry there, so a split with two lines on one other
        // account counts two on it, as that register lists two rows. The
        // visibility terms (not hidden, not merged) are the register's too: a
        // merge and a delete clear the flag, and the terms keep out a row that
        // is still flagged while hidden or merged, which only data written
        // before those writers cleared it can be (ADR-0052 D3). One correlated
        // subquery per account in the same statement, never a read per account.
        var awaitingReview = RegisterQuery.FilteredOnEveryAccount(
            _db, ledgerId, hidden: false, AwaitingReview, ScheduledBoundary.Today(null, _clock));
        return await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && (includeInactive || a.IsActive))
            .Where(NotAHoldingsSibling(_db, ledgerId))
            // Names need not be unique: the id breaks a tie, so two reads list them alike.
            .OrderBy(a => a.Name)
            .ThenBy(a => a.Id)
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
                awaitingReview
                    .Where(l => l.Row.AccountId == a.Id)
                    .Select(l => l.EntryKey)
                    .Distinct()
                    .Count(),
                a.HoldingsAccountId,
                a.IsTradeCommission,
                a.InstitutionName,
                a.ImportProviderKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The register's needs-review view, the review count's definition.</summary>
    private static readonly RegisterFilter AwaitingReview = new(Status: "needs_review");

    /// <summary>
    /// An account of <paramref name="ledgerId"/> that is not a holdings sibling: no
    /// account of the ledger names it as its <c>holdings_account_id</c>. The one test
    /// for "an account a person reads", shared by this listing and the ledger-wide
    /// register entries (<see cref="RegisterRepository.GetLedgerPageAsync"/>), so the
    /// entries never show an account the sidebar does not list.
    /// </summary>
    internal static Expression<Func<AccountRow, bool>> NotAHoldingsSibling(AppDbContext db, Guid ledgerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return a => !db.Accounts.Any(o => o.LedgerId == ledgerId && o.HoldingsAccountId == a.Id);
    }

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
            // is_hidden is canonical since mig 230 — no override layer sits
            // over it.
            where h.IsHidden == false
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
                PostedAt = h.PostedAt,
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
            .ThenBy(r => r.Id)
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
    /// trip. Powers the inactive-account gate: the
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
            .Select(a => new { a.Id, a.IsActive, a.AccountType, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // A Holdings sibling's stored currency is its brokerage's, which
        // HoldingsAccountCurrency.AlignAsync keeps (ADR-0019 Rule 4).
        return rows.ToDictionary(r => r.Id, r => new AccountActivity(r.IsActive, r.AccountType, r.CurrencyCode));
    }

    /// <summary>
    /// Membership + activity snapshot for one account. <c>Exists=false</c>
    /// (signalled by the dictionary miss in
    /// <see cref="LookupAccountActivityAsync"/>) maps to
    /// <c>account-not-in-ledger</c>; <c>IsActive=false</c> on an existing
    /// row maps to <c>account-inactive</c>.
    /// </summary>
    /// <remarks><see cref="AccountType"/> and <see cref="CurrencyCode"/> are the stored
    /// ones, which the posting validation reads to check a typed amount at its currency's
    /// places (ADR-0114 D15).</remarks>
    public readonly record struct AccountActivity(bool IsActive, string AccountType, string CurrencyCode)
    {
        /// <summary>A real account: anything but a category, which holds no money of its
        /// own (ADR-0114 D13).</summary>
        public bool IsReal => !string.Equals(AccountType, "category", StringComparison.Ordinal);
    }

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
        /// <summary>The feed account's currency, as the connection's directory last
        /// recorded it, differs from the Coffer account's (ADR-0114 D13). Nothing is
        /// bound.</summary>
        CurrencyMismatch,
    }

    /// <summary>
    /// The result of <see cref="BindFeedMappingAsync"/>, with both currencies on a
    /// <see cref="BindFeedMappingResult.CurrencyMismatch"/> so the refusal can name them.
    /// </summary>
    public sealed record BindFeedMappingOutcome(
        BindFeedMappingResult Result, string? FeedCurrency = null, string? AccountCurrency = null);

    /// <summary>
    /// Bind a Coffer account to one SimpleFIN-side account on a
    /// feed connection so future syncs route that
    /// <c>simplefin_account_id</c>'s transactions here
    /// (slice 2b — option 2 in the mapping design discussion).
    /// Idempotent: re-binding the same pair is a no-op.
    /// </summary>
    /// <remarks>
    /// An account's currency must match what is imported into it (ADR-0114 D13), so a
    /// feed account whose currency differs from the Coffer account's is refused, by the
    /// rule the sync applies to a binding made before this check
    /// (<see cref="Coffer.Api.Ingest.SimpleFin.SimpleFinPullProvider.CurrencyDiffers"/>). The feed's
    /// currency is the one the connection's directory recorded at its last sync; a feed
    /// account the directory does not hold yet, or one that reported no currency, binds,
    /// and the sync judges it when it is fetched.
    /// </remarks>
    public async Task<BindFeedMappingOutcome> BindFeedMappingAsync(
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
        if (!connectionInLedger) return new(BindFeedMappingResult.ConnectionMismatch);

        var feedCurrency = await _db.FeedConnectionAccounts.AsNoTracking()
            .Where(d => d.FeedConnectionId == feedConnectionId
                        && d.LedgerId == ledgerId
                        && d.ExternalId == simpleFinAccountId)
            .Select(d => d.Currency)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var accountCurrency = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => a.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (accountCurrency is not null
            && Coffer.Api.Ingest.SimpleFin.SimpleFinPullProvider.CurrencyDiffers(feedCurrency, accountCurrency))
            return new(BindFeedMappingResult.CurrencyMismatch, feedCurrency!.Trim(), accountCurrency);

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.FeedConnectionId, feedConnectionId)
                .SetProperty(a => a.ExternalId, simpleFinAccountId),
                cancellationToken)
            .ConfigureAwait(false);
        return new(BindFeedMappingResult.Ok);
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
    /// <summary>
    /// Remember which file-import provider this account was last imported with (mig 223).
    /// </summary>
    /// <remarks>
    /// <para>A memory written as a side effect of a successful import, not a setting the
    /// user maintains. Saying "this account is at that brokerage" once, by importing one
    /// of its files, is the whole interaction; the picker preselects it from then on.</para>
    ///
    /// <para>Best-effort by design. It returns nothing and is never awaited for its
    /// result, because the import has already succeeded by the time this runs and
    /// failing a completed import over a UI convenience would be the wrong trade.</para>
    /// </remarks>
    public async Task RememberImportProviderAsync(
        Guid ledgerId,
        Guid accountId,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return;

        await _db.Accounts
            .Where(a => a.Id == accountId
                        && a.LedgerId == ledgerId
                        && a.ImportProviderKey != providerKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.ImportProviderKey, providerKey),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SetSyncFromDateResult> SetSyncFromDateAsync(
        Guid ledgerId,
        Guid accountId,
        DateTime? syncFromDate,
        CancellationToken cancellationToken = default)
    {
        if (syncFromDate is { } d && d > _clock.GetUtcNow().UtcDateTime)
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
    /// The flag's effect on existing data is realised by recomputing the
    /// brokerage's holdings (<c>recompute_holdings_for_brokerage</c>) in the same
    /// transaction as the flag's write, so the caller sees converged
    /// <c>holdings.cost_basis</c> and <c>lots.unit_cost</c> by the time the response
    /// returns, and a recompute that fails leaves the flag as it was. The transaction
    /// takes the ledger's write lock first (migration 275).
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

        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

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
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        // The total is in the ledger's reporting currency (ADR-0114 D7): a category's
        // leg carries the money of the real account on its posting's other side, in that
        // account's currency, so each is converted at its transaction's date and summed,
        // rounded once. A leg whose posting has no real account (between two categories)
        // is in the category's own currency. Legs in the reporting currency are summed as
        // they stand, so they are grouped by category alone and need no date.
        var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false);
        var sums = await (
                from l in _db.TxnLegs.AsNoTracking()
                join a in _db.Accounts.AsNoTracking() on l.AccountId equals a.Id
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where l.LedgerId == ledgerId && a.AccountType == "category"
                let paying = _db.TxnLegs
                    .Where(o => o.HeaderId == l.HeaderId && o.PostingIndex == l.PostingIndex && o.Id != l.Id)
                    .Join(_db.Accounts, o => o.AccountId, oa => oa.Id, (o, oa) => oa)
                    .Where(oa => oa.AccountType != "category")
                    .Select(oa => oa.CurrencyCode)
                    .FirstOrDefault()
                let currency = paying ?? a.CurrencyCode
                group l.Amount by new
                {
                    l.AccountId,
                    Currency = currency,
                    Day = currency == reporting ? (DateTime?)null : h.PostedAt.Date,
                } into g
                select new { g.Key.AccountId, g.Key.Currency, g.Key.Day, Total = g.Sum() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        foreach (var s in sums.Where(s => s.Day is not null))
            needs.Add(s.Currency, reporting, DateOnly.FromDateTime(s.Day!.Value));
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        var totals = sums
            .GroupBy(s => s.AccountId)
            .ToDictionary(g => g.Key, g =>
            {
                var total = rates.NewTotal(reporting);
                foreach (var s in g)
                {
                    if (s.Day is { } day) total.Add(s.Total, s.Currency, DateOnly.FromDateTime(day));
                    else total.Add(s.Total, reporting, RateDay.Latest());   // the identity
                }
                return total.Result;
            });

        var childCounts = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.ParentId != null)
            .GroupBy(a => a.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        return cats
            .Select(c =>
            {
                var count = txnAggs.GetValueOrDefault(c.Id);
                var total = totals.GetValueOrDefault(c.Id, Converted.Of(0m));
                return new CategoryNode(
                    c.Id, c.Name, c.CategoryKind!, c.ParentId, c.IsActive, c.IsSystem,
                    count, childCounts.GetValueOrDefault(c.Id), total.Amount,
                    total.Unavailable.Count > 0 ? total.Unavailable : null);
            })
            .ToList();
    }

    /// <summary>Outcome of <see cref="MergeCategoryAsync"/>.</summary>
    public enum MergeCategoryResult
    {
        Ok, SourceNotInLedger, TargetNotInLedger, NotCategory, KindMismatch,
        SameCategory, SourceIsSystem,
        /// <summary>The target sits somewhere beneath the source.</summary>
        TargetIsDescendant,
        /// <summary>A month's two budget targets are in different currencies and the rate
        /// between them is missing (ADR-0114 D9, D12): nothing is written, and
        /// <see cref="MergeCategoryOutcome.MissingRate"/> names the pair and the day.</summary>
        RateUnavailable,
    }

    /// <summary>Counts moved by a (non-dry-run) merge, for the API / MCP echo.</summary>
    /// <param name="BudgetTargetsMoved">The source's targets the destination kept
    /// (ADR-0099). Summed into the destination's where both sides held one for the same month.</param>
    /// <param name="BudgetTargetsDropped">Targets that had to go because a category above them
    /// already held one for that month — D1a allows a target on a node or on its descendants,
    /// never both. Whatever sits beneath the destination (the source's children, who arrive
    /// under it, and its own) loses to the destination's, which now includes what moved onto it;
    /// and what arrived on the destination sits beneath the destination's ancestors, whose
    /// targets win.</param>
    public sealed record MergeCategoryOutcome(
        MergeCategoryResult Result, int TransactionsMoved, int ChildrenReparented,
        int BudgetTargetsMoved = 0, int BudgetTargetsDropped = 0,
        RateUnavailable? MissingRate = null);

    /// <summary>
    /// Merge category <paramref name="sourceId"/> into <paramref name="targetId"/>
    /// (ADR-0068). Repoints every leg that references the source — committed
    /// transactions AND recurring-template legs (reminder category postings live on
    /// their template <c>txn_legs</c>) — to the target, reparents the source's child
    /// categories to the target, then <b>deactivates</b> the now-empty source
    /// (the row is kept, not deleted; reactivating it brings back an empty category,
    /// and nothing moves back). Both must be
    /// categories in the ledger, of any kind (see below); the source must not be
    /// system-managed, and the target must not sit beneath the source, because the
    /// source's children are reparented to the target and the target would end up under
    /// itself. Maintained balances for both categories are rebuilt.
    /// <paramref name="dryRun"/> reports the counts that would move without writing.
    /// Atomic.
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
        // Kinds MAY differ, deliberately (ADR-0017). This is now the only way to
        // reclassify a category, so refusing a cross-kind merge would leave no way
        // at all — and the alternative, flipping the kind in place, is the
        // strictly worse version of the same rewrite: no
        // counts, no confirmation, no trail. Here the caller is told how many
        // transactions and children move, and the source is deactivated rather than
        // destroyed. KindMismatch is retained on the enum so an older client that
        // still switches on it keeps compiling; nothing returns it.

        // A target beneath the source: the children are reparented to the target below, so a
        // child that is (or contains) the target would be put under itself, and the tree would
        // have a loop no read can walk out of. Refused before anything is counted or written.
        if ((await DescendantsOfChildrenAsync(ledgerId, sourceId, cancellationToken).ConfigureAwait(false))
            .Contains(targetId))
            return new MergeCategoryOutcome(MergeCategoryResult.TargetIsDescendant, 0, 0);

        // A month whose two targets are in different currencies needs the rate between
        // them (ADR-0114 D12); a missing one refuses the merge before anything is counted
        // or written, the preview included.
        if ((await PlanTargetMergeAsync(ledgerId, sourceId, targetId, cancellationToken).ConfigureAwait(false))
            .Missing is { } missingRate)
            return new MergeCategoryOutcome(MergeCategoryResult.RateUnavailable, 0, 0, MissingRate: missingRate);

        var txnCount = await _db.TxnLegs
            .Where(l => l.LedgerId == ledgerId && l.AccountId == sourceId)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        var childCount = await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.ParentId == sourceId)
            .CountAsync(cancellationToken).ConfigureAwait(false);

        if (dryRun)
            return new MergeCategoryOutcome(MergeCategoryResult.Ok, txnCount, childCount);

        // The ledger's write lock first (migration 275), before the source's row lock
        // below: the merge recomputes balances, and two writers of one ledger take turns.
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        // The source is locked before its legs are read (fn_lock_accounts): a leg or a
        // transaction another request adds to it after the read would otherwise move
        // below with no recompute of its balances or its stored presentation. One being
        // added now is waited for and read.
        _ = await _db.LockAccounts([sourceId]).Select(r => r.Locked)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

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
                PostedAt = h.PostedAt,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The source's children and everything under them, captured BEFORE the
        // reparent below rewrites their parentage — afterwards the tree no longer
        // says which nodes arrived, and these are exactly the ones that can newly
        // collide with a target on the destination (ADR-0099 D1a).
        var movedSubtree = await DescendantsOfChildrenAsync(ledgerId, sourceId, cancellationToken)
            .ConfigureAwait(false);
        // The same refusal as above, asked again inside the transaction, so a category moved
        // under the source since that first read is caught before the merge writes. The lock
        // above is on the source's row; an edit of the tree takes no lock (a reparent has
        // none), so this narrows the window between two requests and does not close it. The
        // transaction ends unwritten.
        if (movedSubtree.Contains(targetId))
            return new MergeCategoryOutcome(MergeCategoryResult.TargetIsDescendant, 0, 0);

        // The targets' conversion, read again inside the transaction, before the first
        // write: a target retyped since the read above is the one converted. A missing rate
        // ends the transaction unwritten.
        var targetPlan = await PlanTargetMergeAsync(ledgerId, sourceId, targetId, cancellationToken)
            .ConfigureAwait(false);
        if (targetPlan.Missing is { } missingNow)
            return new MergeCategoryOutcome(MergeCategoryResult.RateUnavailable, 0, 0, MissingRate: missingNow);

        // Repoint every referencing leg (committed txns + reminder templates) → target.
        await _db.TxnLegs
            .Where(l => l.LedgerId == ledgerId && l.AccountId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccountId, targetId), cancellationToken)
            .ConfigureAwait(false);
        // Every transaction the source category owned now has no leg on it, so it
        // follows its legs to the target (mig 263) — before the source could go.
        await AnnotationWriter.FollowRepointAsync(
                _db, ledgerId, sourceId, targetId, headerIds: null, cancellationToken)
            .ConfigureAwait(false);

        // Reparent the source's child categories to the target.
        await _db.Accounts
            .Where(a => a.LedgerId == ledgerId && a.ParentId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ParentId, (Guid?)targetId), cancellationToken)
            .ConfigureAwait(false);

        var (targetsMoved, targetsDropped) = await MergeBudgetTargetsAsync(
            ledgerId, sourceId, targetId, movedSubtree, targetPlan.Converted, cancellationToken).ConfigureAwait(false);

        // Deactivate the now-empty source (the row is kept; nothing moves back if it is reactivated).
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
        // What a moved leg's side, and its posting's other side, show (their account,
        // and an owner that followed its legs) changed without the change tracker.
        await LegNotes.RecomputeAsync(
            _db, moved.Select(m => m.HeaderId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MergeCategoryOutcome(
            MergeCategoryResult.Ok, txnCount, childCount, targetsMoved, targetsDropped);
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
    // -----------------------------------------------------------------------
    // Budget targets under a moving tree (ADR-0099 D1a)
    // -----------------------------------------------------------------------
    //
    // D1a: a target may sit on a node OR on its descendants, never both, so every
    // subtree has exactly one authoritative mark and the budget hero's roots-only
    // ceiling needs no special case. SetAsync enforces that on the write path; a
    // merge and a reparent can each manufacture a violation out of two states that
    // were individually legal, which is what these cover.
    //
    // They resolve it DIFFERENTLY, on purpose. A merge is an explicit instruction
    // to treat two categories as one, so it repairs and reports. A reparent is a
    // move, not a combine, and carries no such instruction, so it refuses rather
    // than silently discarding a number somebody typed.

    /// <summary>Every descendant of this category's CHILDREN, inclusive of those
    /// children — the subtree a reparent drags along, minus the node itself.
    /// <see cref="CategoryTree.DescendantsAsync"/> does the walk.</summary>
    private async Task<HashSet<Guid>> DescendantsOfChildrenAsync(
        Guid ledgerId, Guid parentId, CancellationToken cancellationToken) =>
        await CategoryTree
            .DescendantsAsync(_db, ledgerId, parentId, includeRoot: false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Repoint the source's targets onto the destination, then drop any target in
    /// the arriving subtree that the destination now covers.
    /// </summary>
    /// <remarks>
    /// SUMMED on collision, not "destination wins". The merge repoints every leg
    /// with NO date predicate, so the destination's ACTUALS for months long past
    /// grow by the source's spend the instant it commits; leaving the mark
    /// unchanged would make each of those months read as newly over budget for a
    /// reason the user never chose. Summing keeps the comparison proportionate.
    /// It does mutate a number ADR-0099 D2 calls frozen, and that is justified as
    /// a new explicit instruction to treat the two categories as one, not as
    /// history drifting on its own.
    /// </remarks>
    /// <summary>
    /// For each month both categories hold a target in, the source's amount in the
    /// destination's currency (ADR-0114 D12): the same rate the Budget screen shows it at,
    /// the month's last day for a past month, the latest rate for the current month or a
    /// later one. A month in one currency needs no rate and keeps the amount as typed.
    /// <see cref="TargetMergePlan.Missing"/> is the first missing rate, by month.
    /// </summary>
    private async Task<TargetMergePlan> PlanTargetMergeAsync(
        Guid ledgerId, Guid sourceId, Guid targetId, CancellationToken cancellationToken)
    {
        var pairs = await (
                from s in _db.BudgetTargets.AsNoTracking()
                join d in _db.BudgetTargets.AsNoTracking()
                    on new { s.LedgerId, s.TargetMonth } equals new { d.LedgerId, d.TargetMonth }
                where s.LedgerId == ledgerId && s.CategoryId == sourceId && d.CategoryId == targetId
                select new { s.Id, s.TargetMonth, s.Amount, From = s.CurrencyCode, To = d.CurrencyCode })
            .OrderBy(x => x.TargetMonth)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var converted = new Dictionary<Guid, decimal>();
        if (pairs.Count == 0) return new TargetMergePlan(converted, null);

        // The month the server is in: a merge has no caller's date, and the screen's
        // current month is the one the latest rate applies to.
        var today = ScheduledBoundary.Today(null, _clock);
        var lookup = new ExchangeRateLookup(_db, _clock);
        var needs = lookup.NewNeeds();
        RateDay DayOf(DateOnly month) =>
            month.AddMonths(1) <= today
                ? RateDay.Of(month.AddMonths(1).AddDays(-1), needs.ServerToday)
                : RateDay.Latest();

        foreach (var p in pairs) DayOf(p.TargetMonth).Declare(needs, p.From, p.To);
        var rates = await lookup.LoadAsync(ledgerId, needs, cancellationToken).ConfigureAwait(false);
        foreach (var p in pairs)
        {
            var rate = DayOf(p.TargetMonth).Read(rates, p.From, p.To);
            if (rate.Unavailable is { } why) return new TargetMergePlan(converted, why);
            converted[p.Id] = p.Amount * rate.Rate!.Value;
        }
        return new TargetMergePlan(converted, null);
    }

    /// <summary>The source targets' amounts in their destination's currency, by source
    /// target id, at full precision; or the missing rate that refuses the merge.</summary>
    private sealed record TargetMergePlan(IReadOnlyDictionary<Guid, decimal> Converted, RateUnavailable? Missing);

    private async Task<(int Moved, int Dropped)> MergeBudgetTargetsAsync(
        Guid ledgerId, Guid sourceId, Guid targetId,
        IReadOnlySet<Guid> arrivingSubtree, IReadOnlyDictionary<Guid, decimal> convertedSource,
        CancellationToken cancellationToken)
    {
        var sourceTargets = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId && t.CategoryId == sourceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var destTargets = await _db.BudgetTargets
            .Where(t => t.LedgerId == ledgerId && t.CategoryId == targetId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var destByMonth = destTargets.ToDictionary(t => t.TargetMonth);
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var src in sourceTargets)
        {
            if (destByMonth.TryGetValue(src.TargetMonth, out var dest))
            {
                // Summed in the destination's currency, which it keeps (ADR-0114 D12): the
                // source converted at full precision and rounded once, at the destination
                // currency's places, half away from zero (ADR-0114 D15), then added. The
                // stored destination figure is added as it is, never re-rounded, so a
                // stored figure that does not fit its currency is kept as it was. One
                // currency is the plain sum it always was.
                dest.Amount = dest.CurrencyCode == src.CurrencyCode
                    ? dest.Amount + src.Amount
                    : dest.Amount + CurrencyMinorUnits.RoundAwayFromZero(convertedSource[src.Id], dest.CurrencyCode);
                dest.UpdatedAt = now;
                _db.BudgetTargets.Remove(src);
            }
            else
            {
                src.CategoryId = targetId;
                src.UpdatedAt = now;
                destByMonth[src.TargetMonth] = src;
            }
        }

        // What the loop moved is the destination's now, which the reads below must see.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The destination speaks for everything beneath it, so a target held beneath it in a month
        // it now covers is a D1a violation the merge itself created. That is the source's children
        // (reparented under it already), and equally a sub-category the destination had before,
        // now that a target has arrived on the destination for that month. The ancestor wins,
        // matching the bulk-fill rule; the count comes back so the caller can say so rather than
        // lose it silently.
        var doomed = new HashSet<BudgetTargetRow>();
        if (destByMonth.Count > 0)
        {
            var beneath = await CategoryTree
                .DescendantsAsync(_db, ledgerId, targetId, includeRoot: false, cancellationToken)
                .ConfigureAwait(false);
            if (beneath.Count > 0)
            {
                var months = destByMonth.Keys.ToList();
                var ids = beneath.ToList();
                doomed.UnionWith(await _db.BudgetTargets
                    .Where(t => t.LedgerId == ledgerId
                                && ids.Contains(t.CategoryId)
                                && months.Contains(t.TargetMonth))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        // The same rule upward. A category above the destination speaks for it, so a target
        // that arrived on the destination, or on what arrived beneath it, in a month one of
        // those ancestors already holds would sit under another for that month. The
        // destination's own targets from before the merge cannot clash with its ancestors', or
        // they could not have been set, so what is dropped here is what the merge brought.
        var above = await CategoriesAboveAsync(ledgerId, targetId, includeSelf: false, cancellationToken)
            .ConfigureAwait(false);
        if (above.Count > 0)
        {
            var aboveIds = above.ToList();
            var ancestorMonths = await _db.BudgetTargets.AsNoTracking()
                .Where(t => t.LedgerId == ledgerId && aboveIds.Contains(t.CategoryId))
                .Select(t => t.TargetMonth)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ancestorMonths.Count > 0)
            {
                var arrived = arrivingSubtree.Append(targetId).ToList();
                doomed.UnionWith(await _db.BudgetTargets
                    .Where(t => t.LedgerId == ledgerId
                                && arrived.Contains(t.CategoryId)
                                && ancestorMonths.Contains(t.TargetMonth))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        _db.BudgetTargets.RemoveRange(doomed);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // Moved is what the destination kept of the source's targets: one that was summed into
        // the destination's counts, and one that arrived and was then dropped does not.
        return (sourceTargets.Count(s => !doomed.Contains(s)), doomed.Count);
    }

    /// <summary>
    /// The categories above <paramref name="categoryId"/>, nearest first, and the category
    /// itself when <paramref name="includeSelf"/>. Category trees are shallow; the walk is
    /// bounded defensively and stops at a node it has seen.
    /// </summary>
    private async Task<HashSet<Guid>> CategoriesAboveAsync(
        Guid ledgerId, Guid categoryId, bool includeSelf, CancellationToken cancellationToken)
    {
        var tree = await _db.Accounts.AsNoTracking()
            .Where(a => a.LedgerId == ledgerId && a.AccountType == "category")
            .Select(a => new { a.Id, a.ParentId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var parentOf = tree.ToDictionary(t => t.Id, t => t.ParentId);

        var above = new HashSet<Guid>();
        Guid? cursor = includeSelf ? categoryId : parentOf.GetValueOrDefault(categoryId);
        while (cursor is Guid node && above.Count < 64)
        {
            if (!above.Add(node)) break;
            cursor = parentOf.TryGetValue(node, out var p) ? p : null;
        }
        return above;
    }

    /// <summary>
    /// True when moving this category under a new parent would put a target under
    /// another for the same month, in either direction.
    /// </summary>
    private async Task<bool> ReparentWouldClashAsync(
        Guid ledgerId, Guid categoryId, Guid newParentId, CancellationToken cancellationToken)
    {
        // The destination chain: the new parent and everything above it.
        var above = await CategoriesAboveAsync(ledgerId, newParentId, includeSelf: true, cancellationToken)
            .ConfigureAwait(false);

        // The moving tree: the node and everything under it.
        var below = await DescendantsOfChildrenAsync(ledgerId, categoryId, cancellationToken)
            .ConfigureAwait(false);
        below.Add(categoryId);

        var aboveIds = above.ToList();
        var aboveMonths = await _db.BudgetTargets.AsNoTracking()
            .Where(t => t.LedgerId == ledgerId && aboveIds.Contains(t.CategoryId))
            .Select(t => t.TargetMonth)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (aboveMonths.Count == 0) return false;

        var belowIds = below.ToList();
        return await _db.BudgetTargets.AsNoTracking()
            .AnyAsync(t => t.LedgerId == ledgerId
                           && belowIds.Contains(t.CategoryId)
                           && aboveMonths.Contains(t.TargetMonth), cancellationToken)
            .ConfigureAwait(false);
    }

    public enum ReparentCategoryResult
    {
        Ok, NotInLedger, NotCategory, IsSystem, ParentNotCategory, WouldCycle, SameParent,
        /// <summary>The move would put a budget target under another for the same
        /// month, which ADR-0099 D1a forbids.</summary>
        BudgetTargetConflict,
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

        // ADR-0099 D1a, checked on the DRY RUN too: a dry run that answered Ok and
        // then failed for real would be worse than having no dry run at all.
        if (newParentId is Guid dest
            && await ReparentWouldClashAsync(ledgerId, categoryId, dest, cancellationToken)
                .ConfigureAwait(false))
            return ReparentCategoryResult.BudgetTargetConflict;

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
        /// <summary>A category named a currency other than the ledger's reporting one.</summary>
        CategoryCurrencyNotReporting,
        /// <summary>A typed money figure (the opening balance or a loan term) carries more
        /// places than the account's currency allows (ADR-0114 D15); the outcome's
        /// <see cref="CreateAccountOutcome.Unfit"/> names it.</summary>
        AmountPlacesExceedCurrency,
    }

    /// <summary>A typed money figure of an account write that does not fit its currency
    /// (ADR-0114 D15): the request field's wire name, and the currency it is checked at.</summary>
    public readonly record struct UnfitTypedAmount(string Field, string CurrencyCode);

    public sealed record CreateAccountOutcome(
        CreateAccountFailure Failure, AccountSummary? Account, UnfitTypedAmount? Unfit = null);

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
            if (categoryKind is not ("income" or "expense" or "adjustment"))
                return new(CreateAccountFailure.CategoryKindInvalid, null);
        }
        else if (!string.IsNullOrWhiteSpace(request.CategoryKind))
        {
            return new(CreateAccountFailure.CategoryKindInvalid, null);
        }

        // The stored form is CurrencyCodes' (prices share it); blank is USD. A real
        // account's code must also be one an account can take (ADR-0114 D15): an ISO 4217
        // currency with at most 3 places. That second check is made here, at the account
        // writer, and not in CurrencyCodes, which a price or quote currency also passes
        // through and which this rule does not cover. A category keeps its own rule below.
        var currency = "USD";
        if (!string.IsNullOrWhiteSpace(request.CurrencyCode)
            && !CurrencyCodes.TryNormalize(request.CurrencyCode, out currency))
        {
            return new(CreateAccountFailure.CurrencyInvalid, null);
        }
        if (!isCategory && !CurrencyMinorUnits.IsAcceptedAccountCurrency(currency))
            return new(CreateAccountFailure.CurrencyInvalid, null);
        // A category has no currency of its own (ADR-0109 D9): its lines carry the paying
        // account's. Its row takes the ledger's reporting currency, so it never reads as
        // foreign, and a request that names another is refused rather than stored.
        if (isCategory)
        {
            var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.CurrencyCode)
                && !string.Equals(currency, reporting, StringComparison.Ordinal))
                return new(CreateAccountFailure.CategoryCurrencyNotReporting, null);
            currency = reporting;
        }

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

        // Every typed money figure is at the account currency's places (ADR-0114 D15).
        if (FirstUnfitMoney(currency, openingBalance, null, type == "loan" ? request.LoanTerms : null, null)
            is { } unfit)
            return new(CreateAccountFailure.AmountPlacesExceedCurrency, null, unfit);

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
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
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
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
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
            request.IsActive, false, null, 0, holdingsSiblingId, false, institution,
            // Never imported from anything yet, by definition.
            null);
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

    /// <summary>
    /// The first typed money figure of an account write that carries more places than
    /// <paramref name="currencyCode"/> allows, or null when every one fits (ADR-0114 D15):
    /// the opening balance, then the loan's principal, escrow and fixed payment, all in the
    /// account's currency (escrow takes the loan's, whatever the escrow account's). A figure
    /// equal to its stored value passes even when it does not fit, because stored amounts
    /// that do not fit are kept and an edit that leaves one as it was is not refused for it.
    /// A fixed payment is checked only when it is used, since a computed payment ignores it.
    /// </summary>
    /// <param name="currencyCode">The currency the write leaves the account in.</param>
    /// <param name="openingBalance">The opening balance the write sends, or null when it
    /// sends none.</param>
    /// <param name="storedOpening">The stored opening balance, on an edit.</param>
    /// <param name="terms">The loan terms the write sends, or null.</param>
    /// <param name="storedTerms">The stored loan terms, on an edit of a loan that has
    /// them.</param>
    private static UnfitTypedAmount? FirstUnfitMoney(
        string currencyCode, decimal? openingBalance, decimal? storedOpening,
        LoanTermsDto? terms, LoanTermsRow? storedTerms)
    {
        bool Unfit(decimal value, decimal? stored) =>
            !(stored is { } s && value == s) && !CurrencyMinorUnits.Fits(value, currencyCode);

        if (openingBalance is { } ob && Unfit(ob, storedOpening))
            return new UnfitTypedAmount("openingBalance", currencyCode);
        if (terms is null) return null;
        if (Unfit(terms.OriginalPrincipal, storedTerms?.OriginalPrincipal))
            return new UnfitTypedAmount("loanTerms.originalPrincipal", currencyCode);
        if (Unfit(terms.EscrowAmount, storedTerms?.EscrowAmount))
            return new UnfitTypedAmount("loanTerms.escrowAmount", currencyCode);
        if (!terms.PaymentIsComputed && terms.FixedPayment is { } fixedPayment
            && Unfit(fixedPayment, storedTerms?.FixedPayment))
            return new UnfitTypedAmount("loanTerms.fixedPayment", currencyCode);
        return null;
    }

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
        /// <summary>The request tried to CHANGE the kind of an existing category.</summary>
        CategoryKindImmutable,
        /// <summary>A category named a currency other than the ledger's reporting one.</summary>
        CategoryCurrencyNotReporting,
        /// <summary>A typed money figure (the opening balance or a loan term) carries more
        /// places than the currency the edit leaves the account in allows (ADR-0114 D15);
        /// the outcome's <see cref="UpdateAccountOutcome.Unfit"/> names it.</summary>
        AmountPlacesExceedCurrency,
        /// <summary>A currency change cannot relabel the account's stored figures
        /// (ADR-0114 D13, D15); the outcome's <see cref="UpdateAccountOutcome.Relabel"/>
        /// says why.</summary>
        RelabelRefused,
    }

    /// <summary>What an account edit did, or why it was refused: <see cref="Result"/>; for
    /// <see cref="UpdateAccountResult.AmountPlacesExceedCurrency"/> the figure that does
    /// not fit; for <see cref="UpdateAccountResult.RelabelRefused"/> the relabel's verdict;
    /// and on <see cref="UpdateAccountResult.Ok"/> how many mirrored pairs a currency
    /// change left across currencies (ADR-0114 D13).</summary>
    public sealed record UpdateAccountOutcome(
        UpdateAccountResult Result,
        UnfitTypedAmount? Unfit = null,
        RelabelCheck.Verdict? Relabel = null,
        int BecameTwoAmount = 0)
    {
        public static implicit operator UpdateAccountOutcome(UpdateAccountResult result) => new(result);
    }

    /// <summary>
    /// Edit an account's general attributes (ADR-0050). PARTIAL: a null scalar
    /// leaves the field unchanged. <c>account_type</c> is immutable (not in the
    /// request) — changing it would invalidate register rendering / holdings /
    /// existing postings. System accounts are not editable. Applied as one
    /// <c>ExecuteUpdateAsync</c> (no tracked mutation — the row type is
    /// init-only, matching <see cref="SetIsActiveAsync"/>).
    /// </summary>
    public async Task<UpdateAccountOutcome> UpdateAsync(
        Guid ledgerId, Guid accountId, UpdateAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ledger's write lock first (migration 275), before the account is read and
        // whatever the edit names: an opening balance recomputes the account's balances, a
        // currency re-derives trade prices under row locks a trade's save also takes, and
        // every edit writes the account's row, which a trade's save in flight may hold. Two
        // writers of one ledger take turns, so none of them waits on another's rows, and the
        // row is read after any earlier edit of it has committed: every field the edit does
        // not name is written back as that edit left it (the active flag, which has a writer
        // that takes no such lock, is not written back at all; below).
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);

        var cur = await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .Select(a => new
            {
                a.Name, a.AccountType, a.CategoryKind, a.CurrencyCode, a.InstitutionName,
                a.AccountNumber, a.RoutingNumber, a.AccountUrl, a.Notes, a.IsActive, a.IsSystem,
                a.OpeningBalance, a.OpenedOn, a.TaxStatus, a.HoldingsAccountId,
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
            // CurrencyCodes is the stored form; unlike create, a blank here is invalid, not USD.
            if (!CurrencyCodes.TryNormalize(request.CurrencyCode, out var c))
                return UpdateAccountResult.CurrencyInvalid;
            // A real account's new code must be one an account can take (ADR-0114 D15). Only
            // a change is refused: resending the stored code is not one, so a code stored
            // before the rule keeps working, at 2 places. A category keeps its own rule.
            if (cur.AccountType != "category"
                && !string.Equals(c, cur.CurrencyCode, StringComparison.Ordinal)
                && !CurrencyMinorUnits.IsAcceptedAccountCurrency(c))
                return UpdateAccountResult.CurrencyInvalid;
            // A category has no currency of its own (ADR-0109 D9): only the ledger's
            // reporting currency is accepted for one, never another.
            if (cur.AccountType == "category"
                && !string.Equals(
                    c,
                    await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal))
                return UpdateAccountResult.CategoryCurrencyNotReporting;
            currency = c;
        }

        var categoryKind = cur.CategoryKind;
        if (request.CategoryKind is not null)
        {
            if (cur.AccountType != "category") return UpdateAccountResult.CategoryKindInvalid;
            var k = request.CategoryKind.Trim();
            if (k is not ("income" or "expense" or "adjustment"))
                return UpdateAccountResult.CategoryKindInvalid;

            // A category's kind is IMMUTABLE (ADR-0017). Changing it does not edit a
            // label: it retroactively moves every posting in that category between
            // the Spending and Income measures — silently, with no record, and for
            // history going back as far as the ledger does. An editable kind would
            // make that reachable by touching a dropdown.
            //
            // Reclassification has exactly one mechanism: create a category of the
            // wanted kind, merge this one into it, and the merge states what moves.
            //
            // Equal is not a change. The account editor sends this field on every
            // save, so refusing a no-op would make a category impossible to rename.
            if (k != cur.CategoryKind) return UpdateAccountResult.CategoryKindImmutable;
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

        // Every typed money figure is at the places of the currency the edit leaves the
        // account in (ADR-0114 D15), and one resent equal to its stored value passes. A
        // stored figure the edit does not send is the relabel's to judge, not this check's.
        var storedTerms = request.LoanTerms is null
            ? null
            : await _db.LoanTerms.AsNoTracking()
                .FirstOrDefaultAsync(t => t.LedgerId == ledgerId && t.AccountId == accountId, cancellationToken)
                .ConfigureAwait(false);
        if (FirstUnfitMoney(currency, request.OpeningBalance, cur.OpeningBalance, request.LoanTerms, storedTerms)
            is { } unfit)
            return new UpdateAccountOutcome(UpdateAccountResult.AmountPlacesExceedCurrency, unfit);

        // A change of a real account's currency relabels (ADR-0114 D13, D15): every stored
        // figure on it and on its Holdings sibling is kept and reads in the new currency,
        // judged before anything is written. A category's line beside a real line takes the
        // real side's currency, so a category's own code changes only the lines of its
        // postings between categories, which read in that code: those are judged for places,
        // and a category has no opening balance or loan terms to name.
        var becameTwoAmount = 0;
        if (cur.AccountType == "category" && !string.Equals(currency, cur.CurrencyCode, StringComparison.Ordinal))
        {
            var relabel = await RelabelCheck.JudgeAsync(
                    _db, ledgerId, [new RelabelCheck.Relabel(accountId, cur.CurrencyCode, currency)],
                    null, currency, [], cancellationToken)
                .ConfigureAwait(false);
            if (relabel.Refused)
                return new UpdateAccountOutcome(UpdateAccountResult.RelabelRefused, Relabel: relabel);
        }
        else if (!string.Equals(currency, cur.CurrencyCode, StringComparison.Ordinal))
        {
            var relabel = await JudgeCurrencyRelabelAsync(
                    ledgerId, accountId, cur.CurrencyCode, currency, cur.HoldingsAccountId, openingBalance,
                    cur.AccountType == "loan", request.LoanTerms, storedTerms,
                    cancellationToken)
                .ConfigureAwait(false);
            if (relabel.Refused)
                return new UpdateAccountOutcome(UpdateAccountResult.RelabelRefused, Relabel: relabel);
            becameTwoAmount = relabel.BecameTwoAmount;
        }

        // A change of currency changes the currency of every trade price whose trade this
        // account made (ADR-0084 D2, 2026-11-04 amendment, migration 274). Before its row
        // is written it takes the trade-price lock order's first step (TradePriceLocks):
        // FOR NO KEY UPDATE, in id order, on this account, the brokerages naming its
        // holdings account, and every account whose currency the rule reads for the days
        // its trades are on, so a re-derivation in flight on those days is waited for, and
        // one that starts later waits for this edit; a trade-price repair or another
        // brokerage's edit over the same days takes the same accounts in the same order.
        // The days are read again after the lock, so a trade committed while it waited is
        // among them.
        //
        // Only this branch writes the currency: an edit whose code equals the one it read
        // leaves the column as it is, and one whose code differs takes the lock and
        // re-derives below. Every writer of an account's currency takes the ledger write
        // lock, which this edit took before it read `cur`, so the code read is the one
        // committed.
        var currencyChanges = currency != cur.CurrencyCode;
        List<(Guid LedgerId, Guid SecurityId, DateOnly Day)> tradeDays = [];
        if (currencyChanges)
        {
            List<Guid> brokerages = cur.HoldingsAccountId is { } holdings
                ? await _db.Accounts.AsNoTracking()
                    .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId == holdings)
                    .Select(a => a.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false)
                : [];
            brokerages.Add(accountId);
            tradeDays = await TradePriceLocks.LockThenReadAsync(
                    _db, ledgerId,
                    read: ct => TradeDaysAsync(ledgerId, accountId, cur.HoldingsAccountId, ct),
                    days: found => found.Select(d => (d.SecurityId, d.Day)),
                    accounts: _ => brokerages,
                    forWrite: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _db.Accounts
            .Where(a => a.Id == accountId && a.LedgerId == ledgerId)
            .ExecuteUpdateAsync(s =>
                {
                    s.SetProperty(a => a.Name, name)
                        .SetProperty(a => a.InstitutionName, institution)
                        .SetProperty(a => a.AccountNumber, accountNumber)
                        .SetProperty(a => a.RoutingNumber, routingNumber)
                        .SetProperty(a => a.AccountUrl, accountUrl)
                        .SetProperty(a => a.Notes, notes)
                        .SetProperty(a => a.CategoryKind, categoryKind)
                        .SetProperty(a => a.OpeningBalance, openingBalance)
                        .SetProperty(a => a.OpenedOn, openedOn)
                        .SetProperty(a => a.TaxStatus, taxStatus);
                    if (currencyChanges) s.SetProperty(a => a.CurrencyCode, currency);
                    // The active flag has a writer of its own that takes no ledger write
                    // lock (SetIsActiveAsync), so it is written only when the edit names it:
                    // written back from `cur`, it would undo a flip committed since the read.
                    if (request.IsActive is not null) s.SetProperty(a => a.IsActive, isActive);
                },
                cancellationToken)
            .ConfigureAwait(false);

        // An opening balance is the SEED of every stored running balance on this
        // account, so changing it invalidates all of them — and nothing else would
        // notice. ExecuteUpdateAsync bypasses the ChangeTracker, so
        // LegDerivedRecomputeInterceptor never sees this write; and even on a
        // SaveChanges path it would not help, because its switch matches only
        // TxnLegRow / TxnHeaderRow and the two override rows, never AccountRow.
        // Migration 088 dropped the last trigger on `accounts`.
        //
        // Left alone, every balance_after on the account stays seeded from the OLD
        // figure — wrong in the register, on the dashboard, and in net-worth history —
        // and it does not self-heal, because the incremental recompute seeds from the
        // last stored row before its anchor and so inherits the stale value. Only a
        // manual repair cleared it. MergeCategoryAsync in this same class already does
        // exactly this after its own ExecuteUpdate; this path simply never did.
        //
        // Anchored at 0001-01-01, the same floor VerifyAndHealBalancesAsync uses: the
        // seed feeds the FIRST header onward, so a partial window would leave the
        // earlier rows carrying the old opening figure.
        if (openingBalance != cur.OpeningBalance)
        {
            await _recompute.RecomputeAsync(
                [(accountId, new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc))],
                cancellationToken).ConfigureAwait(false);
        }

        // A brokerage's holdings account keeps the brokerage's currency (ADR-0019 Rule 4,
        // 2026-11-04 amendment). It is a system account, which this method refuses to edit,
        // so the brokerage's edit writes it, in this transaction. Without it the
        // brokerage's shares would stay in the currency the person replaced, and the
        // check's holdings_currency projection would report the holdings account, named by
        // its brokerage, after every such edit. No price reads the holdings account's
        // currency, so this writes none. It reads the brokerage's code after this edit's
        // update of the brokerage's row, which no other edit can change until this one
        // commits: the code this edit wrote, or the one committed when it writes none.
        if (cur.HoldingsAccountId is { } holdingsAccountId)
        {
            await HoldingsAccountCurrency.AlignAsync(_db, ledgerId, holdingsAccountId, cancellationToken)
                .ConfigureAwait(false);
        }

        // The days read under the lock above: every trade whose line sits on this account
        // or on its holdings account, re-derived in the new currency, in this transaction.
        // It does so whatever the holdings account held before: a pair that disagreed
        // already had its prices in this account's currency.
        if (tradeDays.Count > 0)
        {
            await new TradePriceRecomputeService(_db).ReseedAsync(tradeDays, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.LoanTerms is { } loanTerms)
            await UpsertLoanTermsAsync(ledgerId, accountId, loanTerms, cancellationToken).ConfigureAwait(false);

        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UpdateAccountOutcome(UpdateAccountResult.Ok, BecameTwoAmount: becameTwoAmount);
    }

    /// <summary>
    /// The relabel a currency change makes (ADR-0114 D13, D15; ADR-0050 D8): the legs on
    /// the account, and on its Holdings sibling when the sibling's currency changes with it
    /// (ADR-0019 Rule 4, <see cref="HoldingsAccountCurrency"/>: the first of its brokerages'
    /// codes in ordinal order, so a sibling another brokerage keeps in the old code does not
    /// move), and the account-level figures as the edit leaves them: the opening balance
    /// and, on a loan, the loan terms (the principal, the escrow and a fixed payment that is
    /// used). Read under the ledger write lock the edit took, before anything is written.
    /// </summary>
    /// <param name="openingBalance">The opening balance as the edit leaves it.</param>
    /// <param name="isLoan">Whether the account is a loan, the one type with terms.</param>
    /// <param name="sentTerms">The loan terms the edit sends, or null.</param>
    /// <param name="storedTerms">The stored loan terms when the edit sends terms (read for
    /// the typed check), or null, in which case they are read here.</param>
    private async Task<RelabelCheck.Verdict> JudgeCurrencyRelabelAsync(
        Guid ledgerId, Guid accountId, string fromCurrency, string toCurrency, Guid? holdingsAccountId,
        decimal openingBalance, bool isLoan, LoanTermsDto? sentTerms, LoanTermsRow? storedTerms,
        CancellationToken cancellationToken)
    {
        List<RelabelCheck.Relabel> relabels = [new(accountId, fromCurrency, toCurrency)];
        if (holdingsAccountId is { } holdings)
        {
            var stored = await _db.Accounts.AsNoTracking()
                .Where(a => a.Id == holdings && a.LedgerId == ledgerId)
                .Select(a => a.CurrencyCode)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var brokerages = await _db.Accounts.AsNoTracking()
                .Where(a => a.LedgerId == ledgerId && a.HoldingsAccountId == holdings)
                .Select(a => new { a.Id, a.CurrencyCode })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var expected = DisplayCurrency.Of(
                brokerages.Select(b => b.Id == accountId ? toCurrency : b.CurrencyCode)).Code;
            if (stored is not null) relabels.Add(new(holdings, stored, expected));
        }

        List<string> figures = [];
        if (!CurrencyMinorUnits.Fits(openingBalance, toCurrency)) figures.Add(UnfitAmountKinds.OpeningBalance);

        // The terms as the edit leaves them: the ones it sends, or the stored ones.
        (decimal Principal, decimal Escrow, decimal? Fixed)? terms = null;
        if (isLoan && sentTerms is not null)
        {
            terms = (sentTerms.OriginalPrincipal, sentTerms.EscrowAmount,
                sentTerms.PaymentIsComputed ? null : sentTerms.FixedPayment);
        }
        else if (isLoan)
        {
            var row = storedTerms ?? await _db.LoanTerms.AsNoTracking()
                .FirstOrDefaultAsync(r => r.LedgerId == ledgerId && r.AccountId == accountId, cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
                terms = (row.OriginalPrincipal, row.EscrowAmount, row.PaymentIsComputed ? null : row.FixedPayment);
        }
        if (terms is { } t
            && (!CurrencyMinorUnits.Fits(t.Principal, toCurrency)
                || !CurrencyMinorUnits.Fits(t.Escrow, toCurrency)
                || (t.Fixed is { } fixedPayment && !CurrencyMinorUnits.Fits(fixedPayment, toCurrency))))
            figures.Add(UnfitAmountKinds.LoanTerms);

        return await RelabelCheck.JudgeAsync(_db, ledgerId, relabels, null, toCurrency, figures, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The (security, UTC day) of every trade line on the account or on its holdings
    /// account: the days whose trade price takes the account's currency. Trade-shaped
    /// lines (a priced security line with shares) on a header that is no template, as the
    /// interceptor reads the lines a change takes off their days; re-deriving a day another
    /// brokerage's execution prices writes the price and currency the rule already names.
    /// </summary>
    private async Task<List<(Guid LedgerId, Guid SecurityId, DateOnly Day)>> TradeDaysAsync(
        Guid ledgerId, Guid accountId, Guid? holdingsAccountId, CancellationToken cancellationToken)
    {
        var lines = await (
                from l in _db.TxnLegs.AsNoTracking()
                join h in _db.TxnHeaders.AsNoTracking() on l.HeaderId equals h.Id
                where l.LedgerId == ledgerId
                      && (l.AccountId == accountId || l.AccountId == holdingsAccountId)
                      && !h.IsRecurringTemplate
                      && l.SecurityId != null
                      && l.Quantity != null && l.Quantity != 0m
                      && l.UnitPrice != null && l.UnitPrice > 0m
                select new { SecurityId = l.SecurityId!.Value, h.PostedAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return lines.Select(d => (ledgerId, d.SecurityId, HeaderDates.UtcDay(d.PostedAt))).Distinct().ToList();
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
