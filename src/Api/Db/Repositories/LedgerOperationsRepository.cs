using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway over <c>ledger_operations</c> (ADR-0055/0086; <c>sync_runs</c> → <c>provider_runs</c>
/// → <c>ledger_operations</c>, migrations 038 → 132 → 185). Reads back the ledger-wide
/// activity timeline and one run's detail. Two-phase ops (feed syncs, quote
/// refreshes) are written by their orchestrators (a running row, then a terminal UPDATE);
/// this exposes <see cref="RecordTerminalAsync"/> for the one-shot ops (Moneydance import,
/// snapshot restore) that have no running phase to observe.
/// </summary>
public sealed class LedgerOperationsRepository
{
    private readonly AppDbContext _db;

    public LedgerOperationsRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Cap on <see cref="ListByLedgerAsync"/>'s page size; beyond that
    /// is "load more" territory.</summary>
    public const int MaxLimit = 200;

    /// <summary>Operation identity for the one-shot ops the observability sweep
    /// added (ADR-0086). Feed/quote provider keys are literals in the orchestrators.</summary>
    public const string MoneydanceImportFamily = "ingest";
    public const string MoneydanceImportProviderKey = "moneydance";
    public const string SnapshotRestoreFamily = "snapshot";
    public const string SnapshotRestoreProviderKey = "snapshot-restore";

    /// <summary>The Demo marker's identity (ADR-0115): the one row that records which of a
    /// Demo's examples were added, beside the Moneydance import row of the same family.</summary>
    public const string DemoMarkerFamily = "ingest";
    public const string DemoMarkerProviderKey = "demo";

    /// <summary>
    /// The Demo marker of each of <paramref name="ledgerIds"/> that has one, by ledger: its
    /// status and details. A ledger with several (which the run-once rule rules out) gives
    /// its latest. Read-only; RLS scopes the read to the caller's visible ledgers.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, DemoMarkerState>> DemoMarkersAsync(
        IReadOnlyCollection<Guid> ledgerIds, CancellationToken cancellationToken = default)
    {
        if (ledgerIds.Count == 0) return new Dictionary<Guid, DemoMarkerState>();

        var rows = await _db.LedgerOperations.AsNoTracking()
            .Where(r => ledgerIds.Contains(r.LedgerId)
                && r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey)
            .Select(r => new { r.LedgerId, r.Status, r.DetailsJson, r.StartedAt, r.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .GroupBy(r => r.LedgerId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).First();
                    return new DemoMarkerState(latest.Status, latest.DetailsJson);
                });
    }

    /// <summary>
    /// Every Demo marker of <paramref name="ledgerId"/>, oldest first: what the examples
    /// phase reads to make sure it runs once, on the marker its own import opened.
    /// </summary>
    public async Task<IReadOnlyList<DemoMarkerRow>> DemoMarkersOfAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.LedgerOperations.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId
                && r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey)
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.Id)
            .Select(r => new DemoMarkerRow(r.Id, r.LedgerId, r.Status, r.DetailsJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Every Demo marker still <c>running</c>, on any ledger: what the startup sweep
    /// closes, since no examples phase survives a restart.
    /// </summary>
    public async Task<IReadOnlyList<DemoMarkerRow>> RunningDemoMarkersAsync(
        CancellationToken cancellationToken = default) =>
        await _db.LedgerOperations.AsNoTracking()
            .Where(r => r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey
                && r.Status == "running")
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.Id)
            .Select(r => new DemoMarkerRow(r.Id, r.LedgerId, r.Status, r.DetailsJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Claim a Demo marker for the one examples phase that may run on it: the marker its
    /// import opened, still <c>running</c> with nothing recorded. One guarded write sets
    /// its first details and the time the phase started, so of two runs that both read
    /// the marker as unclaimed, only one claims it. False when it was not unclaimed.
    /// </summary>
    public async Task<bool> ClaimDemoMarkerAsync(
        Guid ledgerId, Guid markerId, string detailsJson, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailsJson);
        var updated = await _db.LedgerOperations
            .Where(r => r.Id == markerId
                && r.LedgerId == ledgerId
                && r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey
                && r.Status == "running"
                && r.DetailsJson == UnclaimedDemoMarkerDetails)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.DetailsJson, _ => detailsJson)
                .SetProperty(r => r.StartedAt, _ => startedAt), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>The details a Demo marker is opened with, before any examples phase has
    /// claimed it.</summary>
    public const string UnclaimedDemoMarkerDetails = "{}";

    /// <summary>
    /// Record a running Demo marker's details as the examples phase goes, so a process
    /// that dies mid-run leaves an accurate record. Writes only while the marker is still
    /// <c>running</c>; false when it is not (closed already, or not this ledger's).
    /// </summary>
    public async Task<bool> RecordDemoMarkerProgressAsync(
        Guid ledgerId, Guid markerId, string detailsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detailsJson);
        var updated = await _db.LedgerOperations
            .Where(r => r.Id == markerId
                && r.LedgerId == ledgerId
                && r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey
                && r.Status == "running")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DetailsJson, _ => detailsJson), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// Close a running Demo marker, once: its final status (<c>completed</c>,
    /// <c>partial</c> or <c>failed</c>), its details, the steps not added with their
    /// reasons, and when. False when the marker was no longer <c>running</c>, so a
    /// marker is never closed twice and a closed one is never reopened.
    /// </summary>
    public async Task<bool> CloseDemoMarkerAsync(
        Guid ledgerId,
        Guid markerId,
        string status,
        string detailsJson,
        string? errorMessage,
        DateTime completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailsJson);
        if (status == "running")
            throw new ArgumentException("A marker is closed with a terminal status.", nameof(status));

        var updated = await _db.LedgerOperations
            .Where(r => r.Id == markerId
                && r.LedgerId == ledgerId
                && r.Family == DemoMarkerFamily
                && r.ProviderKey == DemoMarkerProviderKey
                && r.Status == "running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, _ => status)
                .SetProperty(r => r.DetailsJson, _ => detailsJson)
                .SetProperty(r => r.ErrorMessage, _ => errorMessage)
                .SetProperty(r => r.CompletedAt, _ => (DateTime?)completedAt), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// Record a <em>one-shot terminal</em> ledger operation (ADR-0055/0086) — an
    /// operation with no separate running phase worth observing: a Moneydance
    /// bootstrap import or a snapshot restore. Inserts a single already-terminal
    /// row (<paramref name="status"/> = <c>completed</c> | <c>failed</c>). Feed
    /// syncs and quote refreshes keep their two-phase running→terminal write in the
    /// orchestrators, where the running row makes a mid-flight hang visible.
    /// <c>started_at</c> is DB-assigned (≈ completion for a one-shot); the Activity
    /// timeline shows "when", not a duration, so this is exact enough.
    /// </summary>
    public async Task RecordTerminalAsync(
        Guid ledgerId,
        string family,
        string providerKey,
        string triggeredVia,
        Guid? triggeredByUserId,
        string status,
        string? errorMessage,
        string detailsJson,
        DateTime completedAt,
        CancellationToken cancellationToken = default)
    {
        _db.LedgerOperations.Add(new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            Family = family,
            ProviderKey = providerKey,
            TriggeredVia = triggeredVia,
            TriggeredByUserId = triggeredByUserId,
            Status = status,
            ErrorMessage = errorMessage,
            DetailsJson = detailsJson,
            CompletedAt = completedAt,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <c>family</c> and <c>provider_key</c> of one operation on this ledger, or NULL
    /// if there is no such operation here.
    /// </summary>
    /// <remarks>
    /// Exists so the undo-import endpoint can tell "no such operation" from "that
    /// operation is a quote refresh, which created no transactions", or a Demo marker,
    /// which records examples rather than importing rows. Each would otherwise return
    /// zero rows and read as a successful undo of nothing.
    /// Deliberately not <see cref="GetDetailAsync"/>: that also loads errors and
    /// promotions to answer a two-column question.
    /// </remarks>
    public async Task<LedgerOperationKind?> KindForAsync(
        Guid ledgerId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        await _db.LedgerOperations
            .AsNoTracking()
            .Where(r => r.Id == operationId && r.LedgerId == ledgerId)
            .Select(r => new LedgerOperationKind(r.Family, r.ProviderKey))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Detail for one run of any family — summary + materialised child rows.
    /// Null when the run doesn't exist / isn't in the ledger / is RLS-hidden.</summary>
    public async Task<SyncRunDetail?> GetDetailAsync(
        Guid ledgerId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.LedgerOperations
            .AsNoTracking()
            .Where(r => r.Id == runId && r.LedgerId == ledgerId)
            .Select(r => new RunRead(
                r.Id,
                r.FeedConnectionId,
                r.Status,
                r.DetailsJson,
                r.ErrorMessage,
                r.StartedAt,
                r.CompletedAt,
                r.TriggeredByUserId,
                _db.LedgerOperationErrors.Count(e => e.LedgerOperationId == r.Id),
                _db.LedgerOperationPromotions.Count(p => p.LedgerOperationId == r.Id)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;

        var errorRows = await _db.LedgerOperationErrors
            .AsNoTracking()
            .Where(e => e.LedgerOperationId == runId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => new { e.Code, e.Message, e.SimpleFinConnectionId, e.SimpleFinAccountId, e.ProviderKey })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var accountLabels = await FeedAccountLabelsAsync(
                ledgerId,
                errorRows.Where(e => e.SimpleFinAccountId != null).Select(e => e.SimpleFinAccountId!).Distinct().ToList(),
                cancellationToken)
            .ConfigureAwait(false);
        var errors = errorRows
            .Select(e => new SyncErrorDto(
                e.Code, e.Message, e.SimpleFinConnectionId, e.SimpleFinAccountId,
                e.ProviderKey,
                e.SimpleFinAccountId is { } id ? accountLabels.GetValueOrDefault(id) : null))
            .ToList();

        // The currency both figures are in (ADR-0114 D15): the promotion row keeps no
        // account, so it is read through its header to the header's originating account,
        // the account the row lives on. That is the account it was synced into unless the
        // row was moved since; a move relabels, so the figures then read in the new
        // account's currency. A header with no originating account reads null.
        var promotions = await _db.LedgerOperationPromotions
            .AsNoTracking()
            .Where(p => p.LedgerOperationId == runId)
            .OrderBy(p => p.PromotedAt)
            .ThenBy(p => p.Id)
            .Select(p => new SyncRunPromotionDto(
                p.HeaderId, p.WasAmount, p.BecameAmount, p.PromotedAt,
                _db.TxnHeaders
                    .Where(h => h.Id == p.HeaderId)
                    .Join(_db.Accounts, h => h.OriginatingAccountId, a => (Guid?)a.Id, (h, a) => a.CurrencyCode)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SyncRunDetail(ToSummary(row), errors, promotions);
    }

    /// <summary>
    /// What to call each feed account an error names, by the feed's account id: the name
    /// of the Coffer account it is mapped to, else the feed's own name for it, with its
    /// institution, as the Bank feeds tab shows it ("name · institution"). An id neither
    /// knows (the bank-feed connection was deleted since) has no entry, and the error
    /// shows the id. Read when the run is opened, so a rename since the run shows.
    /// </summary>
    private async Task<Dictionary<string, string>> FeedAccountLabelsAsync(
        Guid ledgerId,
        IReadOnlyList<string> feedAccountIds,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (feedAccountIds.Count == 0) return labels;

        var mapped = await _db.Accounts
            .AsNoTracking()
            .Where(a => a.LedgerId == ledgerId
                        && a.FeedConnectionId != null
                        && a.ExternalId != null
                        && feedAccountIds.Contains(a.ExternalId))
            .OrderBy(a => a.Name)
            .Select(a => new { ExternalId = a.ExternalId!, a.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var a in mapped)
            labels.TryAdd(a.ExternalId, a.Name);

        var directory = await _db.FeedConnectionAccounts
            .AsNoTracking()
            .Where(f => f.LedgerId == ledgerId && feedAccountIds.Contains(f.ExternalId))
            .OrderBy(f => f.Name)
            .Select(f => new { f.ExternalId, f.Name, f.OrgName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var f in directory)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) continue;
            labels.TryAdd(f.ExternalId, string.IsNullOrWhiteSpace(f.OrgName) ? f.Name : $"{f.Name} · {f.OrgName}");
        }
        return labels;
    }

    /// <summary>
    /// Ledger-wide ledger-operation timeline (ADR-0055 slice C) — every family,
    /// optionally filtered to one <paramref name="providerKey"/> and/or runs
    /// started since <paramref name="sinceUtc"/>. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<LedgerOperationSummaryDto>> ListByLedgerAsync(
        Guid ledgerId,
        string? providerKey,
        DateTime? sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, MaxLimit);
        var q = _db.LedgerOperations.AsNoTracking().Where(r => r.LedgerId == ledgerId);
        if (!string.IsNullOrWhiteSpace(providerKey))
            q = q.Where(r => r.ProviderKey == providerKey);
        if (sinceUtc is { } since)
            q = q.Where(r => r.StartedAt >= since);

        var rows = await q
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Take(capped)
            .Select(r => new
            {
                r.Id,
                r.Family,
                r.ProviderKey,
                r.TriggeredVia,
                r.Status,
                r.StartedAt,
                r.CompletedAt,
                r.TriggeredByUserId,
                r.DetailsJson,
                r.FeedConnectionId,
                ErrorCount = _db.LedgerOperationErrors.Count(e => e.LedgerOperationId == r.Id),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new LedgerOperationSummaryDto(
            r.Id,
            r.Family,
            r.ProviderKey,
            r.TriggeredVia,
            r.Status,
            r.StartedAt,
            r.CompletedAt,
            r.TriggeredByUserId,
            ParseCounts(r.DetailsJson),
            r.ErrorCount,
            r.FeedConnectionId)).ToList();
    }

    /// <summary>Parse the details jsonb into a flat name→count map for the
    /// timeline. Counts are ints today; any non-int key is skipped.</summary>
    private static IReadOnlyDictionary<string, int> ParseCounts(string? json)
    {
        var dict = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
                    dict[p.Name] = n;
            }
        }
        catch (JsonException)
        {
            // Malformed details never breaks the activity list.
        }
        return dict;
    }

    /// <summary>Map a fetched row to the ingest-facing summary, parsing the
    /// ingest counters out of <c>details</c>.</summary>
    private static SyncRunSummary ToSummary(RunRead r)
    {
        var d = LedgerOperationDetails.Deserialize<IngestRunDetails>(r.DetailsJson);
        return new SyncRunSummary(
            r.Id,
            r.FeedConnectionId,
            r.Status,
            d.TxnsFetched,
            d.TxnsInserted,
            d.TxnsPromoted,
            d.TxnsAlreadyKnown,
            d.TxnsStillPending,
            r.ErrorMessage,
            r.StartedAt,
            r.CompletedAt,
            r.TriggeredByUserId,
            r.ErrorCount,
            r.PromotionCount);
    }

    private sealed record RunRead(
        Guid Id,
        Guid? FeedConnectionId,
        string Status,
        string DetailsJson,
        string? ErrorMessage,
        DateTime StartedAt,
        DateTime? CompletedAt,
        Guid? TriggeredByUserId,
        int ErrorCount,
        int PromotionCount);
}

/// <summary>A Demo marker's status and details, as <see cref="LedgerOperationsRepository.DemoMarkersAsync"/>
/// reads them.</summary>
public sealed record DemoMarkerState(string Status, string DetailsJson);

/// <summary>What kind of operation a <c>ledger_operations</c> row is.</summary>
public sealed record LedgerOperationKind(string Family, string ProviderKey);

/// <summary>One Demo marker, as the examples phase and the startup sweep read it.</summary>
public sealed record DemoMarkerRow(Guid Id, Guid LedgerId, string Status, string DetailsJson);
