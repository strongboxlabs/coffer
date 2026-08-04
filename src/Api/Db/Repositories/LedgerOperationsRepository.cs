using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Gateway over <c>ledger_operations</c> (ADR-0055/0086; <c>sync_runs</c> → <c>provider_runs</c>
/// → <c>ledger_operations</c>, migrations 038 → 132 → 185). Reads back the per-connection
/// ingest log and the ledger-wide activity timeline. Two-phase ops (feed syncs, quote
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

    /// <summary>Cap on <see cref="ListByConnectionAsync"/>'s page size —
    /// ~a year of daily polling; beyond that is "load more" territory.</summary>
    public const int MaxLimit = 200;

    /// <summary>Operation identity for the one-shot ops the observability sweep
    /// added (ADR-0086). Feed/quote provider keys are literals in the orchestrators.</summary>
    public const string MoneydanceImportFamily = "ingest";
    public const string MoneydanceImportProviderKey = "moneydance";
    public const string SnapshotRestoreFamily = "snapshot";
    public const string SnapshotRestoreProviderKey = "snapshot-restore";

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

    /// <summary>Recent runs for one connection, newest first. RLS scopes to
    /// visible ledgers; the explicit <paramref name="ledgerId"/> filter is
    /// defence in depth.</summary>
    public async Task<IReadOnlyList<SyncRunSummary>> ListByConnectionAsync(
        Guid ledgerId,
        Guid connectionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var capped = Math.Clamp(limit, 1, MaxLimit);
        var rows = await _db.LedgerOperations
            .AsNoTracking()
            .Where(r => r.LedgerId == ledgerId
                        && r.FeedConnectionId == connectionId)
            .OrderByDescending(r => r.StartedAt)
            .Take(capped)
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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToSummary).ToList();
    }

    /// <summary>Detail for one run — summary + materialised child rows.
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

        var errors = await _db.LedgerOperationErrors
            .AsNoTracking()
            .Where(e => e.LedgerOperationId == runId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new SyncErrorDto(
                e.Code, e.Message, e.SimpleFinConnectionId, e.SimpleFinAccountId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var promotions = await _db.LedgerOperationPromotions
            .AsNoTracking()
            .Where(p => p.LedgerOperationId == runId)
            .OrderBy(p => p.PromotedAt)
            .Select(p => new SyncRunPromotionDto(
                p.HeaderId, p.WasAmount, p.BecameAmount, p.PromotedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SyncRunDetail(ToSummary(row), errors, promotions);
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
            r.ErrorCount)).ToList();
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
