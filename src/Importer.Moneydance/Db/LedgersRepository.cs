using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>ledgers</c>. The importer uses this to
/// resolve / create the target ledger before the rest of the pipeline
/// runs; future API code uses it for ledger-management endpoints.
/// </summary>
public sealed class LedgersRepository
{
    private readonly NpgsqlConnection _connection;

    public LedgersRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Look up a ledger by its UUID. Returns null when no ledger exists
    /// with that id.
    /// </summary>
    public async Task<LedgerRow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id, name AS Name, created_at AS CreatedAt
              FROM ledgers WHERE id = @id;
            """;
        var command = new CommandDefinition(sql, new { id }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<LedgerRow>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Look up a ledger by its display name. Returns null when no ledger
    /// matches; multiple matches throw (the schema doesn't enforce name
    /// uniqueness today, but the importer's name-based resolver expects
    /// it for lookup safety).
    /// </summary>
    public async Task<LedgerRow?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ledger name must not be empty.", nameof(name));

        const string sql = """
            SELECT id AS Id, name AS Name, created_at AS CreatedAt
              FROM ledgers WHERE name = @name;
            """;
        var command = new CommandDefinition(sql, new { name }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<LedgerRow>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Create a new ledger and return the persisted row. The default
    /// ledger seeded by migration 014 covers single-tenant use; this is
    /// for users who want a second book ("personal" + "spouse",
    /// "household" + "rental LLC", etc.).
    /// </summary>
    public async Task<LedgerRow> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ledger name must not be empty.", nameof(name));

        const string sql = """
            INSERT INTO ledgers (id, name)
            VALUES (gen_random_uuid(), @name)
            RETURNING id AS Id, name AS Name, created_at AS CreatedAt;
            """;
        var command = new CommandDefinition(sql, new { name }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleAsync<LedgerRow>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the ledger to import into. Precedence:
    /// <paramref name="explicitId"/> wins, then <paramref name="explicitName"/>
    /// (existing or freshly created), otherwise the default ledger from
    /// migration 014. <paramref name="ownerUserId"/> is granted ownership of any
    /// newly-created ledger — the CLI passes <see cref="LedgerRow.SystemUserId"/>;
    /// UI imports pass the importing human's id (ADR-0071 D2).
    /// </summary>
    public async Task<LedgerRow> ResolveOrCreateAsync(
        Guid? explicitId,
        string? explicitName,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        if (explicitId is { } id)
        {
            var existing = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"--ledger-id {id} does not exist. Pass --ledger-name to create one.");
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            var existing = await GetByNameAsync(explicitName, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return existing;

            var created = await CreateAsync(explicitName, cancellationToken).ConfigureAwait(false);
            const string grantSql = """
                INSERT INTO user_ledger_grants (user_id, ledger_id, role)
                VALUES (@ownerUserId, @ledgerId, 'owner')
                ON CONFLICT DO NOTHING;
                """;
            await _connection.ExecuteAsync(new CommandDefinition(
                grantSql, new { ownerUserId, ledgerId = created.Id }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return created;
        }

        // No implicit target (ADR-0088). There used to be a fallback to the seeded
        // …0001 "Default" ledger here; that row no longer exists, and guessing a
        // destination for a bulk financial import is the wrong default anyway —
        // a typo'd flag would silently write into the wrong book.
        throw new InvalidOperationException(
            "No target ledger specified. Pass --ledger-name <name> to create one " +
            "(or reuse it if it already exists), or --ledger-id <uuid> to import " +
            "into an existing ledger.");
    }

    /// <summary>
    /// Read-only resolver for the validate command. Same precedence as
    /// <see cref="ResolveOrCreateAsync"/> but never creates: an unknown
    /// <paramref name="explicitName"/> raises so the user notices the typo
    /// instead of silently validating against an empty ledger.
    /// </summary>
    public async Task<LedgerRow> ResolveForValidationAsync(
        Guid? explicitId,
        string? explicitName,
        CancellationToken cancellationToken = default)
    {
        if (explicitId is { } id)
        {
            return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"--ledger-id {id} does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return await GetByNameAsync(explicitName, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"--ledger-name '{explicitName}' does not exist. Run import first to create it.");
        }

        // Same as ResolveOrCreateAsync: no implicit default (ADR-0088).
        throw new InvalidOperationException(
            "No ledger specified. Pass --ledger-name <name> or --ledger-id <uuid>.");
    }
}
