using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Ingest.Ofx;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Ingest;

/// <summary>
/// Single orchestration point for all ingest sources (ADR-0031 §2).
/// Providers translate raw foreign data into typed records; the
/// orchestrator owns everything downstream: DB writes via the
/// existing repositories, dedup by <c>external_id</c> scoped to
/// (ledger, origin) (mig 105), needs-review flag application,
/// promote-on-clear amount updates, the bank-side account
/// directory upsert, per-account watermark advance, and the
/// <c>sync_runs</c> lifecycle (insert / close / error+promotion
/// child rows).
/// </summary>
/// <remarks>
/// <para>Provider dispatch is by <c>ProviderKey</c>:
/// <see cref="RunPullAsync"/> reads <c>conn.Provider</c> and looks
/// up the matching <see cref="IPullProvider"/> in the DI-resolved
/// set; <see cref="RunFileAsync"/> takes the key as an argument
/// since file uploads have no persisted connection.</para>
///
/// <para><b>Balance recompute</b> on
/// <c>txn_header_account_balances</c> is automatic via
/// <see cref="LegDerivedRecomputeInterceptor"/> after this class's
/// <c>SaveChangesAsync</c> (mig 102 / ADR-0032 / ADR-0034). The
/// header + leg inserts during sync are tracked by EF, so the
/// interceptor sees the full batch in <c>ChangeTracker</c> and
/// recomputes every affected account atomically with the save. The
/// <c>ExecuteUpdateAsync</c> calls on <c>feed_connections</c> and
/// <c>accounts</c> (sync watermark / last_synced_at) are not on
/// balance-affecting tables, so the bulk-bypass-the-interceptor
/// caveat doesn't apply.</para>
/// </remarks>
public sealed class IngestOrchestrator
{
    private readonly AppDbContext _db;
    private readonly SyncConnectionLock _connectionLock;
    private readonly IReadOnlyDictionary<string, IPullProvider> _pullProviders;
    private readonly IReadOnlyDictionary<string, IFileProvider> _fileProviders;
    private readonly Coffer.Api.Quotes.QuoteOrchestrator _quotes;
    private readonly ILogger<IngestOrchestrator> _logger;

    public IngestOrchestrator(
        AppDbContext db,
        SyncConnectionLock connectionLock,
        IEnumerable<IPullProvider> pullProviders,
        IEnumerable<IFileProvider> fileProviders,
        Coffer.Api.Quotes.QuoteOrchestrator quotes,
        ILogger<IngestOrchestrator> logger)
    {
        _db = db;
        _connectionLock = connectionLock;
        _pullProviders = pullProviders.ToDictionary(p => p.ProviderKey);
        _fileProviders = fileProviders.ToDictionary(p => p.ProviderKey);
        _quotes = quotes;
        _logger = logger;
    }

