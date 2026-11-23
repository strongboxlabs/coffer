using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Ingest.Ofx;
using Coffer.Api.Sync.SimpleFin;
using Coffer.Domain.Annotations;
using Coffer.Domain.Investment;

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
///
/// <para><b>The ledger write lock</b> (migration 275). The saves that can recompute
/// (Uncategorized's first creation, which flushes what the run has tracked, and the
/// run's final save) each run in a transaction that takes the ledger's write lock as its
/// first statement; the provider fetch and the file parse never run under it. The
/// accounts' sync watermark is written in the final save's transaction, so it moves only
/// with the rows it covers. A lock held past the wait refuses the run: it is closed as
/// failed with nothing it fetched saved (<see cref="IngestFailureReason.LedgerBusy"/> for
/// a pull, <see cref="LedgerWriteBusyException"/> for a file). A run whose save fails for
/// any other reason is closed as failed the same way, with what it had tracked dropped,
/// and the failure is thrown to the caller.</para>
/// </remarks>
public sealed class IngestOrchestrator
{
    private readonly AppDbContext _db;
    private readonly SyncConnectionLock _connectionLock;
    private readonly IReadOnlyDictionary<string, IPullProvider> _pullProviders;
    private readonly IReadOnlyDictionary<string, IFileProvider> _fileProviders;
    private readonly Coffer.Api.Quotes.QuoteOrchestrator _quotes;
    private readonly ILogger<IngestOrchestrator> _logger;
    private readonly TimeProvider _clock;

