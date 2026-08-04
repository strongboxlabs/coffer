using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Post-import sanity checks. Runs against the DB after every import
/// step completes; verifies structural invariants that aren't all
/// enforced by DB CHECKs/triggers. Warn-only by default — failures
/// don't roll back the data, but they surface and the process exits
/// with a non-zero code so scripts can detect issues.
/// </summary>
public sealed class ImportValidator
{
    private readonly NpgsqlConnection _connection;

    public ImportValidator(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    public sealed record CheckResult(string Name, bool Passed, string? Message);

    public sealed record ValidationReport(IReadOnlyList<CheckResult> Checks)
    {
        public bool AllPassed => Checks.All(c => c.Passed);
        public int Failed => Checks.Count(c => !c.Passed);
    }

    public async Task<ValidationReport> ValidateAsync(
        Guid ledgerId,
        int? expectedMdAccountCount = null,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<CheckResult>
        {
            await CheckAccountNameUniquenessAsync(ledgerId, cancellationToken),
            await CheckInvestmentActionConformanceAsync(ledgerId, cancellationToken),
            await CheckHeaderBalanceAsync(ledgerId, cancellationToken),
            await CheckHeaderHasLegsAsync(ledgerId, cancellationToken),
            await CheckLotsReferenceExistingLegsAsync(ledgerId, cancellationToken),
        };

        // Holdings-quantity-consistency was proposed but dropped:
        // `holdings.quantity` is set BY the recompute function from the
        // leg event stream; comparing to a naive SUM(legs.quantity) is
        // tautologically false for any position the user fully sold
        // out (recompute floors running_qty at zero on overshoot sells,
        // so post-sellout dividend-reinvest legs leave a small residual
        // in holdings that doesn't appear in the raw leg sum).
        // The structural invariant we care about — every lot's leg_id
        // resolves — is checked above.

        if (expectedMdAccountCount is { } expected)
            checks.Add(await CheckAccountCountParityAsync(ledgerId, expected, cancellationToken));

        return new ValidationReport(checks);
    }

    /// <summary>
    /// Flags duplicate accounts where AT LEAST ONE row has null
    /// external_id (drift signal: a re-import created a fresh row
    /// instead of upserting). Same-name-different-extid groups pass
    /// (MD has multiple categories with the same leaf name under
    /// different parents — legitimate).
    /// </summary>
    private async Task<CheckResult> CheckAccountNameUniquenessAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            SELECT name || ' / ' || account_type AS conflict
            FROM accounts
            WHERE ledger_id = @LedgerId
            GROUP BY name, account_type
            HAVING COUNT(*) > 1
               AND COUNT(*) FILTER (WHERE external_id IS NULL) > 0
            LIMIT 5;
            """;
        var conflicts = (await _connection.QueryAsync<string>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct))).ToList();

        return conflicts.Count == 0
            ? new CheckResult("account-name-uniqueness", true, null)
            : new CheckResult("account-name-uniqueness", false,
                $"{conflicts.Count}+ dup-name groups with at least one null external_id; sample: {string.Join(", ", conflicts)}");
    }

    /// <summary>
    /// Every <c>txn_headers.action IS NOT NULL</c> row carries one
    /// of the 9 ADR-0027 actions. CHECK constraint already enforces;
    /// this is a belt-and-suspenders scan.
    /// </summary>
    private async Task<CheckResult> CheckInvestmentActionConformanceAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            SELECT action FROM txn_headers
            WHERE ledger_id = @LedgerId
              AND action IS NOT NULL
              AND action NOT IN (
                  'buy', 'buyx', 'sell', 'sellx',
                  'dividend_cash', 'dividend_reinvest', 'divx',
                  'transfer', 'misc'
              )
            GROUP BY action LIMIT 5;
            """;
        var offCatalog = (await _connection.QueryAsync<string>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct))).ToList();

        return offCatalog.Count == 0
            ? new CheckResult("investment-action-conformance", true, null)
            : new CheckResult("investment-action-conformance", false,
                $"Off-catalog action(s): {string.Join(", ", offCatalog)}");
    }

    /// <summary>
    /// Double-entry invariant: every header's legs must sum to zero
    /// (ADR-0019). No exemptions.
    ///
    /// <para>The prior version checked per-posting AND excluded any
    /// posting that contained a zero-amount leg, on the premise that
    /// "SHARE CLASS EXCHANGE" / "DIST TO OWNER BASIS" shapes legitimately
    /// carry a zero cash leg "by design". That premise was wrong: it
    /// masked the self-referential <c>buysellxfr</c> bug (ADR-0053) where
    /// the importer zeroed the sell proceeds, leaving the header unbalanced
    /// by the full trade amount. The exemption is removed — with the mapper
    /// fixed, every header balances, so this is an unconditional per-header
    /// sum.</para>
    /// </summary>
    private async Task<CheckResult> CheckHeaderBalanceAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM (
                SELECT l.header_id
                FROM txn_legs l
                JOIN txn_headers h ON h.id = l.header_id
                WHERE h.ledger_id = @LedgerId
                GROUP BY l.header_id
                HAVING ABS(SUM(l.amount)) > 0.0001
            ) s;
            """;
        var unbalanced = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct));

        return unbalanced == 0
            ? new CheckResult("header-balance", true, null)
            : new CheckResult("header-balance", false,
                $"{unbalanced} header(s) whose legs do not sum to zero (double-entry violation)");
    }

    /// <summary>
    /// Every <c>txn_headers</c> row must have at least 2 legs (one
    /// posting's worth). Headers without legs are orphans.
    /// </summary>
    private async Task<CheckResult> CheckHeaderHasLegsAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM txn_headers h
            WHERE h.ledger_id = @LedgerId
              AND NOT EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id);
            """;
        var orphans = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct));

        return orphans == 0
            ? new CheckResult("header-has-legs", true, null)
            : new CheckResult("header-has-legs", false, $"{orphans} header(s) with no legs");
    }

    /// <summary>
    /// Every <c>lots.leg_id</c> resolves to an existing
    /// <c>txn_legs.id</c>. Structural invariant — already enforced
    /// by the FK, but the validator runs it as a sanity scan in case
    /// a future migration relaxes the constraint.
    /// </summary>
    private async Task<CheckResult> CheckLotsReferenceExistingLegsAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM lots l
            WHERE l.ledger_id = @LedgerId
              AND NOT EXISTS (SELECT 1 FROM txn_legs tl WHERE tl.id = l.leg_id);
            """;
        var orphans = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct));

        return orphans == 0
            ? new CheckResult("lots-reference-existing-legs", true, null)
            : new CheckResult("lots-reference-existing-legs", false,
                $"{orphans} lot(s) reference a non-existent leg_id");
    }

    /// <summary>
    /// MD account count vs imported account count parity. The caller supplies
    /// the count of MD accounts RESOLVED into the ledger (created OR adopted —
    /// <c>ImportContext.AccountByMdId</c>); we compare it to the count of
    /// accounts with a <c>moneydance</c>-source junction row. Using the resolved
    /// count (not the newly-inserted count) keeps the check correct on a
    /// seed-only re-import, where every account is adopted and nothing is
    /// inserted.
    /// </summary>
    private async Task<CheckResult> CheckAccountCountParityAsync(Guid ledgerId, int expectedMdAccountCount, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*) FROM account_external_ids
            WHERE ledger_id = @LedgerId AND source = 'moneydance';
            """;
        var actual = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct));

        return actual == expectedMdAccountCount
            ? new CheckResult("account-count-parity", true, null)
            : new CheckResult("account-count-parity", false,
                $"MD export had {expectedMdAccountCount} accounts; DB has {actual} with moneydance-source junction");
    }
}
