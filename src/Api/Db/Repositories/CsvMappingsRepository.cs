using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Db.Repositories;

/// <summary>What happened when a mapping document was saved.</summary>
/// <param name="Saved">The stored row, or null when nothing was written.</param>
/// <param name="Errors">
/// Everything wrong with the document. Non-empty means nothing was written — a mapping
/// is stored only if it validates, so a saved row is always a usable one.
/// </param>
/// <param name="NameTaken">
/// Another mapping on this ledger already uses the name. Distinguished from a validation
/// error because the document is fine and only the label collides.
/// </param>
public sealed record CsvMappingSaveResult(
    FeedCsvMappingRow? Saved,
    IReadOnlyList<CsvMappingError> Errors,
    bool NameTaken);

/// <summary>
/// Reads and writes <c>feed_csv_mappings</c> (mig 222).
/// </summary>
/// <remarks>
/// <para>
/// THE ONLY WRITE PATH, deliberately. Both the SPA's REST endpoints and the MCP tools go
/// through here, so validation cannot be reached around: a caller that skipped it would
/// store a document the importer then fails on per row, and the second caller is a
/// language model composing YAML, which is exactly the caller most likely to produce a
/// near-miss.
/// </para>
/// <para>
/// Validation happens ON WRITE, not on read. A stored document is therefore known-good
/// at the moment it was stored — which is what <c>schema_version</c> preserves when a
/// later validator adds a requirement.
/// </para>
/// <para>
/// RLS scopes every query to the caller's grants (mig 222 installs the read/write pair),
/// and the explicit <c>ledger_id</c> predicates here are belt and braces: they keep the
/// plan tight and survive a misconfigured session variable.
/// </para>
/// </remarks>
public sealed class CsvMappingsRepository
{
    private readonly AppDbContext _db;

    public CsvMappingsRepository(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<FeedCsvMappingRow>> ListAsync(
        Guid ledgerId, CancellationToken cancellationToken = default) =>
        await _db.FeedCsvMappings.AsNoTracking()
            .Where(m => m.LedgerId == ledgerId)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<FeedCsvMappingRow?> GetAsync(
        Guid ledgerId, Guid id, CancellationToken cancellationToken = default) =>
        await _db.FeedCsvMappings.AsNoTracking()
            .SingleOrDefaultAsync(m => m.LedgerId == ledgerId && m.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Validate a document and resolve it, without storing anything.
    /// </summary>
    /// <remarks>
    /// The path an author iterates on: a wizard trying a delimiter, or an MCP client
    /// composing YAML from a few sample lines. Nothing is written, so getting it wrong
    /// costs nothing and leaves no half-built rows behind.
    /// </remarks>
    public static CsvMapping? Validate(string yaml, out IReadOnlyList<CsvMappingError> errors) =>
        CsvMappingValidator.Validate(yaml, out errors);

    /// <summary>
    /// Create or replace a mapping. Stores nothing unless the document validates.
    /// </summary>
    /// <param name="id">
    /// The mapping to replace, or null to create one. A replace that names a mapping this
    /// ledger does not have returns no row rather than creating one under an id the
    /// caller chose.
    /// </param>
    public async Task<CsvMappingSaveResult> SaveAsync(
        Guid ledgerId,
        Guid? id,
        string name,
        string yaml,
        CancellationToken cancellationToken = default)
    {
        var mapping = CsvMappingValidator.Validate(yaml, out var errors);
        if (mapping is null) return new CsvMappingSaveResult(null, errors, NameTaken: false);

        var trimmed = name.Trim();
        // Checked here so the caller gets "that name is taken" rather than a unique-index
        // violation. The index is still the authority under a race.
        var clash = await _db.FeedCsvMappings
            .AnyAsync(m => m.LedgerId == ledgerId
                           && m.Id != (id ?? Guid.Empty)
                           && m.Name.ToLower() == trimmed.ToLower(), cancellationToken)
            .ConfigureAwait(false);
        if (clash) return new CsvMappingSaveResult(null, [], NameTaken: true);

        var now = DateTime.UtcNow;
        FeedCsvMappingRow row;
        if (id is { } existingId)
        {
            var existing = await _db.FeedCsvMappings
                .SingleOrDefaultAsync(m => m.LedgerId == ledgerId && m.Id == existingId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null) return new CsvMappingSaveResult(null, [], NameTaken: false);

            existing.Name = trimmed;
            existing.DefinitionYaml = yaml;
            existing.SchemaVersion = mapping.Version;
            existing.UpdatedAt = now;
            row = existing;
        }
        else
        {
            row = new FeedCsvMappingRow
            {
                Id = Guid.NewGuid(),
                LedgerId = ledgerId,
                Name = trimmed,
                DefinitionYaml = yaml,
                SchemaVersion = mapping.Version,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.FeedCsvMappings.Add(row);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CsvMappingSaveResult(row, [], NameTaken: false);
    }

    /// <summary>Remove a mapping. Returns false when this ledger has no such mapping.</summary>
    /// <remarks>
    /// Deleting a mapping does NOT touch anything imported with it. The rows it produced
    /// are ordinary transactions carrying their own import stamp (mig 221), and undoing
    /// an import is what removes those — a format description and the money read through
    /// it have separate lifetimes.
    /// </remarks>
    public async Task<bool> DeleteAsync(
        Guid ledgerId, Guid id, CancellationToken cancellationToken = default)
    {
        var removed = await _db.FeedCsvMappings
            .Where(m => m.LedgerId == ledgerId && m.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return removed > 0;
    }
}