    public IngestOrchestrator(
        AppDbContext db,
        SyncConnectionLock connectionLock,
        IEnumerable<IPullProvider> pullProviders,
        IEnumerable<IFileProvider> fileProviders,
        Coffer.Api.Quotes.QuoteOrchestrator quotes,
        ILogger<IngestOrchestrator> logger,
        TimeProvider clock)
    {
        _db = db;
        _connectionLock = connectionLock;
        _pullProviders = pullProviders.ToDictionary(p => p.ProviderKey);
        _fileProviders = fileProviders.ToDictionary(p => p.ProviderKey);
        _quotes = quotes;
        _logger = logger;
        _clock = clock;
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
    /// <param name="triggeredVia">
    /// How this run was provoked — <c>manual</c> or <c>scheduled</c>. It lands on the
    /// ledger_operations row, which is what the Activity timeline reads, so a scheduled
    /// run labelled "manual" would be the log telling the reader something untrue about
    /// who did it.
    /// </param>
    public async Task<IngestPullOutcome> RunPullAsync(
        Guid ledgerId,
        Guid connectionId,
        Guid triggeredByUserId,
        string? accountIdFilter = null,
        CancellationToken cancellationToken = default,
        string triggeredVia = "manual")
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
        var sweepAt = _clock.GetUtcNow().UtcDateTime;
        var staleCutoff = sweepAt - StaleRunTimeout;
        await _db.LedgerOperations
            .Where(r => r.FeedConnectionId == connectionId
                        && r.Status == "running"
                        && r.StartedAt < staleCutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.ErrorMessage, "Interrupted — process exited before sync completed.")
                .SetProperty(r => r.CompletedAt, sweepAt),
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
            TriggeredVia = triggeredVia,
            FeedConnectionId = connectionId,
            TriggeredByUserId = triggeredByUserId,
            Status = "running",
            StartedAt = _clock.GetUtcNow().UtcDateTime,
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
        // the pull returns, and each account's currency to fit a new
        // row's amount to its places (ADR-0114 D15).
        var mappedAccountsForWindow = await _db.Accounts.AsNoTracking()
            .Where(a => a.FeedConnectionId == connectionId
                        && a.ExternalId != null
                        && (accountIdFilter == null
                            || a.ExternalId == accountIdFilter))
            .Select(a => new { a.Id, a.ExternalId, a.LastSimpleFinSyncAt, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pullContext = new PullContext(
            Connection: keyMaterial.Connection,
            LedgerWrappedLek: keyMaterial.LedgerWrappedLek,
            MappedAccounts: mappedAccountsForWindow
                .Select(a => new MappedAccountWatermark(
                    LedgerAccountId: a.Id,
                    ExternalId: a.ExternalId!,
                    LastSyncedAt: a.LastSimpleFinSyncAt,
                    CurrencyCode: a.CurrencyCode))
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
            run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            // See the SimpleFinException catch below: closing a run is never the
            // thing to cancel.
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex,
                "Ingest pull for connection {ConnectionId} failed: access URL ciphertext could not be unwrapped under the current master KEK",
                connectionId);
            return IngestPullOutcome.Fail(IngestFailureReason.AccessUrlCorrupted);
        }
        catch (SimpleFinException ex)
        {
            // Provider-side HTTP / parse fault. Stamp the run and return a TYPED failure
            // rather than rethrowing.
            //
            // There is no SimpleFinException handler on the sync path, so a rethrow
            // reaches UseExceptionHandler and the caller gets a 500. In the sync-all loop
            // it is worse than a wrong status code — the exception unwinds the whole loop,
            // so one unreachable bank silently skips every other connection on the
            // ledger, against that endpoint's documented promise that per-connection
            // failures do not cascade.
            //
            // Handled here rather than in the loop because the loop is not the only
            // caller that must not unwind: the scheduled sync has exactly the same
            // requirement, and a rule stated in two places is a rule that will drift.
            run.Status = "failed";
            run.ErrorMessage = ex.Message;
            run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            // CancellationToken.None, not the request's: if the caller's token is
            // what aborted the pull, saving under it throws too and the run is left
            // open — which is the state that wedges the connection. Closing a run is
            // never the thing to cancel.
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "Ingest pull for connection {ConnectionId} failed: the provider faulted.",
                connectionId);
            return IngestPullOutcome.Fail(IngestFailureReason.ProviderFault);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or a disconnected client. The pull is over either way,
            // and an open run blocks the next sync on this connection, so close it
            // before letting the cancellation continue to unwind.
            run.Status = "failed";
            run.ErrorMessage = CancelledDetail;
            run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Defensive 403 path: provider detected revoked / expired
        // auth. Flip the connection to needs_reauth, stamp
        // last_synced_at so the user sees the attempt happened,
        // close the run, and return an empty outcome so the SPA can
        // render the re-connect call-to-action.
        if (pullResult.RequiresReauth)
        {
            var reauthAt = _clock.GetUtcNow().UtcDateTime;
            await _db.FeedConnections
                .Where(c => c.Id == connectionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, "needs_reauth")
                    .SetProperty(c => c.LastSyncedAt, reauthAt),
                    cancellationToken)
                .ConfigureAwait(false);
            run.Status = "needs_reauth";
            run.CompletedAt = reauthAt;
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
            .ToDictionary(a => a.ExternalId!, a => (a.Id, a.CurrencyCode));

        // Uncategorized counterparty: lazy-resolved on first row
        // this sync writes. Caches for the rest of the loop.
        Guid? uncategorizedAccountId = null;

        foreach (var pullAccount in pullResult.Accounts)
        {
            if (!mappedByExternalId.TryGetValue(pullAccount.ExternalId, out var mapped))
            {
                // Unmapped — directory upsert already recorded it.
                continue;
            }
            var (ledgerAccountId, accountCurrency) = mapped;

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

            // The rows the payee/memo backfill below may write, read in one batch so
            // a resync costs the same round trips however many rows it re-sees.
            var backfillCandidates = pullAccount.Transactions
                .Where(t => t.Payee is not null)
                .Select(t => existingByExternalId.GetValueOrDefault(t.ExternalId) is { } e
                             && string.Equals(e.Payee, t.Description, StringComparison.Ordinal) ? e : null)
                .OfType<TxnHeaderRow>()
                .Distinct()
                .ToList();
            var storedForBackfill = (await AnnotationWriter.LoadManyAsync(_db, backfillCandidates, cancellationToken)
                    .ConfigureAwait(false))
                .ToDictionary(s => s.Header.Id);
            var backfillWrites = new List<AnnotationWriter.PlannedWrite>();

            // The originals the backfill may restate and a promote-on-clear restates
            // (ADR-0114 D15), tracked and read in the same one batch, so a resync costs
            // the same round trips however many rows it re-sees.
            var originalsFor = backfillCandidates.Select(h => h.Id)
                .Concat(pullAccount.Transactions
                    .Where(t => !t.Pending)
                    .Select(t => existingByExternalId.GetValueOrDefault(t.ExternalId))
                    .Where(e => e is { IsPending: true })
                    .Select(e => e!.Id))
                .Distinct()
                .ToArray();
            var originalsOnFile = originalsFor.Length == 0
                ? new Dictionary<Guid, TxnHeaderOriginalRow>()
                : await _db.TxnHeaderOriginals
                    .Where(o => originalsFor.Contains(o.HeaderId))
                    .ToDictionaryAsync(o => o.HeaderId, cancellationToken)
                    .ConfigureAwait(false);

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
                    // The heuristic is the whole guard, and since migration
                    // 230 it is the ONLY guard: this row IS the user's copy now,
                    // so a rewrite here would overwrite an edit rather than
                    // sitting harmlessly underneath one. It only fires when the
                    // row still looks exactly as the pre-split importer left it
                    // (memo empty AND payee byte-equal to the raw description),
                    // which no edited row can satisfy — an edit that produced
                    // that shape reproduced the feed's own values, so writing
                    // the split is what the user would get anyway. The feed's
                    // original is in txn_header_originals either way.
                    //
                    // "Memo empty" is asked of what the synced account's register
                    // shows, through the one presentation rule, because the memo no
                    // longer sits on the header of a one-split; and the description is
                    // written through the write primitive as that account's own memo.
                    // A row that account may not write (a split's opposite side) is
                    // left alone.
                    //
                    // The originals are restated too where they still hold the values the
                    // backfill replaces, the description as payee and no memo (ADR-0114
                    // D15): payee recall anchors on the originals' payee, so a row edited
                    // before the payee arrived would otherwise be recalled by the raw
                    // description, unlike a row with no originals. Originals that hold
                    // anything else are the feed's own, and are left.
                    string? backfilledMemo = null;
                    if (t.Payee is not null
                        && string.Equals(existing.Payee, t.Description, StringComparison.Ordinal)
                        && storedForBackfill.TryGetValue(existing.Id, out var storedRow)
                        && PreSplitMemoPlan(storedRow, ledgerAccountId, t.Description) is { } split)
                    {
                        existing.Payee = t.Payee;
                        if (split.Plan is { } memoPlan)
                        {
                            backfillWrites.Add(new AnnotationWriter.PlannedWrite(existing, split.Legs, memoPlan));
                            backfilledMemo = t.Description;
                        }
                        if (originalsOnFile.TryGetValue(existing.Id, out var preSplitOriginals)
                            && string.Equals(preSplitOriginals.Payee, t.Description, StringComparison.Ordinal)
                            && string.IsNullOrEmpty(preSplitOriginals.Memo))
                        {
                            preSplitOriginals.Payee = t.Payee;
                            preSplitOriginals.Memo = t.Description;
                        }
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
                        var promoted = await PromoteClearedAsync(
                                existing, ledgerAccountId, t.Amount,
                                originalsOnFile.GetValueOrDefault(existing.Id), backfilledMemo, cancellationToken)
                            .ConfigureAwait(false);
                        promotions.Add((existing.Id, promoted.Was, promoted.Became));
                        transactionsForReview++;
                        continue;
                    }
                    alreadyKnown++;
                    continue;
                }

                // New external_id — insert one txn_header + a
                // symmetric posting (ADR-0019) on the mapped account
                // ↔ Uncategorized.
                try
                {
                    uncategorizedAccountId ??= await EnsureUncategorizedAsync(
                        ledgerId, cancellationToken).ConfigureAwait(false);
                }
                catch (LedgerWriteBusyException)
                {
                    await CloseRunAsBusyAsync(run).ConfigureAwait(false);
                    return IngestPullOutcome.Fail(IngestFailureReason.LedgerBusy);
                }
                catch (Exception ex)
                {
                    // Its save flushes what the run has tracked, as the final save does.
                    await CloseRunAsFailedToSaveAsync(run, ex, PullSaveFailedDetail(ex)).ConfigureAwait(false);
                    throw;
                }

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
                // Mig 107: origin is icon-level mechanism; provider_key is the
                // specific provider. ExternalId is the universal per-provider dedup
                // key (mig 105): SimpleFIN's transaction id (e.g. `TRN-<uuid>`) lands
                // here; soft-hide on DELETE preserves it so the next sync sees the
                // row and doesn't re-insert. online_match_fitid / online_match_fi_id
                // are NOT written by SimpleFIN — they're OFX-protocol fields used by
                // the MD importer (preserving OFX state) and by OFX/QFX direct
                // importers. Owned by the account it was imported into (mig 263),
                // as a hand-entered row is; feeds follow no other rule.
                var header = TxnHeaderRow.NewIngested(
                    headerId, ledgerId, ledgerAccountId,
                    origin: "online_import", externalId: t.ExternalId);
                header.ProviderKey = "simplefin";
                // Which RUN, not just which provider (mig 221) — so this pull can
                // be undone as a set. provider_key alone made a repeated import
                // indistinguishable from the first.
                header.LedgerOperationId = run.Id;
                // SimpleFIN v2 split: cleaned merchant name goes
                // to Payee, raw bank/broker text goes to Memo.
                // Fall back to Description for the Payee when the
                // provider didn't send a payee field (older feeds
                // / non-SimpleFIN providers) so the user still
                // sees something in the register's Payee column.
                header.Payee = t.Payee ?? t.Description;
                header.PostedAt = t.PostedAt;
                // NOT NULL since mig 189: feeds often omit a transacted date, and
                // "no distinct tax date" is stored as the posted date.
                header.TransactedAt = t.TransactedAt ?? t.PostedAt;
                header.IsPending = t.Pending;
                header.NeedsReview = true;
                header.IngestActionHint = t.Action;
                // Mig 114: persist the classifier-extracted
                // ticker string on every provider's path (was
                // OFX-only initially, but the SPA Accept flow
                // needs ONE rail — see InvestmentRegisterPage
                // edit-on-row branch). Backfilling pre-mig-114
                // rows isn't necessary; the SPA falls back to
                // re-running the classifier on the payee when
                // this column is null.
                header.IngestSecurityTickerHint = t.SecurityTickerHint;
                // ADR-0031 follow-up: persist provider's verbatim
                // JSON for the inserted row. Pure pass-through —
                // SimpleFinPullProvider captured it via
                // JsonElement.GetRawText() before any field
                // projection, so the stored payload preserves
                // anything we don't currently model.
                header.ProviderRawPayload = t.RawProviderPayload;
                _db.TxnHeaders.Add(header);
                await AddIngestedLegsAsync(
                        header, ledgerAccountId, uncategorizedAccountId.Value, t.Amount, accountCurrency,
                        t.Payee is not null ? t.Description : null, cancellationToken)
                    .ConfigureAwait(false);
                if (t.Pending) transactionsStillPending++;
                else transactionsForReview++;
            }

            await AnnotationWriter.ApplyManyAsync(_db, backfillWrites, cancellationToken).ConfigureAwait(false);
        }

