using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to the <c>accounts</c> table. The importer's two-pass
/// strategy (insert rows with <c>parent_id=NULL</c>, then update parents on
/// imported categories) is reflected by the two write methods below. Migration
/// 011 added <c>is_system</c> and <c>holdings_account_id</c>; the repository
/// surface includes a helper for creating per-brokerage Holdings siblings
/// and wiring the FK back to the brokerage.
/// </summary>
public sealed class AccountsRepository
{
    private readonly NpgsqlConnection _connection;

    public AccountsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Insert or update an account keyed by <see cref="AccountRow.ExternalId"/>.
    /// On conflict, data fields refresh but the original <c>id</c>,
    /// <c>created_at</c>, and <c>external_id</c> are preserved.
    /// </summary>
    public async Task<Guid> UpsertByExternalIdAsync(AccountRow row, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(row.ExternalId))
            throw new ArgumentException(
                "UpsertByExternalIdAsync requires a non-empty ExternalId on the row.", nameof(row));

        const string sql = """
            INSERT INTO accounts (id, ledger_id, parent_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, external_id,
                                  is_system, holdings_account_id,
                                  notes, account_number,
                                  institution_name, routing_number, account_url,
                                  provider_raw_payload, tax_status, opened_on)
            VALUES (@Id, @LedgerId, @ParentId, @Name, @AccountType, @CategoryKind,
                    @CurrencyCode, @OpeningBalance, @IsActive, @ExternalId,
                    @IsSystem, @HoldingsAccountId,
                    @Notes, @AccountNumber,
                    @InstitutionName, @RoutingNumber, @AccountUrl,
                    @ProviderRawPayload::jsonb, @TaxStatus, @OpenedOn)
            ON CONFLICT (ledger_id, external_id) WHERE external_id IS NOT NULL
            DO UPDATE SET
                parent_id           = EXCLUDED.parent_id,
                name                = EXCLUDED.name,
                account_type        = EXCLUDED.account_type,
                category_kind       = EXCLUDED.category_kind,
                currency_code       = EXCLUDED.currency_code,
                opening_balance     = EXCLUDED.opening_balance,
                is_active           = EXCLUDED.is_active,
                notes               = EXCLUDED.notes,
                account_number      = EXCLUDED.account_number,
                institution_name    = EXCLUDED.institution_name,
                routing_number      = EXCLUDED.routing_number,
                account_url         = EXCLUDED.account_url,
                -- Mig 110 / ADR-0035 §3: refresh provider payload on
                -- conflict — latest MD export is authoritative for the
                -- account's source metadata.
                provider_raw_payload = EXCLUDED.provider_raw_payload,
                -- ADR-0050 / mig 127: seed-once, unlike the columns above.
                -- MD owns the Start Date only until Coffer has one — the
                -- account editor owns the field afterwards, so COALESCE keeps
                -- a user's edit and still backfills accounts imported before
                -- this column was populated.
                opened_on            = COALESCE(accounts.opened_on, EXCLUDED.opened_on)
                -- is_system + holdings_account_id are set-once and stay
                -- pinned across re-runs. The AccountMapper (which feeds
                -- this upsert) always supplies HoldingsAccountId=null and
                -- IsSystem=false because it has no knowledge of the
                -- per-brokerage Holdings sibling — that wiring is
                -- established by EnsureHoldingsSiblingAsync in a later
                -- pass. Refreshing those columns from EXCLUDED on a
                -- re-run would wipe the link and force a fresh sibling
                -- to be minted, orphaning every prior holdings-side row
                -- (ADR-0019). ledger_id stays pinned for the same
                -- reason — re-imports must not reparent rows across
                -- ledgers (ADR-0020).
            RETURNING id;
            """;
        var command = new CommandDefinition(sql, row, cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<Guid>(command).ConfigureAwait(false);
    }

