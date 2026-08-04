using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Db.Repositories;

/// <summary>
/// Read-only access to installation-wide metadata. Today: the DB
/// schema version (ADR-0044), read from DbUp's <c>__schema_migrations</c>
/// journal — the same source the snapshots repository stamps onto each
/// snapshot. Pure LINQ over the mapped <c>SchemaMigrationRow</c> entity;
/// no raw SQL.
/// </summary>
public sealed class MetaRepository
{
    private readonly AppDbContext _db;

    public MetaRepository(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The name of the latest applied migration (highest
    /// <c>schemaversionsid</c>), e.g.
    /// <c>118_recompute_holdings_split_first_buys_before_sells.sql</c>.
    /// Null only on a brand-new database with no migrations recorded —
    /// which never happens in practice, since the API can't start
    /// until DbUp has run.
    /// </summary>
    public async Task<string?> GetLatestSchemaScriptAsync(
        CancellationToken cancellationToken = default) =>
        await _db.SchemaMigrations.AsNoTracking()
            .OrderByDescending(m => m.SchemaVersionsId)
            .Select(m => m.ScriptName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