        // Stamp last_synced_at + clear any prior needs_reauth /
        // error status now that we have a clean round trip.
        // connection.last_synced_at is the LAST ATTEMPT timestamp
        // (display only); the per-account watermark below drives
        // the next sync's start-date math.
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
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
        run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
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

        // The run's writes, in one transaction that takes the ledger's write lock as its
        // first statement (migration 275): the save recomputes balances, and the
        // watermark's update row-locks accounts a trade's re-derivation also locks, so
        // both take their turn among the ledger's writers. A watermark written with the
        // rows it covers never moves past rows that were not saved.
        LedgerWriteScope write;
        try
        {
            write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        }
        catch (LedgerWriteBusyException)
        {
            await CloseRunAsBusyAsync(run).ConfigureAwait(false);
            return IngestPullOutcome.Fail(IngestFailureReason.LedgerBusy);
        }
        try
        {
            await using (write)
            {
                if (advanceForAccountIds.Length > 0)
                {
                    await _db.Accounts
                        .Where(a => advanceForAccountIds.Contains(a.Id))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(a => a.LastSimpleFinSyncAt, nowUtc),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await write.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Rolled back with nothing saved. The rows the save would have written stay
            // tracked unless dropped here, and the scheduler saves its own bookkeeping on
            // this context next.
            await CloseRunAsFailedToSaveAsync(run, ex, PullSaveFailedDetail(ex)).ConfigureAwait(false);
            throw;
        }

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

        // The account imported into: its currency fits each amount to its places
        // (ADR-0114 D15), and must be the statement's (ADR-0114 D13). Loaded before the
        // parse so the provider reads it from its context: QIF refuses an amount whose
        // comma reads two ways in a three-place currency (ADR-0114 D15).
        var accountCurrency = await AccountCurrencyAsync(context, cancellationToken).ConfigureAwait(false);
        context = context with { CurrencyCode = accountCurrency };

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

        // A statement in another currency than the account's is refused before the run
        // opens, so nothing is written (ADR-0114 D13; ADR-0031 Phase 4). Only a format that
        // states its currency (OFX and QFX, CURDEF) is checked: QIF and CSV state none. A
        // stated code that is not the account's, whatever its shape, is refused.
        if (context.ProviderAccountId is not null
            && parsed.DiscoveredAccounts.FirstOrDefault(a => a.ProviderAccountId == context.ProviderAccountId)
                is { Currency: { } statementCurrency }
            && !string.IsNullOrWhiteSpace(statementCurrency)
            && statementCurrency.Trim().ToUpperInvariant() is var statementCode
            && !string.Equals(statementCode, accountCurrency, StringComparison.Ordinal))
        {
            throw new StatementCurrencyMismatchException(statementCode, accountCurrency);
        }

        var run = new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = context.LedgerId,
            Family = "ingest",
            ProviderKey = providerKey,
            TriggeredVia = context.TriggeredVia,     // file-upload, but manual for the Demo's import
            FeedConnectionId = null,                 // file uploads have no connection
            TriggeredByUserId = context.TriggeredByUserId,
            Status = "running",
            StartedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.LedgerOperations.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The run row is saved as running, and nothing sweeps a file import's run (the
        // stale-run sweep is by connection, and a file import has none), so every way out of
        // what follows but the return closes it: the busy refusal as busy, and anything else,
        // a cancellation among them, as failed in that failure's words.
        (int ForReview, int AlreadyKnown) saved;
        try
        {
            saved = await SaveFileRunAsync(run, context, origin, filtered, parsed, accountCurrency, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LedgerWriteBusyException)
        {
            await CloseRunAsBusyAsync(run).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await CloseRunAsFailedToSaveAsync(run, ex, ImportFailedDetail(ex)).ConfigureAwait(false);
            throw;
        }

        return new IngestRunOutcome(
            SyncRunId: run.Id,
            AccountsDiscovered: parsed.DiscoveredAccounts.Count,
            TransactionsForReview: saved.ForReview,
            TransactionsStillPending: 0,                 // OFX statements are post-clear
            AlreadyKnown: saved.AlreadyKnown,
            ConnectionStatus: "active",                  // no connection state for file uploads
            Errors: parsed.Errors);
    }

    /// <summary>
    /// The body of <see cref="RunFileAsync"/> once its run row is saved: the dedup against
    /// what the ledger holds, the new rows added for review, and the one save, under the
    /// ledger's write lock, that writes them with the run's close. Returns how many rows went
    /// to review and how many were already known.
    /// </summary>
    /// <remarks>The caller closes the run on any exception, so nothing here catches one to
    /// close it.</remarks>
    private async Task<(int ForReview, int AlreadyKnown)> SaveFileRunAsync(
        LedgerOperationRow run,
        FileIngestContext context,
        FileOriginMetadata origin,
        IReadOnlyList<IngestedTransaction> filtered,
        FileResult parsed,
        string accountCurrency,
        CancellationToken cancellationToken)
    {
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
            if (existingByExternalId.TryGetValue(t.ExternalId, out var knownById))
            {
                BackfillIngestCarriers(knownById, t);
                alreadyKnown++;
                continue;
            }
            // OFX cross-source: skip if a non-merged header already
            // carries this exact (FiId, Fitid) OFX-protocol pair.
            if (isOfx
                && t.OnlineMatchFiId is not null
                && t.OnlineMatchFitid is not null
                && existingByOnlineMatch.TryGetValue(
                    (t.OnlineMatchFiId, t.OnlineMatchFitid), out var knownByMatch))
            {
                BackfillIngestCarriers(knownByMatch, t);
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
            // External_id is the universal dedup key (mig 105);
            // online_match_fitid + online_match_fi_id are the
            // OFX-protocol fields — populated natively here so
            // future cross-source dedup against MD-imported
            // rows that preserved the same OFX FITID works.
            // OFX-protocol-only: populated by the OFX provider
            // (== ExternalId == FITID); null for QIF (whose
            // ExternalId is a synthetic qif-<hash>) and SimpleFIN.
            // Writing t.ExternalId there would pollute the OFX-only
            // online_match_fitid column with QIF's synthetic ids.
            // Owned by the account imported into (mig 263).
            var header = TxnHeaderRow.NewIngested(
                headerId, context.LedgerId, context.AccountId,
                origin: origin.Origin, externalId: t.ExternalId,
                onlineMatchFitid: t.OnlineMatchFitid, onlineMatchFiId: t.OnlineMatchFiId);
            header.ProviderKey = origin.ProviderKey;
            // Mig 221. Load-bearing for file uploads specifically: they have no
            // dedup, so re-uploading the same statement inserts a second full set,
            // and this stamp is the only thing that can tell the two apart.
            header.LedgerOperationId = run.Id;
            header.Payee = t.Payee ?? t.Description;
            header.PostedAt = t.PostedAt;
            // NOT NULL since mig 189 — see the pull path above.
            header.TransactedAt = t.TransactedAt ?? t.PostedAt;
            header.IsPending = false;                       // OFX statements are post-clear
            header.NeedsReview = true;
            header.IngestActionHint = t.Action;
            // Mig 113: per-row investment prefill carriers.
            // Populated only on OFX investment rows where the
            // provider extracted Shares/UnitPrice/Fee from the
            // wire (UNITS/UNITPRICE/COMMISSION etc.); null on
            // bank/credit rows.
            header.IngestShares = t.Shares;
            header.IngestUnitPrice = t.UnitPrice;
            header.IngestFee = t.Fee;
            // Mig 228: the file's authoritative total, for rows whose own
            // amount is deliberately zero (a reinvest moves no cash).
            // Without it the editor rebuilds the figure from shares x price
            // and lands a cent out.
            header.IngestAmount = t.IngestAmount;
            // Mig 114: persist the provider's ticker hint
            // string so the SPA's Accept flow can record a
            // provider_security_mapping with the same key the
            // next ingest will look up.
            header.IngestSecurityTickerHint = t.SecurityTickerHint;
            // Parity with the pull path, which has always written this. No FILE
            // provider populated RawProviderPayload until the brokerage shims
            // (ADR-0098) — OFX and QIF both pass null — so this line was never
            // missed, and the trail a shim builds was computed and then dropped on
            // the floor for every row. It is the whole provenance story for a
            // converted format: the CSV line a row came from and the QIF record it
            // became, which is what anyone asking "why is this a buy?" needs.
            header.ProviderRawPayload = t.RawProviderPayload;
            _db.TxnHeaders.Add(header);
            await AddIngestedLegsAsync(
                    header, context.AccountId, uncategorizedAccountId.Value, t.Amount, accountCurrency,
                    t.Payee is not null ? t.Description : null, cancellationToken)
                .ConfigureAwait(false);
            transactionsForReview++;
        }

        run.Status = parsed.Errors.Count > 0 ? "partial" : "completed";
        run.DetailsJson = LedgerOperationDetails.Serialize(new IngestRunDetails
        {
            TxnsFetched = filtered.Count,
            TxnsInserted = transactionsForReview,
            TxnsAlreadyKnown = alreadyKnown,
        });
        run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
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

        // The save recomputes balances: a transaction that takes the ledger's write lock
        // as its first statement (migration 275). A lock held past the wait closes the
        // run as failed and reaches the caller as the busy refusal (a 422).
        await using (var write = await _db.BeginLedgerWriteAsync(context.LedgerId, cancellationToken).ConfigureAwait(false))
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (transactionsForReview, alreadyKnown);
    }

    /// <summary>
    /// Preview a file upload — parse only, no DB writes, no
    /// <c>sync_runs</c> row. The SPA's mapping wizard uses this to
    /// show "this file has 3 accounts" before the user confirms
    /// mappings. Same provider, same parser; just bypasses the
    /// orchestrator's write path.
    /// </summary>
    /// <remarks>
    /// When the caller names the account the file will go into, its currency reaches the
    /// provider as it does at import, so the preview reports what the import will
    /// (ADR-0114 D15: QIF's three-place comma rule). A preview that names no account
    /// parses with no currency.
    /// </remarks>
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
        if (context.AccountId != Guid.Empty)
        {
            var accountCurrency = await AccountCurrencyAsync(context, cancellationToken).ConfigureAwait(false);
            context = context with { CurrencyCode = accountCurrency };
        }
        return await provider.ParseAsync(payload, context, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The currency of the account a file goes into, which must be in the
    /// context's ledger.</summary>
    private async Task<string> AccountCurrencyAsync(FileIngestContext context, CancellationToken cancellationToken) =>
        await _db.Accounts.AsNoTracking()
            .Where(a => a.Id == context.AccountId && a.LedgerId == context.LedgerId)
            .Select(a => a.CurrencyCode)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException("The account to import into is not in this ledger.");

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
    /// <summary>
    /// What a file import writes to <c>txn_headers</c>. Internal rather than private so
    /// the completeness guard can read it; see <see cref="OriginRegistry"/>.
    /// </summary>
    internal readonly record struct FileOriginMetadata(string Origin, string ProviderKey);

    /// <summary>
    /// Every file provider's origin metadata, exposed so a test can prove the map is
    /// complete.
    /// </summary>
    /// <remarks>
    /// Registering a provider in DI without adding it here compiles, starts, previews
    /// and then throws at IMPORT — after the user has chosen a file, an account, and
    /// pressed the button. Nothing else can see this map, so the completeness test
    /// over it is what fails a provider registered without an entry.
    /// </remarks>
    internal static IReadOnlyDictionary<string, FileOriginMetadata> OriginRegistry =>
        ProviderOriginFor;

    private static readonly IReadOnlyDictionary<string, FileOriginMetadata> ProviderOriginFor =
        new Dictionary<string, FileOriginMetadata>(StringComparer.Ordinal)
        {
            ["ofx"] = new FileOriginMetadata("file_import", "ofx"),
            // ADR-0042: QIF file import. origin = file_import (icon-
            // level), provider_key = qif. The CHECK constraint already
            // admits both values (MD-transited QIF rows carry them).
            ["qif"] = new FileOriginMetadata("file_import", "qif"),
            // ADR-0031 Phase 5. origin = file_import (icon-level, same as its siblings);
            // provider_key names the generic reader rather than an institution, because
            // one mapping document per institution is the whole point — the provider is
            // shared and the FORMAT is what varies.
            [Csv.CsvGenericFileProvider.Key] =
                new FileOriginMetadata("file_import", Csv.CsvGenericFileProvider.Key),
            // ADR-0031 Phase 6. origin = file_import like its siblings; provider_key
            // names the BROKERAGE, unlike the generic reader above — provider_security_mappings
            // is keyed by it, so a symbol learned from a Fidelity file must be remembered
            // under the Fidelity provider rather than pooled with every other CSV.
            [Csv.FidelityActivityFileProvider.Key] =
                new FileOriginMetadata("file_import", Csv.FidelityActivityFileProvider.Key),
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

        var now = _clock.GetUtcNow().UtcDateTime;
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
    /// Fill in per-row ingest carriers a re-import can supply and the stored
    /// row is missing. Only ever writes over NULL.
    /// </summary>
    /// <remarks>
    /// <para>If re-importing the same file were a pure no-op — the row counted as
    /// already known and skipped — every carrier added after a row was first
    /// imported would be permanently unavailable for it, with no way to repair a
    /// row short of deleting it. The pull path backfills <c>provider_raw_payload</c>
    /// on the same reasoning.</para>
    ///
    /// <para>For example, <c>ingest_amount</c> (mig 228) carries the total an OFX
    /// REINVEST states, because the editor otherwise rebuilds it from shares x
    /// price and lands a cent out. A row imported before that column existed
    /// acquires it by re-importing the file, rather than the re-import reporting a
    /// dedup and changing nothing.</para>
    ///
    /// <para>NULL-ONLY, and metadata only. It never overwrites a value already
    /// stored (once captured, a payload is an archive), and it touches no
    /// amount, payee, memo or any field a person may have edited — the money
    /// lives on txn_legs and is not in scope here. So it is safe on an accepted
    /// row, where it simply improves what a later reader can recover, and it is
    /// the actual repair on a still-unreviewed one, where the editor reads
    /// these carriers when the row is opened.</para>
    /// </remarks>
    private static void BackfillIngestCarriers(TxnHeaderRow existing, IngestedTransaction t)
    {
        if (existing.ProviderRawPayload is null && t.RawProviderPayload is not null)
            existing.ProviderRawPayload = t.RawProviderPayload;
        if (existing.IngestAmount is null && t.IngestAmount is not null)
            existing.IngestAmount = t.IngestAmount;
        if (existing.IngestShares is null && t.Shares is not null)
            existing.IngestShares = t.Shares;
        if (existing.IngestUnitPrice is null && t.UnitPrice is not null)
            existing.IngestUnitPrice = t.UnitPrice;
        if (existing.IngestFee is null && t.Fee is not null)
            existing.IngestFee = t.Fee;
        if (existing.IngestActionHint is null && t.Action is not null)
            existing.IngestActionHint = t.Action;
        if (existing.IngestSecurityTickerHint is null && t.SecurityTickerHint is not null)
            existing.IngestSecurityTickerHint = t.SecurityTickerHint;
    }

    /// <summary>
    /// Lazy-resolve the per-ledger Uncategorized expense category.
    /// Slice 2c lands every bank-feed counterparty leg here; the
    /// user re-categorizes when they approve the row. <c>is_system=true</c>
    /// so mapping wizards / account pickers can filter it out. The identity is
    /// shared with the Moneydance importer (<see cref="UncategorizedCategory"/>),
    /// which lands MD's blank-named category on the same row.
    /// </summary>
    /// <remarks>Its save flushes everything the run has tracked so far (a promoted row's
    /// amounts among them), so it can recompute balances: it runs in a transaction that
    /// takes the ledger's write lock first, and a lock held past the wait throws
    /// <see cref="LedgerWriteBusyException"/> with nothing saved.</remarks>
    private async Task<Guid> EnsureUncategorizedAsync(
        Guid ledgerId, CancellationToken cancellationToken)
    {
        var existing = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.IsSystem
                        && a.AccountType == UncategorizedCategory.AccountType
                        && a.Name == UncategorizedCategory.Name)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return existing.Value;

        // A category has no currency of its own (ADR-0109 D9): a feed's postings carry
        // the paying account's, so the row takes the ledger's reporting currency rather
        // than reading as foreign on a ledger reported in another.
        var reporting = await DisplayCurrency.ForLedgerAsync(_db, ledgerId, cancellationToken)
            .ConfigureAwait(false);
        var inserted = new AccountRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Name = UncategorizedCategory.Name,
            AccountType = UncategorizedCategory.AccountType,
            CategoryKind = UncategorizedCategory.CategoryKind,
            CurrencyCode = reporting,
            OpeningBalance = 0m,
            IsActive = true,
            IsSystem = true,
        };
        _db.Accounts.Add(inserted);
        await using var write = await _db.BeginLedgerWriteAsync(ledgerId, cancellationToken)
            .ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await write.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted.Id;
    }

    /// <summary>
    /// Close <paramref name="run"/> as failed because another write to its ledger held the
    /// ledger's write lock past the wait, and drop what the run had tracked and not saved.
    /// </summary>
    /// <remarks>
    /// <para>Nothing the run fetched or parsed was written, so the next run sees the same
    /// rows as new; no watermark moved (it is written in the refused transaction). Closing
    /// the run frees its connection for the next sync rather than leaving a running row
    /// for the stale-run reaper.</para>
    /// <para>Only the entries waiting to be written are detached: the context may be a
    /// caller's that tracks rows of its own (the scheduler's), and the next connection of a
    /// sync-all or a scheduled pass saves on the same context, which must not carry this
    /// run's rows. The run row is closed by its key, on <see cref="CancellationToken.None"/>
    /// as every other close of a run is.</para>
    /// </remarks>
    private async Task CloseRunAsBusyAsync(LedgerOperationRow run)
    {
        await DropPendingAndCloseRunAsync(run, LedgerWriteLock.BusyDetail).ConfigureAwait(false);
        _logger.LogInformation(
            "Ingest run {RunId} on ledger {LedgerId} refused: another write held the ledger's write lock past the wait.",
            run.Id, run.LedgerId);
    }

    /// <summary>What a pull's run says when a save of what it fetched failed for any reason
    /// but the busy refusal: nothing was saved, and no watermark moved.</summary>
    internal const string SaveFailedDetail =
        "The sync could not save what it fetched, so nothing it fetched was saved; the next sync fetches it again.";

    /// <summary>What a pull's run says when it was cancelled before it finished.</summary>
    internal const string CancelledDetail = "The sync was cancelled before it finished.";

    /// <summary>What a file import's run says when its save of what it read failed for any
    /// reason but the busy refusal: nothing was saved.</summary>
    internal const string ImportSaveFailedDetail =
        "The import could not save what it read, so nothing it read was saved; import the file again.";

    /// <summary>What a file import's run says when it was cancelled before its save
    /// committed: nothing it read was saved.</summary>
    internal const string ImportCancelledDetail =
        "The import was cancelled before it finished, so nothing it read was saved; import the file again.";

    /// <summary>A pull's run's words for a save that threw <paramref name="failure"/>: a
    /// cancellation in the words the cancelled fetch uses.</summary>
    private static string PullSaveFailedDetail(Exception failure) =>
        failure is OperationCanceledException ? CancelledDetail : SaveFailedDetail;

    /// <summary>A file import's run's words for a failure after its run row was saved: a
    /// cancellation as cancelled, anything else as a save that failed.</summary>
    private static string ImportFailedDetail(Exception failure) =>
        failure is OperationCanceledException ? ImportCancelledDetail : ImportSaveFailedDetail;

    /// <summary>
    /// Close <paramref name="run"/> as failed with <paramref name="detail"/> because a save of
    /// what it fetched or read threw <paramref name="failure"/>, other than the busy refusal
    /// (on a file import, anything after its run row was saved, a cancellation among them),
    /// and drop what it had tracked and not saved, as <see cref="CloseRunAsBusyAsync"/> does.
    /// The caller rethrows.
    /// </summary>
    /// <remarks>
    /// <para>The save rolled back, so nothing the run fetched was written and no watermark
    /// moved. Left tracked, those rows would be written by the next save on the context,
    /// with no transaction of its own (on a pull, the scheduler's bookkeeping save, which the
    /// recompute they need then refuses, abandoning the tick); and the run row, left running,
    /// would show as running: a pull's until the stale-run sweep frees its connection, a file
    /// import's for good, since that sweep is by connection.</para>
    /// <para>A failure to close it is logged, never thrown, so the save's own failure is the
    /// one that reaches the caller.</para>
    /// </remarks>
    private async Task CloseRunAsFailedToSaveAsync(LedgerOperationRow run, Exception failure, string detail)
    {
        try
        {
            await DropPendingAndCloseRunAsync(run, detail).ConfigureAwait(false);
        }
        catch (Exception closeFailure)
        {
            _logger.LogError(closeFailure, "Ingest run {RunId} could not be closed after its save failed.", run.Id);
        }
        // A cancellation is a host stopping or a caller giving up, not a fault.
        if (failure is OperationCanceledException)
        {
            _logger.LogInformation(
                "Ingest run {RunId} on ledger {LedgerId} was cancelled; nothing it fetched or read was saved.",
                run.Id, run.LedgerId);
            return;
        }
        _logger.LogError(failure,
            "Ingest run {RunId} on ledger {LedgerId} failed before its save committed; nothing it fetched or read was saved.",
            run.Id, run.LedgerId);
    }

    /// <summary>Detach every entry the context holds to be written, the run row's among
    /// them, and close the run as failed with <paramref name="errorMessage"/> by its key.</summary>
    private async Task DropPendingAndCloseRunAsync(LedgerOperationRow run, string errorMessage)
    {
        foreach (var entry in _db.ChangeTracker.Entries()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
        _db.Entry(run).State = EntityState.Detached;

        var closedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.LedgerOperations
            .Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "failed")
                .SetProperty(r => r.ErrorMessage, errorMessage)
                .SetProperty(r => r.CompletedAt, closedAt),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one posting of a newly ingested row: the account imported into ↔
    /// Uncategorized (ADR-0019), with the feed's memo, when it has one, placed by the
    /// write primitive as that account's own. Both providers' paths add their rows
    /// here, so a feed memo lands the same way whichever wrote it.
    /// </summary>
    /// <remarks>
    /// <para>Both legs take <paramref name="bankAmount"/> fitted to the account's currency
    /// (<paramref name="currencyCode"/>), half away from zero (ADR-0114 D15). When that
    /// rounds, the row's originals are seeded with the bank's figure as sent and the
    /// rounded mark, as whole as a first edit's capture: the header's payee, dates and
    /// check number as written, and <paramref name="memo"/>, the feed's own. The seed calls
    /// <see cref="HeaderOriginals.CaptureAsync"/> directly, because
    /// <c>AnnotationWriter.CaptureOriginalsAsync</c> reads the legs from the database, which
    /// holds none of this row's before the save. A row the header already marks for review
    /// stays so, now marked as rounded. A figure that fits writes no originals.</para>
    /// </remarks>
    /// <param name="bankAmount">The amount the provider handed on, in the row's stored
    /// sign, before rounding.</param>
    private async Task AddIngestedLegsAsync(
        TxnHeaderRow header, Guid accountId, Guid uncategorizedAccountId, decimal bankAmount,
        string currencyCode, string? memo, CancellationToken cancellationToken)
    {
        var (amount, rounded) = IngestAmounts.Fit(bankAmount, currencyCode);
        var legs = new[]
        {
            new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = header.Id,
                LedgerId = header.LedgerId,
                AccountId = accountId,
                PostingIndex = 0,
                Amount = amount,
            },
            new TxnLegRow
            {
                Id = Guid.NewGuid(),
                HeaderId = header.Id,
                LedgerId = header.LedgerId,
                AccountId = uncategorizedAccountId,
                PostingIndex = 0,
                Amount = -amount,
            },
        };
        _db.TxnLegs.AddRange(legs);

        var shape = AnnotationWriter.Shape(null, null,
            legs.Select(l => new AnnotationWriter.PlannedLeg(l.PostingIndex, l.AccountId, l.Id)),
            AnnotationWriter.AccountFacts.None);
        var plan = AnnotationPlanner.Seed(shape, new AnnotationIntent<Guid>(
            accountId,
            memo is null ? FieldEdit<string>.Leave : FieldEdit<string>.Set(memo),
            FieldEdit<IReadOnlyList<Guid>>.Leave));
        if (!plan.IsValid)
            throw new InvalidOperationException($"An ingested row's notes were refused: {plan.Violations[0].Code}");
        await AnnotationWriter.ApplyAsync(_db, header, legs, plan, cancellationToken).ConfigureAwait(false);

        if (rounded)
        {
            await HeaderOriginals.CaptureAsync(
                    _db, header, memo, bankAmount, roundedAtImport: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The pre-split backfill's write, when it applies: the synced account's register
    /// shows no memo for the row (and, on a split, the transaction has none), and that
    /// account may write it. Null when either does not hold, which leaves the row as
    /// it is; a null plan when there is no description to write.
    /// </summary>
    private static (IReadOnlyList<TxnLegRow> Legs, AnnotationPlan<Guid>? Plan)? PreSplitMemoPlan(
        AnnotationWriter.Stored stored, Guid syncedAccountId, string? description)
    {
        if (stored.Shape.FirstLegOn(syncedAccountId) is not { } leg) return null;
        var shown = MemoTagRules.PresentInMemory(stored.Shape, stored.Notes, leg.Key);
        if (shown.Memo is not null || (stored.Shape.HeaderHeld && stored.Notes.Header.Memo is not null)) return null;
        if (description is null) return (stored.Legs, null);

        var plan = AnnotationPlanner.Plan(
            ShapeChange<Guid>.Unchanged(stored.Shape, stored.Notes),
            new AnnotationIntent<Guid>(syncedAccountId, FieldEdit<string>.Set(description), FieldEdit<IReadOnlyList<Guid>>.Leave));
        return plan.IsValid ? (stored.Legs, plan) : null;
    }

    /// <summary>
    /// Promote-on-clear: a pending row arrives cleared, and the bank may have changed the
    /// amount between the two (a tip, an exchange rate). Writes the cleared figure to the
    /// synced leg of a one-posting row, keeps a split as split, and restates the row's
    /// originals. Returns the synced account's amount before and after, for the
    /// promotion's audit.
    /// </summary>
    /// <remarks>
    /// <para><b>The legs</b> (ADR-0114 D13, "A feed keeps the far leg"). The synced leg is
    /// the one on the synced account, or, on a row since moved to another account, the one
    /// on its originating account, since a move relabels the figure. It takes
    /// <paramref name="bankAmount"/> fitted to its account's currency, half away from zero
    /// (ADR-0114 D15). Every other leg of the first posting that is a category, or a real account in
    /// the synced leg's currency, takes its negation (ADR-0019). A leg on a real account in
    /// another currency is a transfer's far leg, entered in that currency, and is kept as
    /// entered. That is a row of one posting, as a synced row lands.</para>
    /// <para><b>A split</b> (ADR-0114 D15, decided by the owner, 2026-11-17). A row of more
    /// than one posting is a split the user made, and no leg of it is rewritten: there is no
    /// one line to take the bank's figure, and the user chose every line. The account keeps
    /// the split's total, the sum of its legs on the synced (or originating) account, which
    /// need not include the first posting. When the cleared figure, fitted to the account's
    /// currency, is not that total, the row is held for review; a split merge loser holds
    /// its winner instead. The audit records the split's total as both figures, since
    /// nothing was written.</para>
    /// <para><b>The review flag</b> (ADR-0114 D15). A promotion that rounds sets
    /// <c>needs_review</c>, so the rounded figure is looked at; otherwise the flag is the
    /// user's and is left, a kept far leg included. Only a row still in a register is held:
    /// the pull's dedup also finds merge losers and soft-deleted rows, and neither is ever
    /// awaiting review (the merge writer clears the flag on a loser, and a soft delete
    /// clears it under ADR-0052 D3). Their legs and originals are still restated, so the
    /// bank's figure is on file if the row comes back. A merge loser whose synced leg
    /// changes holds its winner for review instead (<see cref="HoldMergeWinnerForReviewAsync"/>);
    /// one that clears at the figure it held leaves the winner alone. Each hold records why,
    /// for the Review marker: a cleared figure the row does not hold, a split's own or a
    /// loser's, is kept on the row (<see cref="TxnHeaderRow.HoldForClearedFigure"/>); a
    /// rounding keeps none (<see cref="TxnHeaderRow.HoldForRounding"/>), so a figure left
    /// from an earlier hold is not read as the reason.</para>
    /// <para><b>The originals</b> (ADR-0114 D15; <c>docs/database-schema.md</c>, "A
    /// promote-on-clear"). The cleared figure restates the feed's amount: on originals
    /// already on file it sets the amount to the bank's figure and the rounded mark to
    /// whether it rounded, leaving every other column; where there are none and the figure
    /// rounds, the row is seeded as an import seeds one, with the synced leg's memo, the
    /// feed's while the row is unedited; otherwise nothing is written. The audit records
    /// the amount written to the leg, not the bank's figure.</para>
    /// </remarks>
    /// <param name="originals">The row's originals, tracked, read with the batch's; null
    /// when it has none.</param>
    /// <param name="backfilledMemo">The memo the pre-split backfill writes on this row in
    /// the same sync, which reaches the leg only when the run's notes are applied; null
    /// when the backfill did not fire.</param>
    private async Task<(decimal Was, decimal Became)> PromoteClearedAsync(
        TxnHeaderRow header,
        Guid syncedAccountId,
        decimal bankAmount,
        TxnHeaderOriginalRow? originals,
        string? backfilledMemo,
        CancellationToken cancellationToken)
    {
        var legs = await _db.TxnLegs
            .Where(l => l.HeaderId == header.Id)
            .Join(_db.Accounts, l => l.AccountId, a => a.Id,
                (l, a) => new { Leg = l, a.AccountType, a.CurrencyCode })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // The legs on the account the figure is for: the synced account, or, on a row since
        // moved, its originating account. One on a one-posting row; one per posting that
        // touches the account on a split, which need not include the first.
        var side = legs.Where(l => l.Leg.AccountId == syncedAccountId).ToList();
        if (side.Count == 0) side = legs.Where(l => l.Leg.AccountId == header.OriginatingAccountId).ToList();

        decimal was, amount;
        bool rounded;
        string? sideMemo;
        var heldForFigure = false;
        if (side.Count == 0)
        {
            // No leg on either account, so nothing to compare or rewrite; the bank's figure
            // is still put on file.
            var currency = await _db.Accounts.Where(a => a.Id == syncedAccountId)
                .Select(a => a.CurrencyCode).SingleAsync(cancellationToken).ConfigureAwait(false);
            (amount, rounded) = IngestAmounts.Fit(bankAmount, currency);
            was = amount;
            sideMemo = null;
        }
        else if (legs.Select(l => l.Leg.PostingIndex).Distinct().Count() > 1)
        {
            // A split the user made (ADR-0114 D15, decided by the owner, 2026-11-17): every
            // line stays as split, and the account keeps the split's total. A cleared figure
            // the split does not add up to holds the row for review instead.
            var first = side.OrderBy(l => l.Leg.PostingIndex).First();
            was = side.Sum(l => l.Leg.Amount);
            amount = was;
            (var cleared, rounded) = IngestAmounts.Fit(bankAmount, first.CurrencyCode);
            if (cleared != was)
            {
                if (header.IsMergedInto is { } splitWinner)
                {
                    await HoldMergeWinnerForReviewAsync(header.LedgerId, splitWinner, bankAmount, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (!header.IsHidden)
                {
                    header.HoldForClearedFigure(bankAmount, byMatch: false);
                    heldForFigure = true;
                }
            }
            sideMemo = first.Leg.LegMemo;
        }
        else
        {
            var synced = side[0];
            was = synced.Leg.Amount;
            (amount, rounded) = IngestAmounts.Fit(bankAmount, synced.CurrencyCode);
            foreach (var leg in legs)
            {
                if (ReferenceEquals(leg, synced))
                    leg.Leg.Amount = amount;
                else if (leg.AccountType == "category"
                         || string.Equals(leg.CurrencyCode, synced.CurrencyCode, StringComparison.Ordinal))
                    leg.Leg.Amount = -amount;
            }
            if (amount != was && header.IsMergedInto is { } winnerId)
                await HoldMergeWinnerForReviewAsync(header.LedgerId, winnerId, bankAmount, cancellationToken)
                    .ConfigureAwait(false);
            sideMemo = synced.Leg.LegMemo;
        }

        // A rounding the split's own hold does not already explain.
        if (rounded && !heldForFigure && header.IsMergedInto is null && !header.IsHidden) header.HoldForRounding();

        if (originals is not null)
        {
            originals.Amount = bankAmount;
            originals.RoundedAtImport = rounded;
        }
        else if (rounded)
        {
            await HeaderOriginals.CaptureAsync(
                    _db, header, backfilledMemo ?? sideMemo ?? header.Memo, bankAmount,
                    roundedAtImport: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        return (was, amount);
    }

    /// <summary>
    /// A merge loser's promotion changed its amount: the row it was folded into goes back
    /// to review with its own amounts as they are (ADR-0114 D15).
    /// </summary>
    /// <remarks>
    /// The bank's figure lands only on the loser, which no register shows, so the row the
    /// user kept is put where the user looks, and the user decides what it should hold.
    /// The winner's amounts are never rewritten: a split has no one amount to take the
    /// bank's figure, and a figure the user chose when merging is theirs. A winner since
    /// folded into another row is itself a loser, so the chain is followed to the row a
    /// register shows; one deleted since (hidden) is resolved and is left (ADR-0052 D3).
    /// A merge never folds a row into a loser, so the chain has no cycle; the visited set
    /// only bounds the walk.
    /// </remarks>
    private async Task HoldMergeWinnerForReviewAsync(
        Guid ledgerId, Guid winnerId, decimal bankFigure, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid>();
        TxnHeaderRow? winner = null;
        for (Guid? next = winnerId; next is { } id && visited.Add(id);)
        {
            winner = await _db.TxnHeaders
                .FirstOrDefaultAsync(h => h.Id == id && h.LedgerId == ledgerId, cancellationToken)
                .ConfigureAwait(false);
            next = winner?.IsMergedInto;
        }
        if (winner is { IsMergedInto: null, IsHidden: false }) winner.HoldForClearedFigure(bankFigure, byMatch: true);
    }

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
