using Microsoft.EntityFrameworkCore;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// Seeds a large, internally consistent ledger for the on-demand scale lane
/// (follow-ups.md → Snapshot restore performance).
/// </summary>
/// <remarks>
/// <para>Deliberately NOT a committed fixture. The representative fixture
/// (<c>data/samples/reference-ledger.json</c>, PR #414) exists to cover importer
/// SHAPES and is ~20 KB for a few dozen transactions; the same file at 50k
/// transactions would be tens of megabytes in every clone. This harness is about
/// SCALE, not import fidelity, so it seeds set-based straight into Postgres —
/// nothing large enters the repo, and a 50k-transaction ledger builds in seconds
/// instead of minutes through the import pipeline.</para>
/// <para>Row ids are derived from the row index rather than random, so a seeded
/// ledger is reproducible run to run — which matters when comparing timings.</para>
/// <para>Consistency is established by the product's own code, not by this
/// seeder guessing: it writes the transaction graph plus one open lot per buy,
/// then calls <c>recompute_holdings_cost_basis</c> once, which is what derives
/// holdings quantity/cost_basis, closes lots against sells, and writes
/// realized_gains. So the seeded ledger satisfies the same systemic invariants
/// <c>ReferenceLedgerInvariantsTests</c> asserts, by construction.</para>
/// </remarks>
public static class StressLedger
{
    /// <summary>Scale of a seeded stress ledger.</summary>
    /// <param name="BankTxns">Two-leg bank transactions (the bulk of the graph).</param>
    /// <param name="Holdings">Distinct securities held, each its own holding.</param>
    /// <param name="BuysPerHolding">Open lots created per holding.</param>
    /// <param name="SellsPerHolding">Disposals per holding — these drive the FIFO walk.</param>
    /// <param name="HeaderPayloadBytes">
    /// Approximate size of each bank header's <c>provider_raw_payload</c>. Real
    /// imported headers carry the verbatim Moneydance JSON and measured ~2.3 KB
    /// on a production ledger; 0 reproduces the old thin-row shape for A/B work.
    /// </param>
    public readonly record struct Scale(
        int BankTxns,
        int Holdings,
        int BuysPerHolding,
        int SellsPerHolding,
        int HeaderPayloadBytes = 2304)
    {
        /// <summary>Total transaction headers this scale produces.</summary>
        public int TotalTxns => BankTxns + (Holdings * (BuysPerHolding + SellsPerHolding));

        /// <summary>~50k transactions across 200 holdings — the agreed target.</summary>
        public static Scale Default => new(
            BankTxns: 45_600, Holdings: 200, BuysPerHolding: 20, SellsPerHolding: 2);

        /// <summary>
        /// Same order of investment transactions as <see cref="Default"/>, but
        /// concentrated: few holdings, hundreds of events each.
        /// </summary>
        /// <remarks>
        /// Breadth and depth are NOT interchangeable here, which the first version
        /// of this harness got wrong. <see cref="Default"/> spreads 4,400
        /// investment transactions over 200 holdings — 22 events each — and
        /// measured the FIFO recompute at 0.3s, which looks like "the walk is
        /// cheap". But its inner loop re-queries the open-lot set once per event,
        /// so cost grows with events × open-lots WITHIN a holding, not with the
        /// number of holdings. 20 holdings × 500 events × up to 400 open lots is
        /// the shape that actually probes it — a long-held position with decades of
        /// activity, which is exactly what a real ledger has and the 200-holding
        /// fixture does not.
        /// </remarks>
        public static Scale Deep => new(
            BankTxns: 0, Holdings: 20, BuysPerHolding: 400, SellsPerHolding: 100);

        /// <summary>A small scale for proving the harness itself works quickly.</summary>
        public static Scale Smoke => new(
            BankTxns: 500, Holdings: 10, BuysPerHolding: 4, SellsPerHolding: 1);
    }

