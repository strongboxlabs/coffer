using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>ledgers</c>. The importer uses it to create the
/// ledger an import fills, before the rest of the pipeline runs.
/// </summary>
/// <remarks>
/// An import never looks a ledger up by name: ledger names are not unique, and the
/// importer connects as <c>coffer_service</c>, which bypasses row-level security, so a
/// lookup by name would find other users' ledgers too. It reads back only the ledger it
/// is filling, by id (<see cref="CurrencyOfAsync"/>).
/// </remarks>
public sealed class LedgersRepository
{
    /// <summary>The reporting currency a ledger is created in when none is named: the
    /// column's default (migration 280).</summary>
    public const string DefaultCurrencyCode = "USD";

    private readonly NpgsqlConnection _connection;

    public LedgersRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Insert a new ledger and return the persisted row. No grant is written;
    /// <see cref="CreateOwnedAsync"/> is what an import calls.
    /// </summary>
    /// <param name="currencyCode">The ledger's reporting currency (ledgers.currency_code,
    /// ADR-0114 D7), in stored form: three upper-case characters. The caller validates it;
    /// a value of another shape is refused here rather than written.</param>
    public async Task<LedgerRow> CreateAsync(
        string name, CancellationToken cancellationToken = default, string currencyCode = DefaultCurrencyCode)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Ledger name must not be empty.", nameof(name));
        if (currencyCode is not { Length: 3 } || currencyCode.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException($"'{currencyCode}' is not a currency code in stored form.", nameof(currencyCode));

        const string sql = """
            INSERT INTO ledgers (id, name, currency_code)
            VALUES (gen_random_uuid(), @name, @currencyCode)
            RETURNING id AS Id, name AS Name, created_at AS CreatedAt;
            """;
        var command = new CommandDefinition(sql, new { name, currencyCode }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleAsync<LedgerRow>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// The reporting currency of the ledger <paramref name="ledgerId"/>
    /// (<c>ledgers.currency_code</c>, ADR-0114 D7), which an imported category takes.
    /// Throws when there is no such ledger.
    /// </summary>
    public async Task<string> CurrencyOfAsync(Guid ledgerId, CancellationToken cancellationToken = default)
    {
        var code = await _connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT currency_code FROM ledgers WHERE id = @ledgerId;",
            new { ledgerId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return code ?? throw new InvalidOperationException($"Ledger {ledgerId} does not exist.");
    }

    /// <summary>
    /// Create a new ledger named <paramref name="name"/>, owned by
    /// <paramref name="ownerUserId"/>, whatever other ledgers carry that name.
    /// Imports pass the importing person's id (ADR-0071 D2); the reconcile
    /// diagnostic's scratch ledger, which never commits, passes
    /// <see cref="LedgerRow.SystemUserId"/>.
    /// </summary>
    public async Task<LedgerRow> CreateOwnedAsync(
        string name,
        Guid ownerUserId,
        CancellationToken cancellationToken = default,
        string currencyCode = DefaultCurrencyCode)
    {
        var created = await CreateAsync(name, cancellationToken, currencyCode).ConfigureAwait(false);
        const string grantSql = """
            INSERT INTO user_ledger_grants (user_id, ledger_id, role)
            VALUES (@ownerUserId, @ledgerId, 'owner');
            """;
        await _connection.ExecuteAsync(new CommandDefinition(
            grantSql, new { ownerUserId, ledgerId = created.Id }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return created;
    }
}
