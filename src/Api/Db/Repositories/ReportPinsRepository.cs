using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Pinned reports (ADR-0104 D2, migration 245) — a saved report or a canned
/// one, in one person's rail.
/// </summary>
/// <remarks>
/// Row-level security already restricts every row here to the acting user, so
/// these queries do not re-filter on user id for safety — they take it because
/// a pin's uniqueness is per-user and the write needs it.
/// </remarks>
internal sealed class ReportPinsRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ReportPinsRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// The acting user's rail for this ledger, in their order.
    /// </summary>
    /// <remarks>
    /// Saved pins are LEFT-joined to their report for the name and spec,
    /// because the rail renders labels and the report view takes its whole
    /// question from the URL — a link carrying only an id would open the
    /// default report under a pinned name. A saved pin whose report the user
    /// can no longer see is dropped: RLS hides the report, the join yields
    /// null, and listing it would put an entry in the rail that opens
    /// nothing. Catalog pins carry neither name nor spec; the client resolves
    /// both from its own catalog.
    /// </remarks>
    public async Task<List<PinnedReportDto>> ListAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from pin in _db.ReportPins.AsNoTracking()
            join report in _db.SavedReports.AsNoTracking()
                on pin.SavedReportId equals report.Id into joined
            from report in joined.DefaultIfEmpty()
            where pin.LedgerId == ledgerId
            orderby pin.SortOrder
            select new
            {
                pin.SavedReportId,
                pin.CatalogKey,
                pin.SortOrder,
                Name = report != null ? report.Name : null,
                Spec = report != null ? report.Spec : null,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            // A saved pin with no report is one the reader lost access to.
            .Where(r => r.CatalogKey != null || r.Name != null)
            .Select(r => new PinnedReportDto(
                r.SavedReportId,
                r.CatalogKey,
                r.Name,
                r.Spec is null ? null : JsonDocument.Parse(r.Spec).RootElement,
                r.SortOrder))
            .ToList();
    }

    /// <summary>
    /// Pin a saved report. Returns false when it is not one this user can see
    /// — a pin to an invisible report would be a rail entry that opens
    /// nothing, and the refusal doubles as "no, there is no such report".
    /// </summary>
    public async Task<bool> PinSavedAsync(
        Guid ledgerId,
        Guid savedReportId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var visible = await _db.SavedReports.AsNoTracking()
            .AnyAsync(r => r.Id == savedReportId && r.LedgerId == ledgerId, cancellationToken)
            .ConfigureAwait(false);
        if (!visible) return false;

        var already = await _db.ReportPins
            .AnyAsync(p => p.UserId == userId && p.SavedReportId == savedReportId, cancellationToken)
            .ConfigureAwait(false);
        if (already) return true;

        await AddAsync(ledgerId, userId, savedReportId, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Pin a canned report by key. Nothing validates the key against a
    /// catalog: the catalog lives in the SPA, and a server-side copy would be
    /// a second definition to keep in step. An unknown key renders nothing,
    /// which is the right failure for a retired preset.
    /// </summary>
    public async Task PinCatalogAsync(
        Guid ledgerId,
        string catalogKey,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var already = await _db.ReportPins
            .AnyAsync(p => p.UserId == userId
                           && p.LedgerId == ledgerId
                           && p.CatalogKey == catalogKey, cancellationToken)
            .ConfigureAwait(false);
        if (already) return;

        await AddAsync(ledgerId, userId, null, catalogKey, cancellationToken).ConfigureAwait(false);
    }

    public Task UnpinSavedAsync(
        Guid ledgerId,
        Guid savedReportId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _db.ReportPins
            .Where(p => p.UserId == userId
                        && p.LedgerId == ledgerId
                        && p.SavedReportId == savedReportId)
            .ExecuteDeleteAsync(cancellationToken);

    public Task UnpinCatalogAsync(
        Guid ledgerId,
        string catalogKey,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _db.ReportPins
            .Where(p => p.UserId == userId
                        && p.LedgerId == ledgerId
                        && p.CatalogKey == catalogKey)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Append, so pinning something does not reorder what is already there.
    /// </summary>
    private async Task AddAsync(
        Guid ledgerId,
        Guid userId,
        Guid? savedReportId,
        string? catalogKey,
        CancellationToken cancellationToken)
    {
        var next = await _db.ReportPins
            .Where(p => p.LedgerId == ledgerId)
            .MaxAsync(p => (int?)p.SortOrder, cancellationToken)
            .ConfigureAwait(false) ?? -1;

        _db.ReportPins.Add(new ReportPinRow
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            LedgerId = ledgerId,
            SavedReportId = savedReportId,
            CatalogKey = catalogKey,
            SortOrder = next + 1,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