    /// <summary>
    /// Seed <paramref name="ledger"/> to <paramref name="scale"/> and return how
    /// long the seed took. Idempotent in the sense that the seeding function is
    /// CREATE OR REPLACE'd, but call it once per ledger — ids collide otherwise.
    /// </summary>
    public static async Task<TimeSpan> SeedAsync(
        PostgresFixture fixture,
        SyntheticLedger ledger,
        Scale scale,
        CancellationToken cancellationToken = default)
    {
        // The brokerage (and its sibling holdings account) come from the real
        // helper so the account shape matches what the product creates.
        var broker = await ledger
            .AddInvestmentAccountAsync("stress-brokerage", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await using var db = fixture.NewServiceFactory().Create();
        await db.Database.ExecuteSqlRawAsync(SeedFunctionSql, cancellationToken).ConfigureAwait(false);

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT stress_seed_ledger(
                   {ledger.LedgerId}, {broker.Id}, {broker.HoldingsAccountId!.Value},
                   {scale.BankTxns}, {scale.Holdings}, {scale.BuysPerHolding}, {scale.SellsPerHolding},
                   {scale.HeaderPayloadBytes})",
            cancellationToken).ConfigureAwait(false);

        // A bulk-seeded table has no statistics until autovacuum catches up, which
        // it will not do inside a test run. The planner then works from defaults —
        // measured here as 22 estimated rows against 50,000 actual — and picks
        // plans no production database would pick.
        //
        // This is not a detail. Comparing migration 193's ctid keyset against 197's
        // primary-key keyset on the unanalyzed table showed the two within noise of
        // each other, because both fell back to a bitmap scan plus a 159 MB
        // external-merge sort. After ANALYZE the same comparison is 555 ms vs
        // 39 ms, and 197's index scan appears. Anything this lane concludes about
        // query PLANS is only as good as the statistics behind them.
        await db.Database.ExecuteSqlRawAsync(
            "ANALYZE accounts; ANALYZE securities; ANALYZE txn_headers; " +
            "ANALYZE txn_legs; ANALYZE holdings; ANALYZE lots;",
            cancellationToken).ConfigureAwait(false);

        return startedAt.Elapsed;
    }

