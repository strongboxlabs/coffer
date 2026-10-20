using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Memorized reports (ADR-0104 D3, migration 244).
/// </summary>
/// <remarks>
/// <para>Row-level security does the visibility work, not this class. The
/// policy on <c>saved_reports</c> already restricts reads to rows you own or
/// the ledger shares, and writes to rows you own, so a query here that
/// "forgot" an owner check still cannot return or modify someone else's
/// private report. The explicit owner check in <see cref="UpdateAsync"/> and
/// <see cref="DeleteAsync"/> is there to turn a silent no-op into a 404, not
/// to provide the guarantee.</para>
/// </remarks>
internal sealed class SavedReportsRepository
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public SavedReportsRepository(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Every report the acting user can see in this ledger.</summary>
    public async Task<IReadOnlyList<SavedReportDto>> ListAsync(
        Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await _db.SavedReports.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false)
            is var rows ? rows.Select(ToDto).ToList() : [];

    public async Task<SavedReportDto?> GetAsync(
        Guid ledgerId,
        Guid reportId,
        CancellationToken cancellationToken = default) =>
        await _db.SavedReports.AsNoTracking()
            .Where(r => r.LedgerId == ledgerId && r.Id == reportId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } row
            ? ToDto(row) : null;

    public async Task<SavedReportDto> CreateAsync(
        Guid ledgerId,
        Guid ownerUserId,
        string name,
        string specJson,
        string visibility,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = new SavedReportRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledgerId,
            OwnerUserId = ownerUserId,
            Name = name,
            Spec = specJson,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.SavedReports.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    /// <summary>
    /// Patch the fields that were supplied. Returns null when there is no such
    /// report the acting user may write — which, because of the policy's
    /// WITH CHECK, includes a ledger-shared report belonging to someone else.
    /// </summary>
    public async Task<SavedReportDto?> UpdateAsync(
        Guid ledgerId,
        Guid reportId,
        Guid actingUserId,
        string? name,
        string? specJson,
        string? visibility,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.SavedReports
            .FirstOrDefaultAsync(
                r => r.LedgerId == ledgerId && r.Id == reportId && r.OwnerUserId == actingUserId,
                cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        if (name is not null) row.Name = name;
        if (specJson is not null) row.Spec = specJson;
        if (visibility is not null) row.Visibility = visibility;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(row);
    }

    /// <summary>
    /// The stored spec is parsed on the way out. A row whose spec the server
    /// cannot read is a 500 its owner can delete — the database CHECK only
    /// guarantees the column holds a JSON object, not that the object is a
    /// ReportSpec, and that is the deliberate cost of storing the spec whole
    /// (migration 244).
    /// </summary>
    private static SavedReportDto ToDto(SavedReportRow r) => new(
        r.Id, r.Name, JsonDocument.Parse(r.Spec).RootElement,
        r.Visibility, r.OwnerUserId, r.CreatedAt, r.UpdatedAt);

    public async Task<bool> DeleteAsync(
        Guid ledgerId,
        Guid reportId,
        Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        var removed = await _db.SavedReports
            .Where(r => r.LedgerId == ledgerId && r.Id == reportId && r.OwnerUserId == actingUserId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return removed > 0;
    }
}