    /// <summary>
    /// Seed an account, keyed by the <c>account_external_ids</c> junction
    /// on <c>(ledger_id, source, external_id)</c>: if a junction row
    /// already exists, return that existing (Coffer-owned) account
    /// untouched; otherwise INSERT a fresh account plus its junction row.
    /// </summary>
    /// <remarks>
    /// Seed-once (ADR-0052 D2): the Moneydance importer only ever seeds an
    /// EMPTY ledger, so the junction lookup is a guard for the within-run
    /// case where the same account is referenced more than once (it never
    /// matches a prior import). The pre-0052 same-name ADOPTION step
    /// (cross-source linking of a SimpleFIN-synced account to its MD twin)
    /// was a re-import concern and has been removed — a seed-only import
    /// never encounters an account a different source already created.
    /// The account-creation path used by the pipeline; the junction was
    /// introduced by migration 064.
    /// </remarks>
    public async Task<(Guid Id, bool Inserted)> UpsertWithAdoptionAsync(
        AccountRow row,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(row.ExternalId))
            throw new ArgumentException(
                "UpsertWithAdoptionAsync requires a non-empty ExternalId on the row.", nameof(row));
        if (string.IsNullOrEmpty(source))
            throw new ArgumentException("source must be non-empty.", nameof(source));

        // Step 1: junction lookup for this exact (source, external_id).
        // Under seed-once (ADR-0052 D2) the ledger is empty at the start of
        // the import, so this only matches a row this same run already
        // inserted (the same account referenced more than once) — it never
        // matches a prior import.
        const string junctionLookupSql = """
            SELECT account_id FROM account_external_ids
            WHERE ledger_id = @LedgerId AND source = @Source AND external_id = @ExternalId
            LIMIT 1;
            """;
        var existingViaJunction = await _connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            junctionLookupSql,
            new { row.LedgerId, Source = source, row.ExternalId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (existingViaJunction is { } existingId)
        {
            // Already created in this run — return it untouched.
            return (existingId, false);
        }

        // Step 2: no existing match. Insert the new account + its junction row.
        const string insertSql = """
            INSERT INTO accounts (id, ledger_id, parent_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, external_id,
                                  is_system, holdings_account_id,
                                  notes, account_number,
                                  institution_name, routing_number, account_url,
                                  provider_raw_payload, opened_on)
            VALUES (@Id, @LedgerId, @ParentId, @Name, @AccountType, @CategoryKind,
                    @CurrencyCode, @OpeningBalance, @IsActive, @ExternalId,
                    @IsSystem, @HoldingsAccountId,
                    @Notes, @AccountNumber,
                    @InstitutionName, @RoutingNumber, @AccountUrl,
                    @ProviderRawPayload::jsonb, @OpenedOn)
            RETURNING id;
            """;
        var insertedId = await _connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            insertSql, row, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO account_external_ids (account_id, ledger_id, source, external_id)
            VALUES (@AccountId, @LedgerId, @Source, @ExternalId);
            """,
            new { AccountId = insertedId, row.LedgerId, Source = source, row.ExternalId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (insertedId, true);
    }

    /// <summary>
    /// Insert a system-managed account that has no <c>external_id</c> (so the
    /// idempotency strategy is "look up by id; if missing, insert"). Used by
    /// <see cref="EnsureHoldingsSiblingAsync"/> to create the per-brokerage
    /// Holdings account that hosts the holdings-side legs of investment
    /// transactions (ADR-0019).
    /// </summary>
    public async Task<Guid> InsertSystemAccountAsync(AccountRow row, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO accounts (id, ledger_id, parent_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, external_id,
                                  is_system, holdings_account_id,
                                  notes, account_number,
                                  institution_name, routing_number, account_url)
            VALUES (@Id, @LedgerId, @ParentId, @Name, @AccountType, @CategoryKind,
                    @CurrencyCode, @OpeningBalance, @IsActive, @ExternalId,
                    @IsSystem, @HoldingsAccountId,
                    @Notes, @AccountNumber,
                    @InstitutionName, @RoutingNumber, @AccountUrl)
            ON CONFLICT (id) DO NOTHING
            RETURNING id;
            """;
        var command = new CommandDefinition(sql, row, cancellationToken: cancellationToken);
        var inserted = await _connection.ExecuteScalarAsync<Guid?>(command).ConfigureAwait(false);
        return inserted ?? row.Id;
    }