    /// <summary>
    /// A DB function rather than an inline DO block: a DO body cannot take bound
    /// parameters, and building this SQL by string concatenation to work around
    /// that is how injection-shaped bugs get into test infrastructure.
    /// </summary>
    private const string SeedFunctionSql = """
        CREATE OR REPLACE FUNCTION stress_seed_ledger(
            p_ledger_id           uuid,
            p_brokerage_id        uuid,
            p_holdings_account_id uuid,
            p_bank_txns           integer,
            p_holdings            integer,
            p_buys_per_holding    integer,
            p_sells_per_holding   integer,
            p_header_payload_bytes integer DEFAULT 2304
        ) RETURNS void
        LANGUAGE plpgsql
        AS $fn$
        DECLARE
            v_base    timestamptz := '2010-01-01T00:00:00Z';
            v_banks   uuid[];
            v_cats    uuid[];
            v_secs    uuid[];
            -- Six hex digits of the ledger id, mixed into every derived id below.
            -- Without it the ids depend only on the row index, so seeding a SECOND
            -- ledger in the same database collides on accounts_pkey — which is what
            -- happened whenever both stress tests ran in one pass, i.e. whenever
            -- anyone used the documented `--filter Integration.Stress` command. Ids
            -- stay derived (reproducible per ledger), just no longer global.
            v_disc    text := substr(replace(p_ledger_id::text, '-', ''), 1, 6);
        BEGIN
            -- ----- Accounts: a handful of banks, a spread of categories ---------
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code,
                                  opening_balance, is_active, created_at)
            SELECT ('10' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'stress-bank-' || i, 'bank', 'USD',
                   1000 + i, TRUE, v_base
              FROM generate_series(1, 5) i;

            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind,
                                  currency_code, opening_balance, is_active, created_at)
            SELECT ('11' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'stress-cat-' || i, 'category', 'expense', 'USD',
                   0, TRUE, v_base
              FROM generate_series(1, 20) i;

            SELECT array_agg(id ORDER BY name) INTO v_banks
              FROM accounts WHERE ledger_id = p_ledger_id AND account_type = 'bank'
                AND name LIKE 'stress-bank-%';
            SELECT array_agg(id ORDER BY name) INTO v_cats
              FROM accounts WHERE ledger_id = p_ledger_id AND account_type = 'category'
                AND name LIKE 'stress-cat-%';

            -- ----- Bank activity: two balanced legs per header ------------------
            -- provider_raw_payload carries the verbatim Moneydance `txn` JSON on
            -- every imported header (mig 109 / ADR-0035 §3), and it dominates row
            -- width: a real ledger measured 97 MB of jsonb across 42,785 headers,
            -- ~2.3 KB each. Seeding six thin columns produced rows an order of
            -- magnitude narrower, which is not a smaller version of production —
            -- it is a different shape, and it hides every cost that scales with
            -- payload width. Snapshot capture is exactly such a cost.
            --
            -- The md5 chain is deliberate: repeated literal padding compresses to
            -- almost nothing in TOAST and would restore the very shape this is
            -- trying to avoid.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at,
                                     payee, provider_raw_payload)
            SELECT ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_base + (i * interval '1 hour'),
                   -- transacted_at defaults to the posted date, which is what the
                   -- product does when nobody sets a tax date (migration 183 / #436).
                   v_base + (i * interval '1 hour'),
                   'stress-payee-' || (i % 500),
                   CASE WHEN p_header_payload_bytes <= 0 THEN NULL ELSE
                     jsonb_build_object(
                       'obj_type', 'txn',
                       'id',       md5(i::text),
                       'acct',     md5((i * 7)::text),
                       'dt',       to_char(v_base + (i * interval '1 hour'), 'YYYYMMDD'),
                       'amt',      (i % 100000),
                       'desc',     'stress-payee-' || (i % 500),
                       'splits',   jsonb_build_array(
                                     jsonb_build_object('acct', md5((i * 3)::text),
                                                        'amt',  (i % 1000)),
                                     jsonb_build_object('acct', md5((i * 11)::text),
                                                        'amt', -(i % 1000))),
                       'raw',      (SELECT string_agg(md5((i * 1000 + g)::text), '')
                                      FROM generate_series(
                                             1, greatest(1, p_header_payload_bytes / 32)) g))
                   END
              FROM generate_series(1, p_bank_txns) i;

            INSERT INTO txn_legs (header_id, account_id, posting_index, amount, ledger_id)
            SELECT ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   v_banks[1 + (i % array_length(v_banks, 1))],
                   0,
                   -- Vary magnitudes; keep 2dp so ck_txn_legs_amount_scale_2 holds.
                   -round((1 + (i % 977) + (i % 7) / 8.0)::numeric, 2),
                   p_ledger_id
              FROM generate_series(1, p_bank_txns) i
            UNION ALL
            SELECT ('20' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   v_cats[1 + (i % array_length(v_cats, 1))],
                   1,
                   round((1 + (i % 977) + (i % 7) / 8.0)::numeric, 2),
                   p_ledger_id
              FROM generate_series(1, p_bank_txns) i;

            -- ----- Securities + holding shells ---------------------------------
            INSERT INTO securities (id, ledger_id, name, ticker, created_at)
            SELECT ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 'Stress Security ' || i, 'STR' || i, v_base
              FROM generate_series(1, p_holdings) i;

            SELECT array_agg(id ORDER BY name) INTO v_secs
              FROM securities WHERE ledger_id = p_ledger_id;

            INSERT INTO holdings (id, account_id, security_id, ledger_id, quantity, cost_basis, as_of)
            SELECT ('31' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_holdings_account_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(i), 12, '0'))::uuid,
                   p_ledger_id, 0, 0, v_base
              FROM generate_series(1, p_holdings) i;

            -- ----- Buys: cash out of the brokerage, shares into holdings -------
            -- Header id encodes (security, buy) so ids stay derived, not random.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, payee, action)
            -- Buys must ALL land before the sells below: the FIFO walk only offers
            -- a lot whose header is on or before the disposal date, so buys dated
            -- after a sell cannot fund it. Spacing these 90 days apart put most
            -- buys decades past the sells at BuysPerHolding=400 and left holdings
            -- oversold — caught by the lot-reconciliation assertion.
            SELECT ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_base + ((s * 37 + b * 3) * interval '1 day'),
                   v_base + ((s * 37 + b * 3) * interval '1 day'),
                   'buy-' || s, 'buy'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b;

            INSERT INTO txn_legs (header_id, account_id, posting_index, amount, ledger_id,
                                  security_id, quantity, unit_price, posting_role)
            -- Security side: +cost, fractional quantity (12dp scale exercised).
            SELECT ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_holdings_account_id, 0,
                   round((10 * (b + 1) * (1 + (s % 13)))::numeric, 2),
                   p_ledger_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   round((1.5 + (b % 5) + (s % 3) / 3.0)::numeric, 12),
                   round((10 * (b + 1) * (1 + (s % 13)))::numeric
                         / round((1.5 + (b % 5) + (s % 3) / 3.0)::numeric, 12), 6),
                   'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b
            UNION ALL
            -- Cash side: -cost.
            SELECT ('40' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + b), 12, '0'))::uuid,
                   p_brokerage_id, 1,
                   -round((10 * (b + 1) * (1 + (s % 13)))::numeric, 2),
                   p_ledger_id, NULL, NULL, NULL, NULL
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_buys_per_holding) b;

            -- One open lot per buy leg. recompute_holdings_cost_basis below is what
            -- closes them against sells and derives holdings + realized_gains.
            INSERT INTO lots (holding_id, leg_id, quantity, unit_cost, acquired_at, ledger_id)
            SELECT h.id, l.id, l.quantity,
                   round(l.amount / l.quantity, 12),
                   hd.posted_at, p_ledger_id
              FROM txn_legs l
              JOIN txn_headers hd ON hd.id = l.header_id
              JOIN holdings h    ON h.security_id = l.security_id
                                AND h.account_id = l.account_id
             WHERE l.ledger_id = p_ledger_id
               AND hd.action = 'buy'
               AND l.quantity > 0;

            -- ----- Sells: disposals, so the FIFO walk has lots to consume -------
            -- Dated after every buy so lot availability is never the constraint.
            INSERT INTO txn_headers (id, ledger_id, origin, posted_at, transacted_at, payee, action)
            SELECT ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_ledger_id, 'manual',
                   v_base + interval '20 years' + ((s * 7 + k) * interval '1 day'),
                   v_base + interval '20 years' + ((s * 7 + k) * interval '1 day'),
                   'sell-' || s, 'sell'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k;

            INSERT INTO txn_legs (header_id, account_id, posting_index, amount, ledger_id,
                                  security_id, quantity, unit_price, posting_role)
            -- Security side: -proceeds, negative quantity (the disposal).
            SELECT ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_holdings_account_id, 0,
                   -round((40 * (1 + (s % 11)))::numeric, 2),
                   p_ledger_id,
                   ('30' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s), 12, '0'))::uuid,
                   -round((2.0 + (k % 3))::numeric, 12),
                   round((40 * (1 + (s % 11)))::numeric
                         / round((2.0 + (k % 3))::numeric, 12), 6),
                   'security'
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k
            UNION ALL
            -- Cash side: +proceeds.
            SELECT ('50' || v_disc || '-0000-4000-8000-' || lpad(to_hex(s * 1000 + k), 12, '0'))::uuid,
                   p_brokerage_id, 1,
                   round((40 * (1 + (s % 11)))::numeric, 2),
                   p_ledger_id, NULL, NULL, NULL, NULL
              FROM generate_series(1, p_holdings) s,
                   generate_series(1, p_sells_per_holding) k;

            -- ----- Let the product derive all investment state ------------------
            PERFORM recompute_holdings_cost_basis(p_ledger_id);
            -- Materialised balances, the one derived table snapshots do not carry.
            PERFORM fn_recompute_balances_for_ledger(p_ledger_id);
        END;
        $fn$;
        """;
}
