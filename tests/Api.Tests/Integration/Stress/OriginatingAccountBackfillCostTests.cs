using System.Diagnostics;
using System.Globalization;

using Npgsql;
using Xunit.Abstractions;

using Coffer.Api.Tests.Integration.Infra;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Migration 263's backfill and end assertion, timed on a large ledger staged just
/// before it: the upgrade every existing install runs once, over all of its headers.
/// </summary>
/// <remarks>
/// <para>The ledger is generated set-based on the pre-263 schema, so the upgrade sees
/// rows that predate the column. It carries every shape the backfill decides
/// differently: one-splits, transfers, multi-splits (some with every line to one
/// category, some with no account on every line), each kind of investment event
/// including both forms of in-kind transfer, reminder templates and their
/// occurrences, Moneydance rows with a wide payload and a home account, and headers
/// without legs. Statistics are gathered first, as a live database would have
/// them.</para>
/// <para>The migration runs the way an upgrade runs it, through the migration runner,
/// and must leave every header keeping the invariants the import check enforces.
/// The figures are the output; <see cref="HeadersVariable"/> changes the size, so two
/// sizes show how the cost grows.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OriginatingAccountBackfillCostTests : DatabaseTest
{
    /// <summary>Overrides how many headers the staged ledger holds.</summary>
    public const string HeadersVariable = "COFFER_MIG263_COST_HEADERS";

    private const int DefaultHeaders = 100_000;

    private const string Mig263 = "263_originating_account_tag_name_key_and_saved_report_repoint.sql";

    private static readonly Guid Ledger = new("26300000-0000-4000-8000-00000000c057");

    private readonly ITestOutputHelper _output;

    public OriginatingAccountBackfillCostTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
        _output = output;
    }

    [Fact]
    public async Task Migration_263_backfills_a_large_ledger_and_prints_its_cost()
    {
        var headers = Environment.GetEnvironmentVariable(HeadersVariable) is { Length: > 0 } raw
            ? int.Parse(raw, CultureInfo.InvariantCulture)
            : DefaultHeaders;

        var work = Directory.CreateTempSubdirectory("coffer-mig263-cost-").FullName;
        var cs = Db.EmptyDatabaseConnectionString($"mig263_cost_{headers}");
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig263, work));

        var seed = Stopwatch.StartNew();
        await ExecAsync(cs, GeneratorSql(headers));
        seed.Stop();
        var legs = (long)(await ScalarAsync(cs, "SELECT count(*) FROM txn_legs"))!;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seed:     {seed.Elapsed.TotalSeconds,7:F1}s  ({headers:N0} headers, {legs:N0} legs)"));

        var upgrade = Stopwatch.StartNew();
        UpgradeRehearsal.Apply(cs, work, Mig263);
        upgrade.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mig 263:  {upgrade.Elapsed.TotalSeconds,7:F1}s"));

        var check = Stopwatch.StartNew();
        var violations = (int)(await ScalarAsync(cs,
            $"SELECT count(*)::int FROM ({OriginatingAccountInvariants.ViolationsSql}) v", Ledger))!;
        check.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"check:    {check.Elapsed.TotalSeconds,7:F1}s"));
        Assert.Equal(0, violations);

        // The source-brokerage definition alone, over every in-kind transfer of the ledger.
        var sources = Stopwatch.StartNew();
        var sourced = (long)(await ScalarAsync(cs,
            "SELECT count(*) FROM public.transfer_shares_source_brokerages(@LedgerId)", Ledger))!;
        sources.Stop();
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"sources:  {sources.Elapsed.TotalSeconds,7:F1}s  ({sourced:N0} in-kind transfers)"));

        var unfilled = (long)(await ScalarAsync(cs, """
            SELECT count(*) FROM txn_headers h
             WHERE h.originating_account_id IS NULL
               AND EXISTS (SELECT 1 FROM txn_legs l WHERE l.header_id = h.id)
            """))!;
        Assert.Equal(0, unfilled);
    }

    /// <summary>
    /// The staged ledger, set-based. Header <c>i</c>'s shape is <c>i % 20</c>; every id is
    /// derived from its row number, so a size seeds the same ledger every run.
    /// </summary>
    private static string GeneratorSql(int headers) => $$"""
        BEGIN;

        INSERT INTO ledgers (id, name) VALUES ('{{Ledger}}'::uuid,'Ledger A');

        INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, is_system,
                              provider_raw_payload)
        SELECT md5('bank' || n)::uuid, '{{Ledger}}'::uuid,'Account ' || n, 'bank', NULL, 'USD', FALSE,
               CASE WHEN n <= 3 THEN jsonb_build_object('id', 'md-bank-' || n) END
          FROM generate_series(1, 5) n
        UNION ALL
        SELECT md5('cat' || n)::uuid, '{{Ledger}}'::uuid,'Category ' || n, 'category', 'expense', 'USD', FALSE,
               CASE WHEN n <= 5 THEN jsonb_build_object('id', 'md-cat-' || n) END
          FROM generate_series(1, 20) n
        UNION ALL
        SELECT md5('income')::uuid, '{{Ledger}}'::uuid,'Category Income', 'category', 'income', 'USD', FALSE, NULL
        UNION ALL
        SELECT md5('fee')::uuid, '{{Ledger}}'::uuid,'Category Fee', 'category', 'expense', 'USD', FALSE, NULL
        UNION ALL
        SELECT md5('hold' || n)::uuid, '{{Ledger}}'::uuid,'Holdings ' || n, 'investment', NULL, 'USD', TRUE, NULL
          FROM generate_series(1, 2) n;

        INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id,
                              provider_raw_payload)
        SELECT md5('brok' || n)::uuid, '{{Ledger}}'::uuid,'Brokerage ' || n, 'investment', 'USD',
               md5('hold' || n)::uuid,
               CASE WHEN n = 1 THEN jsonb_build_object('id', 'md-brok-1') END
          FROM generate_series(1, 2) n;

        INSERT INTO securities (id, ledger_id, name)
        SELECT md5('sec' || n)::uuid, '{{Ledger}}'::uuid,'Security ' || n FROM generate_series(1, 50) n;

        CREATE TEMP TABLE gen ON COMMIT DROP AS
        SELECT i, i % 20 AS kind,
               md5('h' || i)::uuid AS id,
               md5('bank' || (1 + i % 5))::uuid AS bank,
               md5('bank' || (1 + (i + 1) % 5))::uuid AS bank2,
               md5('cat' || (1 + i % 20))::uuid AS cat,
               md5('cat' || (1 + (i + 3) % 20))::uuid AS cat2,
               md5('sec' || (1 + i % 50))::uuid AS sec,
               -- A Moneydance row: a third of the bank and investment rows, homed on a
               -- bank that has an id, or on one no account carries.
               i % 3 = 0 AND i % 20 NOT IN (9, 10, 11) AS md,
               CASE WHEN (1 + i % 5) <= 3 THEN 'md-bank-' || (1 + i % 5) ELSE 'md-gone' END AS md_home,
               -- Lines of a multi-split, and whether every line goes to one category.
               2 + (i / 20) % 4 AS lines,
               (i / 20) % 7 = 0 AS symmetric
          FROM generate_series(1, {{headers}}) i;

        -- Headers, except the occurrences, which need their series first.
        INSERT INTO txn_headers (id, ledger_id, origin, external_id, import_source, provider_raw_payload,
                                 action, payee, posted_at, transacted_at, created_at, is_recurring_template)
        SELECT g.id, '{{Ledger}}'::uuid,'manual',
               CASE WHEN g.md THEN 'md-' || g.i
                    WHEN g.kind = 9 AND g.i % 40 = 9 THEN 'mdreminder:r' || g.i END,
               CASE WHEN g.md OR (g.kind = 9 AND g.i % 40 = 9) THEN 'moneydance_export' END,
               CASE WHEN g.md THEN jsonb_build_object(
                   'acctid', g.md_home,
                   'raw', (SELECT string_agg(md5((g.i * 100 + n)::text), '') FROM generate_series(1, 64) n)) END,
               CASE g.kind WHEN 3 THEN 'buy' WHEN 4 THEN 'buyx' WHEN 5 THEN 'dividend_cash'
                           WHEN 6 THEN 'transfer' WHEN 7 THEN 'transfer_shares' WHEN 8 THEN 'transfer_shares' END,
               'Payee ' || (g.i % 500),
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               g.kind = 9
          FROM gen g
         WHERE g.kind <> 10;

        -- One series per template: half name no source, a Moneydance one names its home.
        INSERT INTO recurring_transactions (id, ledger_id, start_date, rrule, next_due_date, template_header_id,
                                            source_account_id, origin, external_id, source_payload)
        SELECT md5('rt' || g.i)::uuid, '{{Ledger}}'::uuid,DATE '2020-01-01', 'FREQ=MONTHLY;BYMONTHDAY=1',
               DATE '2020-01-01', g.id,
               CASE WHEN g.i % 60 = 49 THEN g.bank END,
               CASE WHEN g.i % 40 = 9 THEN 'moneydance_import' ELSE 'manual' END,
               CASE WHEN g.i % 40 = 9 THEN 'r' || g.i END,
               CASE WHEN g.i % 40 = 9 THEN jsonb_build_object('txn.acctid', g.md_home) END
          FROM gen g
         WHERE g.kind = 9;

        -- An occurrence of the series of the template just before it.
        INSERT INTO txn_headers (id, ledger_id, origin, payee, posted_at, transacted_at, created_at,
                                 recurring_transaction_id, occurrence_date)
        SELECT g.id, '{{Ledger}}'::uuid,'manual', 'Payee ' || (g.i % 500),
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               TIMESTAMPTZ '2015-01-01 00:00:00+00' + g.i * interval '1 hour',
               md5('rt' || (g.i - 1))::uuid, DATE '2020-01-01' + g.i
          FROM gen g
         WHERE g.kind = 10 AND g.i > 1;

        INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                              security_id, quantity, unit_price, posting_role)
        SELECT md5('l' || g.i || '-' || v.n)::uuid, g.id, '{{Ledger}}'::uuid,v.account, v.posting, v.amount,
               v.security, v.quantity, CASE WHEN v.quantity IS NOT NULL THEN 50 END, v.role
          FROM gen g
         CROSS JOIN LATERAL (
                -- A multi-split from the bank, to one category or several.
                SELECT 2 * k AS n, g.bank AS account, k AS posting, -(10 + k)::numeric AS amount,
                       NULL::uuid AS security, NULL::numeric AS quantity, NULL::text AS role
                  FROM generate_series(0, g.lines - 1) k WHERE g.kind = 0
                UNION ALL
                SELECT 2 * k + 1, CASE WHEN g.symmetric THEN g.cat
                                       ELSE md5('cat' || (1 + (g.i + k * 7) % 20))::uuid END,
                       k, (10 + k)::numeric, NULL, NULL, NULL
                  FROM generate_series(0, g.lines - 1) k WHERE g.kind = 0
                UNION ALL
                SELECT v.n, v.account, v.posting, v.amount, v.security, v.quantity, v.role FROM (VALUES
                    -- Two lines, no account on both.
                    (1, 0, g.bank,  0, -5::numeric, NULL::uuid, NULL::numeric, NULL::text),
                    (1, 1, g.cat,   0,  5, NULL, NULL, NULL),
                    (1, 2, g.bank2, 1, -7, NULL, NULL, NULL),
                    (1, 3, g.cat2,  1,  7, NULL, NULL, NULL),
                    -- A transfer between two banks.
                    (2, 0, g.bank,  0, -10, NULL, NULL, NULL),
                    (2, 1, g.bank2, 0,  10, NULL, NULL, NULL),
                    -- A buy.
                    (3, 0, md5('hold1')::uuid, 0,  400, g.sec, 8, 'security'),
                    (3, 1, md5('brok1')::uuid, 0, -400, NULL, NULL, 'security'),
                    -- A buy_xfr with a fee, funded from the second brokerage.
                    (4, 0, md5('hold1')::uuid, 0,  1000, g.sec, 20, 'security'),
                    (4, 1, md5('brok1')::uuid, 0, -1000, NULL, NULL, 'security'),
                    (4, 2, md5('brok1')::uuid, 1,   -10, NULL, NULL, 'fee'),
                    (4, 3, md5('fee')::uuid,   1,    10, NULL, NULL, 'fee'),
                    (4, 4, md5('brok1')::uuid, 2,  1010, NULL, NULL, 'transfer'),
                    (4, 5, md5('brok2')::uuid, 2, -1010, NULL, NULL, 'transfer'),
                    -- A dividend.
                    (5, 0, md5('brok1')::uuid,  0,  3, NULL, NULL, 'income'),
                    (5, 1, md5('income')::uuid, 0, -3, NULL, NULL, 'income'),
                    -- A cash transfer from a bank.
                    (6, 0, md5('brok1')::uuid, 0,  100, NULL, NULL, 'transfer'),
                    (6, 1, g.bank,             0, -100, NULL, NULL, 'transfer'),
                    -- In kind, each brokerage paired with its own holdings.
                    (7, 0, md5('hold1')::uuid, 0, -500, g.sec, -10, 'security'),
                    (7, 1, md5('brok1')::uuid, 0,    0, NULL, NULL, 'security'),
                    (7, 2, md5('hold2')::uuid, 1,  500, g.sec,  10, 'security'),
                    (7, 3, md5('brok2')::uuid, 1,    0, NULL, NULL, 'security'),
                    -- In kind, no posting pairing them.
                    (8, 0, md5('hold1')::uuid, 0, -500, g.sec, -10, 'security'),
                    (8, 1, md5('hold2')::uuid, 0,  500, g.sec,  10, 'security'),
                    (8, 2, md5('brok1')::uuid, 1,    0, NULL, NULL, 'security'),
                    (8, 3, md5('brok2')::uuid, 1,    0, NULL, NULL, 'security'),
                    -- A template, and an occurrence: to a category, or between two banks.
                    (9, 0, g.bank, 0, -40, NULL, NULL, NULL),
                    (9, 1, CASE WHEN g.i % 3 = 0 THEN g.bank2 ELSE g.cat END, 0, 40, NULL, NULL, NULL),
                    (10, 0, g.bank, 0, -40, NULL, NULL, NULL),
                    (10, 1, CASE WHEN g.i % 3 = 0 THEN g.bank2 ELSE g.cat END, 0, 40, NULL, NULL, NULL)
                ) AS v(kind, n, account, posting, amount, security, quantity, role)
                 WHERE v.kind = g.kind
                UNION ALL
                -- A one-split to a category: a payment, or a deposit on every fourth.
                SELECT v.* FROM (VALUES
                    (0, g.bank, 0, CASE WHEN g.i % 4 = 0 THEN 25 ELSE -25 END::numeric,
                     NULL::uuid, NULL::numeric, NULL::text),
                    (1, g.cat,  0, CASE WHEN g.i % 4 = 0 THEN -25 ELSE 25 END, NULL, NULL, NULL)
                ) AS v(n, account, posting, amount, security, quantity, role)
                 WHERE g.kind >= 12
              ) v
         WHERE NOT (g.kind = 10 AND g.i = 1);

        -- The posting counts every writer keeps.
        UPDATE txn_legs l
           SET header_total_postings = c.n, account_postings_on_header = a.n
          FROM (SELECT header_id, count(DISTINCT posting_index)::int AS n
                  FROM txn_legs GROUP BY header_id) c,
               (SELECT header_id, account_id, count(DISTINCT posting_index)::int AS n
                  FROM txn_legs GROUP BY header_id, account_id) a
         WHERE c.header_id = l.header_id
           AND a.header_id = l.header_id AND a.account_id = l.account_id;

        COMMIT;

        ANALYZE;
        """;

    private static async Task<object?> ScalarAsync(string cs, string sql, Guid? ledgerId = null)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        if (ledgerId is { } id) command.Parameters.AddWithValue("LedgerId", id);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 1200 };
        await command.ExecuteNonQueryAsync();
    }
}
