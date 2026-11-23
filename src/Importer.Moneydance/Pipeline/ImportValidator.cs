using Dapper;
using Npgsql;

namespace Coffer.Importer.Moneydance.Pipeline;

/// <summary>
/// Post-import sanity checks. Runs against the DB after every import
/// step completes; verifies structural invariants that aren't all
/// enforced by DB CHECKs/triggers. Warn-only — failures don't roll back
/// the data; the API reports each failed check, with its summary, on the
/// import job's status and logs its name.
/// </summary>
public sealed class ImportValidator
{
    private readonly NpgsqlConnection _connection;

    public ImportValidator(NpgsqlConnection connection)
    {
        _connection = connection;
    }

    /// <summary>One check's outcome. A failed check says what failed twice, for two
    /// readers: <paramref name="Message"/> in the importer's own terms, for whoever
    /// diagnoses it, and <paramref name="Summary"/> in plain words, for the person who
    /// ran the import (see <see cref="CheckSummaries"/>). A passed check carries neither.</summary>
    public sealed record CheckResult(string Name, bool Passed, string? Message, string? Summary)
    {
        public static CheckResult Pass(string name) => new(name, true, null, null);

        public static CheckResult Fail(string name, string message, string summary) =>
            new(name, false, message, summary);
    }

    /// <summary>The most offending values a check names; past it, the summary says "at least".</summary>
    private const int Sample = 5;

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
            await CheckOriginatingAccountAsync(ledgerId, cancellationToken),
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
    /// different parents — legitimate). System rows are left out: they
    /// never carry an external_id (the Holdings siblings, the
    /// Uncategorized category), so one sharing a name with a Moneydance
    /// account — the user's own category named "Uncategorized" — is not drift.
    /// </summary>
    private async Task<CheckResult> CheckAccountNameUniquenessAsync(Guid ledgerId, CancellationToken ct)
    {
        // One past the sample, so the summary knows whether there are more than it names.
        const string sql = """
            SELECT name AS Name, account_type AS Type
            FROM accounts
            WHERE ledger_id = @LedgerId
              AND NOT is_system
            GROUP BY name, account_type
            HAVING COUNT(*) > 1
               AND COUNT(*) FILTER (WHERE external_id IS NULL) > 0
            ORDER BY name, account_type
            LIMIT @Limit;
            """;
        var found = (await _connection.QueryAsync<(string Name, string Type)>(
            new CommandDefinition(sql, new { LedgerId = ledgerId, Limit = Sample + 1 }, cancellationToken: ct))).ToList();
        if (found.Count == 0) return CheckResult.Pass("account-name-uniqueness");

        var conflicts = found.Take(Sample).ToList();
        var more = found.Count > Sample;
        return CheckResult.Fail("account-name-uniqueness",
            $"{conflicts.Count}{(more ? "+" : "")} dup-name groups with at least one null external_id; sample: "
            + string.Join(", ", conflicts.Select(c => $"{c.Name} / {c.Type}")),
            CheckSummaries.DuplicateAccountNames(
                conflicts.Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList(), more));
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
            GROUP BY action ORDER BY action LIMIT @Limit;
            """;
        var found = (await _connection.QueryAsync<string>(
            new CommandDefinition(sql, new { LedgerId = ledgerId, Limit = Sample + 1 }, cancellationToken: ct))).ToList();
        if (found.Count == 0) return CheckResult.Pass("investment-action-conformance");

        var offCatalog = found.Take(Sample).ToList();
        var more = found.Count > Sample;
        return CheckResult.Fail("investment-action-conformance",
            $"Off-catalog action(s): {string.Join(", ", offCatalog)}{(more ? ", …" : "")}",
            CheckSummaries.UnusableActions(offCatalog, more));
    }

    /// <summary>
    /// Double-entry invariant (ADR-0019, ADR-0114 D13): a transfer between
    /// currencies passes when its two amounts are nonzero and opposite in
    /// sign, and every other leg of a header is summed and must come to zero.
    ///
    /// <para>A two-amount pair is a posting (<c>header_id</c>,
    /// <c>posting_index</c>) of exactly two legs, both on real accounts (not
    /// <c>account_type = 'category'</c>), whose <c>currency_code</c>s differ.
    /// It keeps each account's own figure (ADR-0114 D13), so it is left out of
    /// its header's sum. A posting with a category leg is never a pair, whatever
    /// currency the category carries, because a category leg mirrors the real
    /// side; a Holdings sibling shares its brokerage's currency, so its posting
    /// never is one either. Reminder templates are checked like every other
    /// header.</para>
    ///
    /// <para>No exemption for a posting that contains a zero-amount leg.
    /// Share-class-exchange / basis-distribution shapes do not need one, and
    /// an exemption would mask a mapper that zeroes sell proceeds — the
    /// self-referential <c>buysellxfr</c> shape (ADR-0053), which leaves the
    /// header unbalanced by the full trade amount. A pair with a zero side
    /// fails for the same reason. Every correctly mapped header balances, so
    /// the sum over the legs outside a pair is unconditional.</para>
    /// </summary>
    private async Task<CheckResult> CheckHeaderBalanceAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = """
            WITH postings AS (
                SELECT l.header_id,
                       COUNT(*) = 2
                         AND bool_and(a.account_type <> 'category')
                         AND MIN(a.currency_code) <> MAX(a.currency_code) AS is_pair,
                       MIN(l.amount) AS low,
                       MAX(l.amount) AS high,
                       SUM(l.amount) AS total
                FROM txn_legs l
                JOIN txn_headers h ON h.id = l.header_id
                JOIN accounts a ON a.id = l.account_id
                WHERE h.ledger_id = @LedgerId
                GROUP BY l.header_id, l.posting_index
            )
            SELECT COUNT(*) FROM (
                SELECT header_id
                FROM postings
                GROUP BY header_id
                HAVING ABS(COALESCE(SUM(total) FILTER (WHERE NOT is_pair), 0)) > 0.0001
                    OR bool_or(is_pair AND NOT (low < 0 AND high > 0))
            ) s;
            """;
        var unbalanced = await _connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct));

        return unbalanced == 0
            ? CheckResult.Pass("header-balance")
            : CheckResult.Fail("header-balance",
                $"{unbalanced} header(s) whose legs do not sum to zero outside a transfer between currencies, "
                + "or whose transfer between currencies lacks two nonzero amounts of opposite sign "
                + "(double-entry violation)",
                CheckSummaries.Unbalanced(unbalanced));
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
            ? CheckResult.Pass("header-has-legs")
            : CheckResult.Fail("header-has-legs", $"{orphans} header(s) with no legs",
                CheckSummaries.WithoutLines(orphans));
    }

    /// <summary>
    /// The originating-account invariants of migration 263 (see
    /// <see cref="OriginatingAccountInvariants"/>), over what the import wrote. The
    /// home account satisfies all of them by construction, which is what this proves.
    /// The message counts the headers and tallies the clauses they break.
    /// </summary>
    private async Task<CheckResult> CheckOriginatingAccountAsync(Guid ledgerId, CancellationToken ct)
    {
        const string sql = $"""
            SELECT v.clause, count(*)::integer AS headers
              FROM ({OriginatingAccountInvariants.ViolationsSql}) v
             GROUP BY v.clause
             ORDER BY v.clause;
            """;
        var broken = (await _connection.QueryAsync<(string Clause, int Headers)>(
            new CommandDefinition(sql, new { LedgerId = ledgerId }, cancellationToken: ct))).ToList();
        if (broken.Count == 0) return CheckResult.Pass("originating-account");

        var headers = await _connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT count(DISTINCT v.header_id)::integer FROM ({OriginatingAccountInvariants.ViolationsSql}) v",
            new { LedgerId = ledgerId }, cancellationToken: ct));
        return CheckResult.Fail("originating-account",
            $"{headers} header(s) whose originating account breaks an invariant (migration 263): "
            + string.Join(", ", broken.Select(b => $"{b.Clause} {b.Headers}")),
            CheckSummaries.WithoutOwner(headers));
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
            ? CheckResult.Pass("lots-reference-existing-legs")
            : CheckResult.Fail("lots-reference-existing-legs",
                $"{orphans} lot(s) reference a non-existent leg_id",
                CheckSummaries.LotsWithoutLine(orphans));
    }

    /// <summary>
    /// MD account count vs imported account count parity. The caller supplies
    /// the count of MD accounts RESOLVED into the ledger (created OR adopted —
    /// <c>ImportContext.AccountByMdId</c>) less those resolved onto the system
    /// Uncategorized (<c>ImportContext.MdIdsOnUncategorized</c>), which share a
    /// Coffer-native row and have no junction of their own; we compare it to
    /// the count of accounts with a <c>moneydance</c>-source junction row. Using
    /// the resolved count (not the newly-inserted count) keeps the check correct
    /// on a seed-only re-import, where every account is adopted and nothing is
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
            ? CheckResult.Pass("account-count-parity")
            : CheckResult.Fail("account-count-parity",
                $"MD export had {expectedMdAccountCount} accounts; DB has {actual} with moneydance-source junction",
                CheckSummaries.AccountCount(expectedMdAccountCount, actual));
    }
}