    /// <summary>
    /// Ensure the brokerage account at <paramref name="brokerageId"/> has a
    /// system-managed Holdings sibling, creating it if necessary, and return
    /// the sibling's id. Idempotent: if the brokerage already has
    /// <c>holdings_account_id</c> set, returns that.
    /// </summary>
    public async Task<Guid> EnsureHoldingsSiblingAsync(
        Guid brokerageId,
        string brokerageName,
        string currencyCode,
        Guid ledgerId,
        CancellationToken cancellationToken = default)
    {
        // Fast path: already wired.
        var existing = await _connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT holdings_account_id FROM accounts WHERE id = @brokerageId;",
            new { brokerageId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is { } found) return found;

        var holdingsId = Guid.NewGuid();
        await InsertSystemAccountAsync(new AccountRow(
            Id: holdingsId,
            LedgerId: ledgerId,                              // sibling lives in the brokerage's ledger
            ParentId: null,                                  // categories-only constraint stays in force
            Name: $"{brokerageName} Holdings",
            AccountType: "investment",                       // same flavour as the cash side
            CategoryKind: null,
            CurrencyCode: currencyCode,
            OpeningBalance: 0m,
            IsActive: true,
            ExternalId: null,
            IsSystem: true,
            HoldingsAccountId: null,
            Notes: null,
            AccountNumber: null,
            InstitutionName: null,
            RoutingNumber: null,
            AccountUrl: null
        ), cancellationToken).ConfigureAwait(false);

        await _connection.ExecuteAsync(new CommandDefinition(
            "UPDATE accounts SET holdings_account_id = @holdingsId WHERE id = @brokerageId;",
            new { holdingsId, brokerageId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return holdingsId;
    }

    /// <summary>
    /// Set <c>parent_id</c> on a row identified by external id. Used by the
    /// importer's second pass to wire up category hierarchy after every row
    /// has been inserted.
    /// </summary>
    public async Task<int> UpdateParentByExternalIdAsync(Guid ledgerId, string externalId, Guid? parentId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE accounts SET parent_id = @parentId
             WHERE ledger_id = @ledgerId AND external_id = @externalId;
            """;
        var command = new CommandDefinition(sql, new { ledgerId, externalId, parentId }, cancellationToken: cancellationToken);
        return await _connection.ExecuteAsync(command).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM accounts;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public async Task<AccountRow?> GetByExternalIdAsync(Guid ledgerId, string externalId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id                   AS Id,
                   ledger_id            AS LedgerId,
                   parent_id            AS ParentId,
                   name                 AS Name,
                   account_type         AS AccountType,
                   category_kind        AS CategoryKind,
                   currency_code        AS CurrencyCode,
                   opening_balance      AS OpeningBalance,
                   is_active            AS IsActive,
                   external_id          AS ExternalId,
                   is_system            AS IsSystem,
                   holdings_account_id  AS HoldingsAccountId,
                   notes                AS Notes,
                   account_number       AS AccountNumber,
                   institution_name     AS InstitutionName,
                   routing_number       AS RoutingNumber,
                   account_url          AS AccountUrl,
                   provider_raw_payload::text AS ProviderRawPayload,
                   tax_status           AS TaxStatus,
                   opened_on            AS OpenedOn
            FROM accounts
            WHERE ledger_id = @ledgerId AND external_id = @externalId;
            """;
        var command = new CommandDefinition(sql, new { ledgerId, externalId }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<AccountRow>(command).ConfigureAwait(false);
    }
}