    /// <summary>
    /// Run a pull sync for the given connection. Caller has already
    /// verified the user can see the ledger and the connection
    /// belongs to it.
    /// </summary>
    /// <remarks>Balance recompute is automatic via
    /// <see cref="LegDerivedRecomputeInterceptor"/> after this method's
    /// <c>SaveChangesAsync</c>. Header + leg inserts are tracked by
    /// EF, so every account that receives legs in this batch is
    /// picked up by the interceptor's ChangeTracker scan.</remarks>
    /// <param name="ledgerId">Connection's owning ledger.</param>
    /// <param name="connectionId">The connection to sync.</param>
    /// <param name="triggeredByUserId">Audit attribution for the
    /// <c>sync_runs</c> row (slice 2c.1).</param>
    /// <param name="accountIdFilter">When non-null, narrow the
    /// sync to one bank-side account on this connection (slice
    /// 2c.3 per-account endpoint). Pull provider scopes its fetch;
    /// orchestrator also defensively narrows its dispatch loop.</param>
    public async Task<IngestPullOutcome> RunPullAsync(
        Guid ledgerId,
        Guid connectionId,
        Guid triggeredByUserId,
        string? accountIdFilter = null,
        CancellationToken cancellationToken = default)
    {
        // API-layer concurrency fast-path (slice 2c.2). Acquired
        // before any DB work so two requests against the same
        // connection get a clean SyncInProgress without burning a
        // DB round-trip. Independent of the DB-level UNIQUE index
        // from migration 040.
        using var slot = _connectionLock.TryAcquire(connectionId);
        if (slot is null)
            return IngestPullOutcome.Fail(IngestFailureReason.SyncInProgress);

        // Load the connection + the ledger's wrapped LEK. The
        // provider needs both: the connection's encrypted
        // access URL + the wrapped LEK so it can unwrap.
        var keyMaterial = await _db.FeedConnections
            .AsNoTracking()
            .Where(c => c.Id == connectionId && c.LedgerId == ledgerId)
            .Join(_db.Ledgers.AsNoTracking(),
                  c => c.LedgerId,
                  l => l.Id,
                  (c, l) => new
                  {
                      Connection = c,
                      LedgerWrappedLek = l.WrappedLek,
                  })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (keyMaterial is null)
            return IngestPullOutcome.Fail(IngestFailureReason.ConnectionNotFound);
        if (keyMaterial.Connection.AccessUrlCiphertext is null
            || keyMaterial.LedgerWrappedLek is null)
            return IngestPullOutcome.Fail(IngestFailureReason.AccessUrlMissing);

        if (!_pullProviders.TryGetValue(keyMaterial.Connection.Provider, out var provider))
        {
            throw new InvalidOperationException(
                $"No IPullProvider registered for provider key '{keyMaterial.Connection.Provider}'.");
        }

        // Stale-run reaper (slice 2c.2). A process killed mid-sync
        // leaves a `running` row stranded; the UNIQUE partial index
        // from migration 040 would then permanently block syncs for
        // this connection. Lazy sweep before our own INSERT.
        var staleCutoff = DateTime.UtcNow - StaleRunTimeout;
        await _db.LedgerOperations
            .Where(r => r.FeedConnectionId == connectionId
                        && r.Status == "running"
                        && r.StartedAt < staleCutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.ErrorMessage, "Interrupted — process exited before sync completed.")
                .SetProperty(r => r.CompletedAt, DateTime.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        // Open the sync_run row. The UNIQUE partial index
        // `uq_sync_runs_one_running_per_connection` (migration 040)
        // enforces "at most one running sync per connection" at the
        // DB level — concurrent INSERTs race here and the loser
        // hits a unique-violation we map to SyncInProgress.
        var run = new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Family = "ingest",
            ProviderKey = keyMaterial.Connection.Provider,
            TriggeredVia = "manual",
            FeedConnectionId = connectionId,
            TriggeredByUserId = triggeredByUserId,
            Status = "running",
            StartedAt = DateTime.UtcNow,
        };
        _db.LedgerOperations.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, "uq_ledger_operations_one_running_per_connection"))
        {
            _db.Entry(run).State = EntityState.Detached;
            _logger.LogInformation(
                "Ingest pull for connection {ConnectionId} rejected: a sync is already running", connectionId);
            return IngestPullOutcome.Fail(IngestFailureReason.SyncInProgress);
        }

        // Load mapped accounts on this connection. Provider uses
        // the watermark snapshot for window math; orchestrator uses
        // it to dispatch transactions to ledger account ids after
        // the pull returns.
        var mappedAccountsForWindow = await _db.Accounts.AsNoTracking()
            .Where(a => a.FeedConnectionId == connectionId
                        && a.ExternalId != null
                        && (accountIdFilter == null
                            || a.ExternalId == accountIdFilter))
            .Select(a => new { a.Id, a.ExternalId, a.LastSimpleFinSyncAt })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pullContext = new PullContext(
            Connection: keyMaterial.Connection,
            LedgerWrappedLek: keyMaterial.LedgerWrappedLek,
            MappedAccounts: mappedAccountsForWindow
                .Select(a => new MappedAccountWatermark(
                    LedgerAccountId: a.Id,
                    ExternalId: a.ExternalId!,
                    LastSyncedAt: a.LastSimpleFinSyncAt))
                .ToList(),
            AccountIdFilter: accountIdFilter);

        PullResult pullResult;
        try
        {
            pullResult = await provider.PullAsync(pullContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Access URL ciphertext couldn't be unwrapped under the
            // current master KEK. Mark the run failed before
            // returning the typed failure code so the endpoint
            // maps to a clear 422.
            run.Status = "failed";
            run.ErrorMessage = $"Access URL decrypt failed: {ex.Message}";
            run.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(ex,
                "Ingest pull for connection {ConnectionId} failed: access URL ciphertext could not be unwrapped under the current master KEK",
                connectionId);
            return IngestPullOutcome.Fail(IngestFailureReason.AccessUrlCorrupted);
        }
        catch (SimpleFinException ex)
        {
            // Provider-side HTTP / parse fault. Stamp the run, then
            // rethrow so the endpoint's existing error mapping
            // (→ 422) still fires.
            run.Status = "failed";
            run.ErrorMessage = ex.Message;
            run.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // Defensive 403 path: provider detected revoked / expired
        // auth. Flip the connection to needs_reauth, stamp
        // last_synced_at so the user sees the attempt happened,
        // close the run, and return an empty outcome so the SPA can
        // render the re-connect call-to-action.
        if (pullResult.RequiresReauth)
        {
            await _db.FeedConnections
                .Where(c => c.Id == connectionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, "needs_reauth")
                    .SetProperty(c => c.LastSyncedAt, DateTime.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
            run.Status = "needs_reauth";
            run.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IngestPullOutcome.Ok(new IngestRunOutcome(
                SyncRunId: run.Id,
                AccountsDiscovered: 0,
                TransactionsForReview: 0,
                TransactionsStillPending: 0,
                AlreadyKnown: 0,
                ConnectionStatus: "needs_reauth",
                Errors: Array.Empty<IngestError>()));
        }

        // Slice 2c.4: upsert the bank-side account directory so the
        // SPA's unified accounts panel can render mapped + unmapped
        // rows at any time.
        await UpsertConnectionAccountsAsync(
            ledgerId, connectionId, pullResult.Accounts, cancellationToken)
            .ConfigureAwait(false);

        var transactionsForReview = 0;
        var transactionsStillPending = 0;
        var alreadyKnown = 0;
        // Promote-on-clear events captured for the sync_run audit
        // log; flushed in the closing SaveChanges.
        var promotions = new List<(Guid HeaderId, decimal Was, decimal Became)>();
        var totalFetched = pullResult.Accounts.Sum(a => a.Transactions.Count);

        var mappedByExternalId = mappedAccountsForWindow
            .Where(a => a.ExternalId is not null)
            .ToDictionary(a => a.ExternalId!, a => a.Id);

        // Uncategorized counterparty: lazy-resolved on first row
        // this sync writes. Caches for the rest of the loop.
        Guid? uncategorizedAccountId = null;

        foreach (var pullAccount in pullResult.Accounts)
        {
            if (!mappedByExternalId.TryGetValue(pullAccount.ExternalId, out var ledgerAccountId))
            {
                // Unmapped — directory upsert already recorded it.
                continue;
            }

            // Bulk-fetch existing txn_headers for this ledger that
            // match any incoming external_id, scoped to this provider's
            // origin. external_id is the universal per-provider
            // identifier (mig 105); SimpleFIN ids and any future
            // provider's ids both land in this column. Origin scope
            // prevents an unlikely id collision across providers
            // (e.g., an MD txnid that happens to equal a SimpleFIN
            // id). Soft-hidden rows are included so DELETE→re-sync
            // recognises them and doesn't re-insert (which IS the
            // bug mig 105 fixes).
            var incomingExternalIds = pullAccount.Transactions
                .Select(t => t.ExternalId).ToArray();
            // Mig 107: dedup scope shifted from origin to provider_key.
            // Origin is now the icon-level mechanism (online_import /
            // file_import / manual) and is shared across providers;
            // provider_key is the per-provider tag that uniquely
            // identifies the source.
            var providerKey = provider.ProviderKey;
            var existingHeaders = await _db.TxnHeaders
                .Where(h => h.LedgerId == ledgerId
                            && h.ProviderKey == providerKey
                            && h.ExternalId != null
                            && incomingExternalIds.Contains(h.ExternalId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var existingByExternalId = existingHeaders
                .Where(h => h.ExternalId is not null)
                .ToDictionary(h => h.ExternalId!, h => h, StringComparer.Ordinal);

            foreach (var t in pullAccount.Transactions)
            {
                if (existingByExternalId.TryGetValue(t.ExternalId, out var existing))
                {
                    // ADR-0031 follow-up: backfill provider_raw_payload
                    // on the alreadyKnown / promote-on-clear branches
                    // when the existing row was synced before the
                    // storage column existed. Idempotent (only writes
                    // when current value is null); never overwrites a
                    // captured payload — once stored it's an archive.
                    if (existing.ProviderRawPayload is null
                        && t.RawProviderPayload is not null)
                    {
                        existing.ProviderRawPayload = t.RawProviderPayload;
                    }

                    // Backfill the SimpleFIN v2 payee/memo split on
                    // rows that were imported before this code knew
                    // to put `payee` in payee + `description` in memo.
                    // Heuristic: only adjust when the existing row
                    // looks pre-split (memo empty + payee equals the
                    // raw description); user-edited rows carry a
                    // distinct payee that we don't want to clobber.
                    // User overrides live in txn_header_overrides; the
                    // base row is the bank's view, which is what we
                    // refresh here.
                    if (t.Payee is not null
                        && string.IsNullOrEmpty(existing.Memo)
                        && string.Equals(existing.Payee, t.Description, StringComparison.Ordinal))
                    {
                        existing.Payee = t.Payee;
                        existing.Memo = t.Description;
                    }

                    // Three sub-cases by (existing.IsPending, t.Pending):
                    //   (F, *)  already posted — already known.
                    //   (T, F)  promote-on-clear: pending → posted.
                    //           Update is_pending + leg amounts (bank
                    //           may have changed cleared amount).
                    //   (T, T)  re-sync of a still-pending row. No-op.
                    if (existing.IsPending && !t.Pending)
                    {
                        existing.IsPending = false;
                        var wasAmount = await UpdateLegAmountsAsync(
                            existing.Id, ledgerAccountId, t.Amount, cancellationToken)
                            .ConfigureAwait(false);
                        promotions.Add((existing.Id, wasAmount, t.Amount));
                        transactionsForReview++;
                        continue;
                    }
                    alreadyKnown++;
                    continue;
                }

                // New external_id — insert one txn_header + a
                // symmetric posting (ADR-0019) on the mapped account
                // ↔ Uncategorized.
                uncategorizedAccountId ??= await EnsureUncategorizedAsync(
                    ledgerId, cancellationToken).ConfigureAwait(false);

                // ADR-0031 Phase 3c: classifier hints. The orchestrator
                // stays on the bank-shape insert (cash-flow → Uncategorized)
                // because the action × posting_role × posting-cardinality
                // matrix per ADR-0029 requires shares/price the wire
                // doesn't carry. Instead persist the classifier outputs
                // on txn_headers — the editor's pre-fill flow (Phase 3d)
                // reads them on open + upgrades the row through the
                // existing /investment-transactions create path.
                //
                // SecurityTickerHint → security_id resolution is
                // dynamic per ADR-0038: persist only the ticker hint
                // here; `resolved_transactions` LEFT JOINs
                // `provider_security_mappings` to resolve the id on
                // every read. Re-link propagates instantly with no
                // backfill.
                var headerId = Guid.NewGuid();
                _db.TxnHeaders.Add(new TxnHeaderRow
                {
                    Id = headerId,
                    LedgerId = ledgerId,
                    // Mig 107: origin is icon-level mechanism;
                    // provider_key is the specific provider.
                    Origin = "online_import",
                    ProviderKey = "simplefin",
                    // SimpleFIN v2 split: cleaned merchant name goes
                    // to Payee, raw bank/broker text goes to Memo.
                    // Fall back to Description for the Payee when the
                    // provider didn't send a payee field (older feeds
                    // / non-SimpleFIN providers) so the user still
                    // sees something in the register's Payee column.
                    Payee = t.Payee ?? t.Description,
                    Memo = t.Payee is not null ? t.Description : null,
                    PostedAt = t.PostedAt,
                    // NOT NULL since mig 189: feeds often omit a transacted date, and
                    // "no distinct tax date" is stored as the posted date.
                    TransactedAt = t.TransactedAt ?? t.PostedAt,
                    IsPending = t.Pending,
                    NeedsReview = true,
                    // ExternalId is the universal per-provider dedup
                    // key (mig 105). SimpleFIN's transaction id (e.g.
                    // `TRN-<uuid>`) lands here; soft-hide on DELETE
                    // preserves it so the next sync sees the row and
                    // doesn't re-insert. online_match_fitid /
                    // online_match_fi_id are NOT written by SimpleFIN
                    // — they're OFX-protocol fields used by the MD
                    // importer (preserving OFX state) and by future
                    // OFX/QFX direct importers.
                    ExternalId = t.ExternalId,
                    IngestActionHint = t.Action,
                    // Mig 114: persist the classifier-extracted
                    // ticker string on every provider's path (was
                    // OFX-only initially, but the SPA Accept flow
                    // needs ONE rail — see InvestmentRegisterPage
                    // edit-on-row branch). Backfilling pre-mig-114
                    // rows isn't necessary; the SPA falls back to
                    // re-running the classifier on the payee when
                    // this column is null.
                    IngestSecurityTickerHint = t.SecurityTickerHint,
                    // ADR-0031 follow-up: persist provider's verbatim
                    // JSON for the inserted row. Pure pass-through —
                    // SimpleFinPullProvider captured it via
                    // JsonElement.GetRawText() before any field
                    // projection, so the stored payload preserves
                    // anything we don't currently model.
                    ProviderRawPayload = t.RawProviderPayload,
                });
                _db.TxnLegs.Add(new TxnLegRow
                {
                    Id = Guid.NewGuid(),
                    HeaderId = headerId,
                    LedgerId = ledgerId,
                    AccountId = ledgerAccountId,
                    PostingIndex = 0,
                    Amount = t.Amount,
                });
                _db.TxnLegs.Add(new TxnLegRow
                {
                    Id = Guid.NewGuid(),
                    HeaderId = headerId,
                    LedgerId = ledgerId,
                    AccountId = uncategorizedAccountId.Value,
                    PostingIndex = 0,
                    Amount = -t.Amount,
                });
                if (t.Pending) transactionsStillPending++;
                else transactionsForReview++;
            }
        }

        // Stamp last_synced_at + clear any prior needs_reauth /
        // error status now that we have a clean round trip.
        // connection.last_synced_at is the LAST ATTEMPT timestamp
        // (display only); the per-account watermark below drives
        // the next sync's start-date math.
        var nowUtc = DateTime.UtcNow;
        await _db.FeedConnections
            .Where(c => c.Id == connectionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LastSyncedAt, nowUtc)
                .SetProperty(c => c.Status, "active"),
                cancellationToken)
            .ConfigureAwait(false);

        // Slice 2c.5: advance the per-account watermark only for
        // accounts that were mapped AT this sync's read time AND
        // weren't tagged by the provider's error list. Errored
        // accounts keep their old watermark so the next sync
        // retries the same window for them.
        var erroredExternalIds = pullResult.Errors
            .Where(e => e.AccountId is not null)
            .Select(e => e.AccountId!)
            .ToHashSet(StringComparer.Ordinal);
        var advanceForAccountIds = mappedAccountsForWindow
            .Where(a => a.ExternalId is not null
                        && !erroredExternalIds.Contains(a.ExternalId))
            .Select(a => a.Id)
            .ToArray();
        if (advanceForAccountIds.Length > 0)
        {
            await _db.Accounts
                .Where(a => advanceForAccountIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.LastSimpleFinSyncAt, nowUtc),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Close the sync_run row with terminal counters + status.
        // `partial` when provider returned errors; `completed`
        // otherwise. Flush captured errors + promotions to the
        // child tables.
        run.Status = pullResult.Errors.Count > 0 ? "partial" : "completed";
        run.DetailsJson = LedgerOperationDetails.Serialize(new IngestRunDetails
        {
            TxnsFetched = totalFetched,
            TxnsInserted = transactionsForReview + transactionsStillPending - promotions.Count,
            TxnsPromoted = promotions.Count,
            TxnsAlreadyKnown = alreadyKnown,
            TxnsStillPending = transactionsStillPending,
        });
        run.CompletedAt = DateTime.UtcNow;
        foreach (var e in pullResult.Errors)
        {
            _db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = run.Id,
                LedgerId = ledgerId,
                Code = e.Code,
                Message = e.Message,
                SimpleFinConnectionId = e.ConnectionId,
                SimpleFinAccountId = e.AccountId,
            });
        }
        foreach (var (hid, was, became) in promotions)
        {
            _db.LedgerOperationPromotions.Add(new LedgerOperationPromotionRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = run.Id,
                LedgerId = ledgerId,
                HeaderId = hid,
                WasAmount = was,
                BecameAmount = became,
            });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // ADR-0033 §5: per-family orchestrator coupling. A successful SimpleFIN
        // sync just refreshed every brokerage's last_provider_raw_payload, so
        // lift prices out of THAT payload in the same user action — a single
        // RunPullAsync against the simplefin-holdings provider. Deliberately NOT
        // RunAllPullsAsync: external market-data providers (Yahoo et al.) are
        // unrelated to this bank sync, and fanning to them would turn every sync
        // into outbound egress (ADR-0054). They run on an explicit refresh or
        // the scheduled worker instead. Errors here don't fail the sync (the
        // run is recorded "partial"; the sync_run stays "completed"). Explicit
        // call, not an event-bus hook (ADR-0033 §5).
        try
        {
            await _quotes.RunPullAsync(
                ledgerId,
                Coffer.Api.Quotes.SimpleFin.SimpleFinHoldingsQuoteProvider.Key,
                "post-sync",
                triggeredByUserId,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Quote refresh failures don't reverse a successful sync. Log +
            // continue; the ingest outcome is unaffected. RunPullAsync already
            // records the post-sync quote run (ADR-0055) and folds provider
            // errors into it, so this catch only fires on an unexpected throw.
            _logger.LogWarning(ex,
                "Quote refresh failed after sync: ledgerId={LedgerId}",
                ledgerId);
        }

        return IngestPullOutcome.Ok(new IngestRunOutcome(
            SyncRunId: run.Id,
            AccountsDiscovered: pullResult.Accounts.Count,
            TransactionsForReview: transactionsForReview,
            TransactionsStillPending: transactionsStillPending,
            AlreadyKnown: alreadyKnown,
            ConnectionStatus: "active",
            Errors: pullResult.Errors));
    }

    /// <summary>
    /// Run a file-upload ingest (ADR-0031 Phase 4). The provider's
    /// <see cref="IFileProvider.ParseAsync"/> turns the payload into
    /// typed <see cref="IngestedTransaction"/> records; the
    /// orchestrator filters them to <see cref="FileIngestContext.ProviderAccountId"/>
    /// (when set), dedups against the existing
    /// <c>(ledger_id, origin, external_id)</c> index, and writes
    /// new rows through the same EF write path as the pull
    /// providers — so the <c>LegDerivedRecomputeInterceptor</c>
    /// fires automatically on save.
    /// </summary>
    /// <remarks>
    /// <para>Origin per provider: OFX/QFX → <c>"ofx_import"</c>;
    /// generic CSV / per-institution CSV will pick their own origins
    /// when they land. The orchestrator reads it from the provider's
    /// <see cref="IFileProvider.ProviderKey"/> via the
    /// <see cref="ProviderOriginFor"/> map — explicit, not derived,
    /// so the schema's <c>txn_headers_origin_check</c> enumeration
    /// stays the source of truth.</para>
    ///
    /// <para><c>sync_runs.feed_connection_id</c> is NULL for file
    /// uploads — no long-lived connection exists. The unique
    /// constraint <c>uq_sync_runs_one_running_per_connection</c> is
    /// a partial index keyed by connection_id, so concurrent file
    /// imports don't conflict with each other (or with live syncs).</para>
    /// </remarks>
    public async Task<IngestRunOutcome> RunFileAsync(
        string providerKey,
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        if (!_fileProviders.TryGetValue(providerKey, out var provider))
        {
            throw new InvalidOperationException(
                $"No IFileProvider registered for provider key '{providerKey}'.");
        }
        if (!ProviderOriginFor.TryGetValue(providerKey, out var origin))
        {
            throw new InvalidOperationException(
                $"No txn_headers.origin mapping registered for provider key '{providerKey}'.");
        }

        // Parse first — if the file is malformed the provider throws
        // and the caller surfaces a 422; we never open a sync_runs
        // row for an unparseable upload.
        var parsed = await provider.ParseAsync(payload, context, cancellationToken)
            .ConfigureAwait(false);

        // Filter to the requested provider-account when set. For
        // single-account file formats (CSV) ProviderAccountId is
        // null and every parsed row matches.
        var filtered = context.ProviderAccountId is null
            ? parsed.Transactions
            : parsed.Transactions
                .Where(t => t.ProviderAccountId == context.ProviderAccountId)
                .ToList();

        var run = new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = context.LedgerId,
            Family = "ingest",
            ProviderKey = providerKey,
            TriggeredVia = "file-upload",
            FeedConnectionId = null,                 // file uploads have no connection
            TriggeredByUserId = context.TriggeredByUserId,
            Status = "running",
            StartedAt = DateTime.UtcNow,
        };
        _db.LedgerOperations.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Dedup query: (ledger, provider_key, external_id). Same
        // shape as RunPullAsync after mig 107 — provider_key is the
        // per-provider dedup scope; origin is icon-level. Soft-hidden
        // rows are included so a delete-then-re-import recognises
        // the row instead of re-inserting (mig 105 contract).
        // The provider_key persisted on inserted rows is taken from
        // the ProviderOriginFor entry above — distinct from the
        // file-provider's `providerKey` parameter (which the file
        // provider dispatch uses).
        var incomingExternalIds = filtered.Select(t => t.ExternalId).ToArray();
        var rowProviderKey = origin.ProviderKey;

        // Cross-source FITID dedup (OFX provider only). An
        // MD-imported ledger preserves OFX state on
        // online_match_fi_id (BANKID) + online_match_fitid (FITID)
        // under a DIFFERENT provider_key, so the external_id branch
        // alone would double-enter every transaction when the same
        // bank's OFX is re-imported (or MD + OFX run side by side).
        // For OFX we ALSO treat an incoming row as known if a
        // non-merged header in the ledger carries the same
        // (online_match_fi_id, online_match_fitid) pair. SimpleFIN /
        // QIF behaviour is unchanged: this branch is gated to OFX,
        // and QIF's online_match_fitid is null anyway.
        var isOfx = string.Equals(rowProviderKey, OfxFileProvider.Key, StringComparison.Ordinal);

        // Incoming online-match pairs where BOTH halves are present
        // (the only rows the second dedup branch can match on).
        var incomingFiIds = isOfx
            ? filtered
                .Where(t => t.OnlineMatchFiId is not null && t.OnlineMatchFitid is not null)
                .Select(t => t.OnlineMatchFiId!)
                .Distinct()
                .ToArray()
            : Array.Empty<string>();
        var incomingFitids = isOfx
            ? filtered
                .Where(t => t.OnlineMatchFiId is not null && t.OnlineMatchFitid is not null)
                .Select(t => t.OnlineMatchFitid!)
                .Distinct()
                .ToArray()
            : Array.Empty<string>();

        // Single round-trip: existing headers that match EITHER the
        // external_id/provider_key branch OR the OFX online-match
        // pair branch. The pair candidates are pre-filtered to the
        // incoming FI ids + FITIDs server-side; the exact (FiId,
        // Fitid) pairing is verified in memory below (set membership
        // can't express composite-pair equality in a translatable
        // LINQ predicate without a join over an in-memory list).
        var existingHeaders = await _db.TxnHeaders
            .Where(h => h.LedgerId == context.LedgerId
                        && (
                            // Same-provider external_id dedup
                            // (SimpleFIN / QIF / OFX re-import).
                            (h.ProviderKey == rowProviderKey
                             && h.ExternalId != null
                             && incomingExternalIds.Contains(h.ExternalId))
                            ||
                            // OFX cross-source online-match dedup
                            // against MD-preserved (or other-provider)
                            // OFX state. is_merged_into null = the row
                            // is still a live header (not a merge loser).
                            (isOfx
                             && h.IsMergedInto == null
                             && h.OnlineMatchFiId != null
                             && h.OnlineMatchFitid != null
                             && incomingFiIds.Contains(h.OnlineMatchFiId)
                             && incomingFitids.Contains(h.OnlineMatchFitid))
                        ))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var existingByExternalId = existingHeaders
            .Where(h => h.ProviderKey == rowProviderKey && h.ExternalId is not null)
            .ToDictionary(h => h.ExternalId!, h => h, StringComparer.Ordinal);
        // Composite (FiId, Fitid) → header for the OFX online-match
        // branch. Built only for OFX; verifies the exact pair (the
        // server-side filter only narrowed by each half independently).
        var existingByOnlineMatch = isOfx
            ? existingHeaders
                .Where(h => h.OnlineMatchFiId is not null && h.OnlineMatchFitid is not null)
                .GroupBy(h => (h.OnlineMatchFiId!, h.OnlineMatchFitid!))
                .ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<(string, string), TxnHeaderRow>();

        Guid? uncategorizedAccountId = null;
        var transactionsForReview = 0;
        var alreadyKnown = 0;

        foreach (var t in filtered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existingByExternalId.ContainsKey(t.ExternalId))
            {
                alreadyKnown++;
                continue;
            }
            // OFX cross-source: skip if a non-merged header already
            // carries this exact (FiId, Fitid) OFX-protocol pair.
            if (isOfx
                && t.OnlineMatchFiId is not null
                && t.OnlineMatchFitid is not null
                && existingByOnlineMatch.ContainsKey((t.OnlineMatchFiId, t.OnlineMatchFitid)))
            {
                alreadyKnown++;
                continue;
            }
            uncategorizedAccountId ??= await EnsureUncategorizedAsync(
                context.LedgerId, cancellationToken).ConfigureAwait(false);

            // ADR-0031 Phase 3c parity for file imports. The ticker
            // hint is persisted as-is; ADR-0038 retired the stored
            // resolved id in favour of view-level resolution against
            // provider_security_mappings (see mig 115).
            var headerId = Guid.NewGuid();
            _db.TxnHeaders.Add(new TxnHeaderRow
            {
                Id = headerId,
                LedgerId = context.LedgerId,
                Origin = origin.Origin,
                ProviderKey = origin.ProviderKey,
                Payee = t.Payee ?? t.Description,
                Memo = t.Payee is not null ? t.Description : null,
                PostedAt = t.PostedAt,
                // NOT NULL since mig 189 — see the pull path above.
                TransactedAt = t.TransactedAt ?? t.PostedAt,
                IsPending = false,                       // OFX statements are post-clear
                NeedsReview = true,
                // External_id is the universal dedup key (mig 105);
                // online_match_fitid + online_match_fi_id are the
                // OFX-protocol fields — populated natively here so
                // future cross-source dedup against MD-imported
                // rows that preserved the same OFX FITID works.
                ExternalId = t.ExternalId,
                // OFX-protocol-only: populated by the OFX provider
                // (== ExternalId == FITID); null for QIF (whose
                // ExternalId is a synthetic qif-<hash>) and SimpleFIN.
                // Writing t.ExternalId here would pollute the OFX-only
                // online_match_fitid column with QIF's synthetic ids.
                OnlineMatchFitid = t.OnlineMatchFitid,
                OnlineMatchFiId = t.OnlineMatchFiId,
                IngestActionHint = t.Action,
                // Mig 113: per-row investment prefill carriers.
                // Populated only on OFX investment rows where the
                // provider extracted Shares/UnitPrice/Fee from the
                // wire (UNITS/UNITPRICE/COMMISSION etc.); null on
                // bank/credit rows.
                IngestShares = t.Shares,
                IngestUnitPrice = t.UnitPrice,
                IngestFee = t.Fee,
                // Mig 114: persist the provider's ticker hint
                // string so the SPA's Accept flow can record a
                // provider_security_mapping with the same key the
                // next ingest will look up.
                IngestSecurityTickerHint = t.SecurityTickerHint,
            });
            _db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = context.LedgerId,
                AccountId = context.AccountId,
                PostingIndex = 0,
                Amount = t.Amount,
            });
            _db.TxnLegs.Add(new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = headerId,
                LedgerId = context.LedgerId,
                AccountId = uncategorizedAccountId.Value,
                PostingIndex = 0,
                Amount = -t.Amount,
            });
            transactionsForReview++;
        }

        run.Status = parsed.Errors.Count > 0 ? "partial" : "completed";
        run.DetailsJson = LedgerOperationDetails.Serialize(new IngestRunDetails
        {
            TxnsFetched = filtered.Count,
            TxnsInserted = transactionsForReview,
            TxnsAlreadyKnown = alreadyKnown,
        });
        run.CompletedAt = DateTime.UtcNow;
        foreach (var e in parsed.Errors)
        {
            _db.LedgerOperationErrors.Add(new LedgerOperationErrorRow
            {
                Id = Guid.NewGuid(),
                LedgerOperationId = run.Id,
                LedgerId = context.LedgerId,
                Code = e.Code,
                Message = e.Message,
                SimpleFinConnectionId = e.ConnectionId,
                SimpleFinAccountId = e.AccountId,
            });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new IngestRunOutcome(
            SyncRunId: run.Id,
            AccountsDiscovered: parsed.DiscoveredAccounts.Count,
            TransactionsForReview: transactionsForReview,
            TransactionsStillPending: 0,                 // OFX statements are post-clear
            AlreadyKnown: alreadyKnown,
            ConnectionStatus: "active",                  // no connection state for file uploads
            Errors: parsed.Errors);
    }

    /// <summary>
    /// Preview a file upload — parse only, no DB writes, no
    /// <c>sync_runs</c> row. The SPA's mapping wizard uses this to
    /// show "this file has 3 accounts" before the user confirms
    /// mappings. Same provider, same parser; just bypasses the
    /// orchestrator's write path.
    /// </summary>
    public async Task<FileResult> PreviewFileAsync(
        string providerKey,
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);
        if (!_fileProviders.TryGetValue(providerKey, out var provider))
        {
            throw new InvalidOperationException(
                $"No IFileProvider registered for provider key '{providerKey}'.");
        }
        return await provider.ParseAsync(payload, context, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Map from <see cref="IFileProvider.ProviderKey"/> to the
    /// <c>txn_headers.origin</c> value the schema's CHECK
    /// constraint allows. Explicit table rather than derived so
    /// adding a new provider also requires updating the origin
    /// allow-list.
    /// </summary>
    /// <summary>
    /// Per-row origin metadata for a file provider, post mig 107.
    /// </summary>
    /// <param name="Origin">Icon-level mechanism — one of
    /// <c>online_import</c> / <c>file_import</c>.</param>
    /// <param name="ProviderKey">Specific provider tag persisted to
    /// <c>txn_headers.provider_key</c> — e.g. <c>ofx</c>,
    /// <c>csv</c>.</param>
    private readonly record struct FileOriginMetadata(string Origin, string ProviderKey);

    private static readonly IReadOnlyDictionary<string, FileOriginMetadata> ProviderOriginFor =
        new Dictionary<string, FileOriginMetadata>(StringComparer.Ordinal)
        {
            ["ofx"] = new FileOriginMetadata("file_import", "ofx"),
            // ADR-0042: QIF file import. origin = file_import (icon-
            // level), provider_key = qif. The CHECK constraint already
            // admits both values (MD-transited QIF rows carry them).
            ["qif"] = new FileOriginMetadata("file_import", "qif"),
        };

    // ----- shared helpers (provider-agnostic write paths) -----

    /// <summary>
    /// Insert new <c>feed_connection_accounts</c> rows for any
    /// previously unseen external_ids; refresh display fields +
    /// bump last_seen_at for known ones (slice 2c.4). Flush is
    /// deferred to the closing SaveChanges in <see cref="RunPullAsync"/>
    /// so the upsert lands in the same write batch as the run-row
    /// close + child inserts.
    /// </summary>
    private async Task UpsertConnectionAccountsAsync(
        Guid ledgerId,
        Guid connectionId,
        IReadOnlyList<PullAccount> pullAccounts,
        CancellationToken cancellationToken)
    {
        if (pullAccounts.Count == 0) return;
        var incomingIds = pullAccounts.Select(a => a.ExternalId).ToArray();
        var existing = await _db.FeedConnectionAccounts
            .Where(d => d.FeedConnectionId == connectionId
                        && incomingIds.Contains(d.ExternalId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byExternalId = existing.ToDictionary(d => d.ExternalId, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        foreach (var pa in pullAccounts)
        {
            if (byExternalId.TryGetValue(pa.ExternalId, out var row))
            {
                row.Name = pa.Name;
                row.OrgName = pa.OrgName;
                row.Currency = pa.Currency;
                row.Balance = pa.Balance;
                row.BalanceAt = pa.BalanceAt;
                row.LastSeenAt = now;
                // ADR-0031 follow-up: latest-snapshot semantics.
                // Always overwrite when the provider sent fresh JSON;
                // skip the write when null so a future sync that
                // doesn't carry the payload doesn't blank out a
                // previously-captured one.
                if (pa.RawAccountPayload is not null)
                    row.LastProviderRawPayload = pa.RawAccountPayload;
            }
            else
            {
                _db.FeedConnectionAccounts.Add(new FeedConnectionAccountRow
                {
                    Id = Guid.NewGuid(),
                    FeedConnectionId = connectionId,
                    LedgerId = ledgerId,
                    ExternalId = pa.ExternalId,
                    Name = pa.Name,
                    OrgName = pa.OrgName,
                    Currency = pa.Currency,
                    Balance = pa.Balance,
                    BalanceAt = pa.BalanceAt,
                    LastSeenAt = now,
                    LastProviderRawPayload = pa.RawAccountPayload,
                });
            }
        }
    }

    /// <summary>
    /// Lazy-resolve the per-ledger Uncategorized expense category.
    /// Slice 2c lands every bank-feed counterparty leg here; the
    /// user re-categorizes when they approve the row. <c>is_system=true</c>
    /// so mapping wizards / account pickers can filter it out.
    /// </summary>
    private async Task<Guid> EnsureUncategorizedAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var existing = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.IsSystem
                        && a.AccountType == "category"
                        && a.Name == UncategorizedName)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return existing.Value;

        var inserted = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Name = UncategorizedName,
            AccountType = "category",
            CategoryKind = "expense",
            CurrencyCode = "USD",
            OpeningBalance = 0m,
            IsActive = true,
            IsSystem = true,
        };
        _db.Accounts.Add(inserted);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return inserted.Id;
    }

    /// <summary>
    /// Promote-on-clear leg amount update: the bank may have
    /// changed the cleared amount (restaurant tip, exchange rate)
    /// between pending and posted states. Rewrite the source-side
    /// leg to <paramref name="amount"/> + the counterparty leg to
    /// its negation, preserving the symmetric-posting invariant
    /// (ADR-0019). Single-row only — promote-on-clear never
    /// applies to multi-splits (sync-written rows are always
    /// single postings).
    /// </summary>
    private async Task<decimal> UpdateLegAmountsAsync(
        Guid headerId,
        Guid sourceAccountId,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var legs = await _db.TxnLegs
            .Where(l => l.HeaderId == headerId && l.PostingIndex == 0)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var sourceLeg = legs.FirstOrDefault(l => l.AccountId == sourceAccountId);
        var wasAmount = sourceLeg?.Amount ?? 0m;
        foreach (var leg in legs)
        {
            leg.Amount = leg.AccountId == sourceAccountId ? amount : -amount;
        }
        return wasAmount;
    }

    private const string UncategorizedName = "Uncategorized";

    /// <summary>Lazy stale-run reaper threshold (slice 2c.2). A
    /// <c>sync_runs</c> row sitting in <c>running</c> longer than
    /// this is assumed to be from a crashed process — the next
    /// sync against the connection sweeps it into <c>failed</c>
    /// before attempting its own INSERT.</summary>
    private static readonly TimeSpan StaleRunTimeout = TimeSpan.FromMinutes(10);

    private static bool IsUniqueViolation(DbUpdateException ex, string constraintName)
    {
        if (ex.InnerException is not Npgsql.PostgresException pg) return false;
        if (pg.SqlState != "23505") return false;
        return pg.ConstraintName == constraintName;
    }
}
