using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Db;

/// <summary>
/// Dapper-backed gateway to <c>recurring_transactions</c>. The Moneydance
/// importer calls <see cref="UpsertByExternalIdAsync"/> once per
/// <c>reminder</c> item. Seed-once (ADR-0052 D2): the importer runs only
/// against an empty ledger, so each call is a plain INSERT.
/// </summary>
public sealed class RecurringTransactionsRepository
{
    private readonly NpgsqlConnection _connection;

    public RecurringTransactionsRepository(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Insert a recurring-transaction template keyed by
    /// <see cref="RecurringTransactionRow.ExternalId"/> and return its id.
    /// Seed-once (ADR-0052 D2): the ledger is empty when the importer runs, so
    /// this is a plain INSERT — there is no prior row to conflict with.
    /// </summary>
    public async Task<Guid> UpsertByExternalIdAsync(RecurringTransactionRow row, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(row.ExternalId))
            throw new ArgumentException(
                "UpsertByExternalIdAsync requires a non-empty ExternalId on the row.", nameof(row));

        const string sql = """
            INSERT INTO recurring_transactions (
                id, ledger_id, external_id,
                rrule, source_payload, auto_commit_days_before, template_header_id, source_account_id,
                start_date, end_date, next_due_date, last_acknowledged_date,
                is_loan_reminder, is_active, origin
            )
            VALUES (
                @Id, @LedgerId, @ExternalId,
                @Rrule, @SourcePayload::jsonb, @AutoCommitDaysBefore, @TemplateHeaderId, @SourceAccountId,
                @StartDate, @EndDate, @NextDueDate, @LastAcknowledgedDate,
                @IsLoanReminder, @IsActive, @Origin
            )
            RETURNING id;
            """;
        var command = new CommandDefinition(sql, row, cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<Guid>(command).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var command = new CommandDefinition("SELECT COUNT(*) FROM recurring_transactions;", cancellationToken: cancellationToken);
        return await _connection.ExecuteScalarAsync<int>(command).ConfigureAwait(false);
    }

    public async Task<RecurringTransactionRow?> GetByExternalIdAsync(string externalId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id                      AS Id,
                   ledger_id               AS LedgerId,
                   external_id             AS ExternalId,
                   rrule                   AS Rrule,
                   source_payload          AS SourcePayload,
                   auto_commit_days_before AS AutoCommitDaysBefore,
                   template_header_id      AS TemplateHeaderId,
                   source_account_id       AS SourceAccountId,
                   start_date              AS StartDate,
                   end_date                AS EndDate,
                   next_due_date           AS NextDueDate,
                   last_acknowledged_date  AS LastAcknowledgedDate,
                   is_loan_reminder        AS IsLoanReminder,
                   is_active               AS IsActive,
                   origin                  AS Origin
            FROM recurring_transactions
            WHERE external_id = @externalId;
            """;
        var command = new CommandDefinition(sql, new { externalId }, cancellationToken: cancellationToken);
        return await _connection.QuerySingleOrDefaultAsync<RecurringTransactionRow>(command).ConfigureAwait(false);
    }
}
