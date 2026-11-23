using Npgsql;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Migration 263's backfill of <c>txn_headers.originating_account_id</c>, rehearsed on
/// an install staged just before it.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> The value comes from the header's CURRENT shape first
/// (the source brokerage of an in-kind transfer, the brokerage on every posting of an
/// event, the account on every line of a split) and only then from the chain: the
/// Moneydance home, the reminder source, a real account over a category, the outflow
/// side, the lower id. Every fixture below is built so that the step it names is the
/// ONLY one that gives the expected account — the lower-id and outflow candidates point
/// elsewhere wherever they could — so a step dropped or moved changes an answer.</para>
///
/// <para><b>The seeds are SQL</b>, standing in for what earlier releases wrote: the
/// rows predate the column, and each shape is one a writer of that release could
/// leave (an event the investment editor converted, a split made from the other
/// side, a category merged away). That is the accepted exception to the no-raw-writes
/// rule, and it holds because the old code could reach those states.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class OriginatingAccountBackfillTests : DatabaseTest
{
    public OriginatingAccountBackfillTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Mig263 = "263_originating_account_tag_name_key_and_saved_report_repoint.sql";

    private static Guid Id(int n) => new($"26300000-0000-0000-0000-{n:D12}");

    private static readonly Guid Ledger = Id(1);
    private static readonly Guid Security = Id(2);

    // Ordering matters: several fixtures prove a step by giving the expected account
    // the HIGHER id, so the lower-id tie-break would have picked the other one.
    private static readonly Guid BankA = Id(10);
    private static readonly Guid BankB = Id(11);
    private static readonly Guid BankC = Id(12);
    private static readonly Guid CatExpense = Id(20);
    private static readonly Guid CatIncome = Id(21);
    private static readonly Guid CatY = Id(22);
    private static readonly Guid CatX = Id(23);
    private static readonly Guid Uncategorized = Id(24);
    private static readonly Guid BrokF = Id(25);
    private static readonly Guid HoldF = Id(26);
    private static readonly Guid BrokP = Id(30);
    private static readonly Guid HoldP = Id(31);
    private static readonly Guid BrokQ = Id(32);
    private static readonly Guid HoldQ = Id(33);
    // Two brokerages naming ONE holdings account: holdings_account_id is not unique.
    private static readonly Guid BrokR = Id(34);
    private static readonly Guid BrokS = Id(35);
    private static readonly Guid HoldShared = Id(36);

    /// <summary>One header per fixture, and the account 263 must store on it.</summary>
    private static readonly (string Name, Guid Header, Guid? Expected)[] Expectations =
    [
        ("in-kind transfer, one lot (source has the higher id)",   Id(100), BrokQ),
        ("in-kind transfer, two lots",                             Id(101), BrokQ),
        ("in-kind transfer, all legs on one posting",              Id(102), BrokQ),
        ("in-kind transfer between brokerages sharing holdings",   Id(103), BrokS),
        ("one-posting brokerage-to-brokerage transfer (outflow)",  Id(104), BrokQ),
        ("event converted from a hinted feed row",                 Id(105), BrokP),
        ("buy_xfr with a fee, funded by a lower-id brokerage",     Id(106), BrokP),
        ("withdrawal (real over category)",                        Id(107), BankA),
        ("deposit (real over category, before outflow)",           Id(108), BankA),
        ("real-to-real transfer (outflow)",                        Id(109), BankB),
        ("zero-sum split between two real accounts (lower id)",    Id(110), BankA),
        ("symmetric split, every line to one category",            Id(111), BankB),
        ("template, NULL source, one real leg",                    Id(112), BankC),
        ("template, NULL source, two real legs, Moneydance home",  Id(113), BankB),
        ("template, NULL source, two real legs, no home",          Id(114), BankC),
        ("occurrence of a NULL-source series (template's value)",  Id(115), BankB),
        ("template whose series names its source",                 Id(116), BankB),
        ("header without legs",                                    Id(117), null),
        ("feed row still on Uncategorized",                        Id(118), BankA),
        ("feed row recategorized to a real account (outflow)",     Id(119), BankC),
        ("cash transfer moved off its brokerage onto a bank",      Id(120), BankC),
        ("Moneydance one-split decided by its home",               Id(121), BankB),
        ("Moneydance symmetric split owned by its home category",  Id(122), CatExpense),
        ("Moneydance transfer converted to an event",              Id(123), BrokP),
        ("Moneydance one-split split from its other side",         Id(124), BankB),
        ("occurrence split from its other side",                   Id(125), BankB),
        ("Moneydance buyx funded by a lower-id outflow broker",    Id(126), BrokP),
        ("Moneydance category row whose home was merged away",     Id(127), CatX),
        ("template of the occurrence split from the other side",   Id(128), BankA),
        ("in-kind transfer with no brokerage paired by posting",   Id(129), BrokQ),
        ("split with no account on every line (outflow)",          Id(130), BankB),
    ];

    private (string ConnectionString, string WorkDir) FreshInstallBefore263(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig263-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(Mig263, work));
        return (cs, work);
    }

    [Fact]
    public async Task Every_header_gets_the_account_its_shape_then_the_chain_names()
    {
        var (cs, work) = FreshInstallBefore263("mig263_backfill");
        await SeedAccountsAsync(cs);
        await SeedFixturesAsync(cs);

        UpgradeRehearsal.Apply(cs, work, Mig263);

        var stored = await StoredAsync(cs);
        var wrong = Expectations
            .Where(e => stored[e.Header] != e.Expected)
            .Select(e => $"{e.Name}: expected {Label(e.Expected)}, stored {Label(stored[e.Header])}")
            .ToList();
        Assert.True(wrong.Count == 0, "263 stored the wrong originating account:\n  " + string.Join("\n  ", wrong));
        Assert.Equal(Expectations.Length, stored.Count);
    }

    [Fact]
    public async Task A_rerun_changes_nothing_and_a_broken_invariant_fails_the_upgrade()
    {
        var (cs, work) = FreshInstallBefore263("mig263_rerun");
        await SeedAccountsAsync(cs);
        await SeedFixturesAsync(cs);

        const string everything = "SELECT id, originating_account_id FROM txn_headers ORDER BY id";
        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig263);
        var afterRerun = await UpgradeRehearsal.DigestAsync(cs, everything);

        // A third run over what the first two left: deterministic, and it never overwrites.
        await RerunAsync(cs, work);
        Assert.Equal(afterRerun, await UpgradeRehearsal.DigestAsync(cs, everything));

        // Each invariant, broken on a re-run, fails the upgrade by its own clause and
        // leaves the value as it was (one BEGIN/COMMIT).
        await AssertRerunRefusesAsync(cs, work, Id(101), BrokP, "(c) transfer_shares not owned by its source brokerage: 1");
        await AssertRerunRefusesAsync(cs, work, Id(105), HoldP, "(b) actioned event not owned by a brokerage on every posting: 1");
        await AssertRerunRefusesAsync(cs, work, Id(111), BankC, "(a) not on a leg, or NULL with legs: 1");
        await AssertRerunRefusesAsync(cs, work, Id(124), BankA, "(d) multi-split not owned by an account on every line: 1");
        Assert.Equal(afterRerun, await UpgradeRehearsal.DigestAsync(cs, everything));
    }

    [Fact]
    public async Task An_event_with_no_brokerage_on_every_posting_fails_the_upgrade_and_changes_nothing()
    {
        var (cs, work) = FreshInstallBefore263("mig263_no_brokerage");
        await SeedAccountsAsync(cs);
        // Shares in against a category: no brokerage leg at all. No current writer
        // produces it, so the upgrade must stop rather than invent an owner.
        await HeaderAsync(cs, Id(200), action: "buyx");
        await LegsAsync(cs, Id(200),
            (HoldP, 0, 900m, Security, 30m, "security"),
            (CatExpense, 0, -900m, null, null, "security"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => UpgradeRehearsal.Apply(cs, work, Mig263)));
        Assert.Contains("(b) actioned event not owned by a brokerage on every posting: 1", Flatten(ex), StringComparison.Ordinal);
        Assert.False(await ColumnExistsAsync(cs), "263 failed but left its column behind — the script is not atomic");
    }

    private static async Task AssertRerunRefusesAsync(
        string cs, string work, Guid header, Guid wrongValue, string clause)
    {
        var before = await ScalarAsync(cs, $"SELECT originating_account_id FROM txn_headers WHERE id = '{header}'");
        // The FK accepts any account of the ledger; the invariants are what refuse it.
        await ExecAsync(cs, $"UPDATE txn_headers SET originating_account_id = '{wrongValue}' WHERE id = '{header}'");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => RerunAsync(cs, work));
        Assert.Contains(clause, Flatten(ex), StringComparison.Ordinal);

        await ExecAsync(cs, $"UPDATE txn_headers SET originating_account_id = '{before}' WHERE id = '{header}'");
    }

    private static async Task RerunAsync(string cs, string work)
    {
        await ExecAsync(cs, $"DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%{Mig263}'");
        UpgradeRehearsal.RunStaged(cs, work);
    }

    // ----- seeds -----

    private static async Task SeedAccountsAsync(string cs)
    {
        await ExecAsync(cs, $"""
            INSERT INTO ledgers (id, name) VALUES ('{Ledger}', 'Ledger A');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, is_system, provider_raw_payload)
            VALUES ('{BankA}',         '{Ledger}', 'Account A',  'bank',       NULL,      'USD', FALSE, {MdId("md-a")}),
                   ('{BankB}',         '{Ledger}', 'Account B',  'bank',       NULL,      'USD', FALSE, {MdId("md-b")}),
                   ('{BankC}',         '{Ledger}', 'Account C',  'bank',       NULL,      'USD', FALSE, NULL),
                   ('{CatExpense}',    '{Ledger}', 'Category A', 'category',   'expense', 'USD', FALSE, {MdId("md-cat")}),
                   ('{CatIncome}',     '{Ledger}', 'Category B', 'category',   'income',  'USD', FALSE, NULL),
                   ('{CatY}',          '{Ledger}', 'Category C', 'category',   'expense', 'USD', FALSE, NULL),
                   ('{CatX}',          '{Ledger}', 'Category D', 'category',   'expense', 'USD', FALSE, NULL),
                   ('{Uncategorized}', '{Ledger}', 'Category E', 'category',   'expense', 'USD', TRUE,  NULL),
                   ('{HoldF}',         '{Ledger}', 'Holdings F', 'investment', NULL,      'USD', TRUE,  NULL),
                   ('{HoldP}',         '{Ledger}', 'Holdings P', 'investment', NULL,      'USD', TRUE,  NULL),
                   ('{HoldQ}',         '{Ledger}', 'Holdings Q', 'investment', NULL,      'USD', TRUE,  NULL),
                   ('{HoldShared}',    '{Ledger}', 'Holdings R', 'investment', NULL,      'USD', TRUE,  NULL);
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id, provider_raw_payload)
            VALUES ('{BrokF}', '{Ledger}', 'Brokerage F', 'investment', 'USD', '{HoldF}',      NULL),
                   ('{BrokP}', '{Ledger}', 'Brokerage P', 'investment', 'USD', '{HoldP}',      {MdId("md-brok")}),
                   ('{BrokQ}', '{Ledger}', 'Brokerage Q', 'investment', 'USD', '{HoldQ}',      NULL),
                   ('{BrokR}', '{Ledger}', 'Brokerage R', 'investment', 'USD', '{HoldShared}', NULL),
                   ('{BrokS}', '{Ledger}', 'Brokerage S', 'investment', 'USD', '{HoldShared}', NULL);
            INSERT INTO securities (id, ledger_id, name) VALUES ('{Security}', '{Ledger}', 'Security A');
            """);
    }

    /// <summary>An account's Moneydance payload: just the id 263 resolves a home through.</summary>
    private static string MdId(string mdId) => $"'{{\"id\": \"{mdId}\"}}'::jsonb";

    private static async Task SeedFixturesAsync(string cs)
    {
        // In-kind transfers, Q to P: per lot, Q's zero-cash leg pairs with Q's holdings
        // going out, and P's with P's holdings coming in (BuildTransferSharesPostings).
        await HeaderAsync(cs, Id(100), action: "transfer_shares");
        await LegsAsync(cs, Id(100),
            (HoldQ, 0, -500m, Security, -10m, "security"), (BrokQ, 0, 0m, null, null, "security"),
            (HoldP, 1, 500m, Security, 10m, "security"),   (BrokP, 1, 0m, null, null, "security"));
        await HeaderAsync(cs, Id(101), action: "transfer_shares");
        await LegsAsync(cs, Id(101),
            (HoldQ, 0, -500m, Security, -10m, "security"), (BrokQ, 0, 0m, null, null, "security"),
            (HoldP, 1, 500m, Security, 10m, "security"),   (BrokP, 1, 0m, null, null, "security"),
            (HoldQ, 2, -300m, Security, -5m, "security"),  (BrokQ, 2, 0m, null, null, "security"),
            (HoldP, 3, 300m, Security, 5m, "security"),    (BrokP, 3, 0m, null, null, "security"));
        // The older test-seed shape: all four legs on one posting.
        await HeaderAsync(cs, Id(102), action: "transfer_shares");
        await LegsAsync(cs, Id(102),
            (HoldQ, 0, -500m, Security, -10m, "security"), (BrokQ, 0, 0m, null, null, "security"),
            (HoldP, 0, 500m, Security, 10m, "security"),   (BrokP, 0, 0m, null, null, "security"));
        // S to R over one shared holdings account: only the posting pairing tells them apart.
        await HeaderAsync(cs, Id(103), action: "transfer_shares");
        await LegsAsync(cs, Id(103),
            (HoldShared, 0, -500m, Security, -10m, "security"), (BrokS, 0, 0m, null, null, "security"),
            (HoldShared, 1, 500m, Security, 10m, "security"),   (BrokR, 1, 0m, null, null, "security"));

        // Brokerage to brokerage, one posting: both are in E; Q pays out.
        await HeaderAsync(cs, Id(104), action: "transfer");
        await LegsAsync(cs, Id(104), (BrokP, 0, 100m, null, null, "transfer"), (BrokQ, 0, -100m, null, null, "transfer"));

        // A feed row the investment editor turned into a buy.
        await HeaderAsync(cs, Id(105), action: "buy", origin: "online_import", externalId: "feed-105");
        await LegsAsync(cs, Id(105), (HoldP, 0, 400m, Security, 8m, "security"), (BrokP, 0, -400m, null, null, "security"));

        // buy_xfr with a fee, funded from F: F is on the transfer posting only, pays
        // out, and has the lower id.
        await HeaderAsync(cs, Id(106), action: "buyx");
        await LegsAsync(cs, Id(106),
            (HoldP, 0, 1000m, Security, 20m, "security"), (BrokP, 0, -1000m, null, null, "security"),
            (BrokP, 1, -10m, null, null, "fee"),         (CatExpense, 1, 10m, null, null, "fee"),
            (BrokP, 2, 1010m, null, null, "transfer"),   (BrokF, 2, -1010m, null, null, "transfer"));

        await OneSplitAsync(cs, Id(107), BankA, -50m, CatExpense);
        // A deposit: the category is the outflow side, so real-over-category must run first.
        await OneSplitAsync(cs, Id(108), BankA, 50m, CatIncome);
        await OneSplitAsync(cs, Id(109), BankA, 100m, BankB);

        // Two real accounts on every line, each netting zero.
        await HeaderAsync(cs, Id(110));
        await LegsAsync(cs, Id(110),
            (BankA, 0, 10m, null, null, null), (BankB, 0, -10m, null, null, null),
            (BankA, 1, -10m, null, null, null), (BankB, 1, 10m, null, null, null));

        // Every line to one income category, which is the outflow side.
        await HeaderAsync(cs, Id(111));
        await LegsAsync(cs, Id(111),
            (BankB, 0, 30m, null, null, null), (CatIncome, 0, -30m, null, null, null),
            (BankB, 1, 20m, null, null, null), (CatIncome, 1, -20m, null, null, null));

        // Reminder templates and their series.
        await TemplateAsync(cs, Id(112), series: Id(300), source: null, BankC, -40m, CatExpense);
        await TemplateAsync(cs, Id(113), series: Id(301), source: null, BankA, -25m, BankB,
            mdReminderId: "r301", mdHome: "md-b");
        await TemplateAsync(cs, Id(114), series: Id(302), source: null, BankA, 15m, BankC);
        // An occurrence of 301: no payload; the outflow and the lower id both say A.
        await HeaderAsync(cs, Id(115), series: Id(301));
        await LegsAsync(cs, Id(115), (BankA, 0, -25m, null, null, null), (BankB, 0, 25m, null, null, null));
        await TemplateAsync(cs, Id(116), series: Id(303), source: BankB, BankA, -5m, BankB);

        await HeaderAsync(cs, Id(117));

        await HeaderAsync(cs, Id(118), origin: "online_import", externalId: "feed-118");
        await LegsAsync(cs, Id(118), (BankA, 0, -12m, null, null, null), (Uncategorized, 0, 12m, null, null, null));
        await HeaderAsync(cs, Id(119), origin: "online_import", externalId: "feed-119");
        await LegsAsync(cs, Id(119), (BankA, 0, 12m, null, null, null), (BankC, 0, -12m, null, null, null));

        // A cash transfer moved from its brokerage onto another bank, which cleared its
        // action: now a plain bank-to-bank transfer.
        await OneSplitAsync(cs, Id(120), BankB, 70m, BankC);

        // Moneydance rows.
        await HeaderAsync(cs, Id(121), mdHome: "md-b");
        await LegsAsync(cs, Id(121), (BankA, 0, -9m, null, null, null), (BankB, 0, 9m, null, null, null));
        await HeaderAsync(cs, Id(122), mdHome: "md-cat");
        await LegsAsync(cs, Id(122),
            (CatExpense, 0, -6m, null, null, null), (BankB, 0, 6m, null, null, null),
            (CatExpense, 1, -4m, null, null, null), (BankB, 1, 4m, null, null, null));
        // A bank-home transfer to a brokerage, given an action in the brokerage
        // register. The home is not on the event's every-posting brokerage list.
        await HeaderAsync(cs, Id(123), action: "transfer", mdHome: "md-a");
        await LegsAsync(cs, Id(123), (BankA, 0, -100m, null, null, "transfer"), (BrokP, 0, 100m, null, null, "transfer"));
        // A one-split homed on A, split into two lines from B.
        await HeaderAsync(cs, Id(124), mdHome: "md-a");
        await LegsAsync(cs, Id(124),
            (BankA, 0, -60m, null, null, null), (BankB, 0, 60m, null, null, null),
            (BankB, 1, -10m, null, null, null), (CatExpense, 1, 10m, null, null, null));
        // The same for an occurrence: its series' source is A, which is on line 0 only.
        await TemplateAsync(cs, Id(128), series: Id(304), source: BankA, BankA, -60m, BankB);
        await HeaderAsync(cs, Id(125), series: Id(304));
        await LegsAsync(cs, Id(125),
            (BankA, 0, -60m, null, null, null), (BankB, 0, 60m, null, null, null),
            (BankB, 1, -10m, null, null, null), (CatExpense, 1, 10m, null, null, null));
        // A buyx funded from F, which has the lower id and pays out.
        await HeaderAsync(cs, Id(126), action: "buyx", mdHome: "md-brok");
        await LegsAsync(cs, Id(126),
            (HoldP, 0, 700m, Security, 14m, "security"), (BrokP, 0, -700m, null, null, "security"),
            (BrokP, 1, 700m, null, null, "transfer"),   (BrokF, 1, -700m, null, null, "transfer"));
        // Category to category, homed on a category no account carries any more.
        // X pays out and has the higher id.
        await HeaderAsync(cs, Id(127), mdHome: "md-gone");
        await LegsAsync(cs, Id(127), (CatX, 0, -8m, null, null, null), (CatY, 0, 8m, null, null, null));

        // Q to P with both brokerage legs on a posting of their own: no posting pairs a
        // brokerage with its holdings, so only the fallback names the source (Q, whose
        // holdings carry the outgoing shares, and which has the higher id).
        await HeaderAsync(cs, Id(129), action: "transfer_shares");
        await LegsAsync(cs, Id(129),
            (HoldQ, 0, -500m, Security, -10m, "security"), (HoldP, 0, 500m, Security, 10m, "security"),
            (BrokQ, 1, 0m, null, null, "security"),        (BrokP, 1, 0m, null, null, "security"));

        // Two lines, no account on both: every account is a candidate, the real ones
        // win over the categories, and B pays out though A has the lower id.
        await HeaderAsync(cs, Id(130));
        await LegsAsync(cs, Id(130),
            (BankA, 0, 5m, null, null, null),  (CatExpense, 0, -5m, null, null, null),
            (BankB, 1, -7m, null, null, null), (CatY, 1, 7m, null, null, null));
    }

    private static async Task OneSplitAsync(string cs, Guid header, Guid account, decimal amount, Guid other)
    {
        await HeaderAsync(cs, header);
        await LegsAsync(cs, header, (account, 0, amount, null, null, null), (other, 0, -amount, null, null, null));
    }

    private static async Task TemplateAsync(
        string cs, Guid header, Guid series, Guid? source, Guid account, decimal amount, Guid other,
        string? mdReminderId = null, string? mdHome = null)
    {
        var importSource = mdReminderId is null ? "NULL" : "'moneydance_export'";
        var externalId = mdReminderId is null ? "NULL" : $"'mdreminder:{mdReminderId}'";
        await ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, external_id, import_source, payee,
                                     posted_at, transacted_at, created_at, is_recurring_template)
            VALUES ('{header}', '{Ledger}', 'manual', {externalId}, {importSource}, 'Payee A',
                    TIMESTAMPTZ '2026-01-01 00:00:00+00', TIMESTAMPTZ '2026-01-01 00:00:00+00',
                    TIMESTAMPTZ '2026-01-01 00:00:00+00', TRUE);
            """);
        await LegsAsync(cs, header, (account, 0, amount, null, null, null), (other, 0, -amount, null, null, null));
        var origin = mdReminderId is null ? "'manual'" : "'moneydance_import'";
        var rtExternal = mdReminderId is null ? "NULL" : $"'{mdReminderId}'";
        var payload = mdHome is null ? "NULL" : $"'{{\"txn.acctid\": \"{mdHome}\"}}'::jsonb";
        var sourceSql = source is null ? "NULL" : $"'{source}'";
        await ExecAsync(cs, $"""
            INSERT INTO recurring_transactions
                (id, ledger_id, start_date, rrule, next_due_date, template_header_id,
                 source_account_id, origin, external_id, source_payload)
            VALUES ('{series}', '{Ledger}', DATE '2026-01-01', 'FREQ=MONTHLY;BYMONTHDAY=1', DATE '2026-01-01',
                    '{header}', {sourceSql}, {origin}, {rtExternal}, {payload});
            """);
    }

    private static Task HeaderAsync(
        string cs, Guid header, string? action = null, string origin = "manual", string? externalId = null,
        string? mdHome = null, Guid? series = null)
    {
        var actionSql = action is null ? "NULL" : $"'{action}'";
        var providerKey = origin == "manual" ? "NULL" : "'simplefin'";
        var external = mdHome is not null ? $"'md-{header:N}'" : externalId is null ? "NULL" : $"'{externalId}'";
        var importSource = mdHome is null ? "NULL" : "'moneydance_export'";
        var payload = mdHome is null ? "NULL" : $"'{{\"acctid\": \"{mdHome}\"}}'::jsonb";
        var seriesSql = series is null ? "NULL" : $"'{series}'";
        var occurrence = series is null ? "NULL" : "DATE '2026-02-01'";
        return ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, provider_key, external_id, import_source,
                                     provider_raw_payload, action, payee, posted_at, transacted_at,
                                     created_at, recurring_transaction_id, occurrence_date)
            VALUES ('{header}', '{Ledger}', '{origin}', {providerKey}, {external}, {importSource},
                    {payload}, {actionSql}, 'Payee A', TIMESTAMPTZ '2026-02-01 00:00:00+00',
                    TIMESTAMPTZ '2026-02-01 00:00:00+00', TIMESTAMPTZ '2026-02-01 00:00:00+00',
                    {seriesSql}, {occurrence});
            """);
    }

    private static async Task LegsAsync(
        string cs, Guid header,
        params (Guid Account, int Posting, decimal Amount, Guid? Security, decimal? Quantity, string? Role)[] legs)
    {
        foreach (var leg in legs)
        {
            var security = leg.Security is null ? "NULL" : $"'{leg.Security}'";
            var quantity = leg.Quantity is null ? "NULL" : leg.Quantity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var unitPrice = leg.Quantity is null ? "NULL" : "50";
            var role = leg.Role is null ? "NULL" : $"'{leg.Role}'";
            await ExecAsync(cs, $"""
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount,
                                      security_id, quantity, unit_price, posting_role)
                VALUES ('{Guid.NewGuid()}', '{header}', '{Ledger}', '{leg.Account}', {leg.Posting},
                        {leg.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                        {security}, {quantity}, {unitPrice}, {role});
                """);
        }
    }

    // ----- reads -----

    private static async Task<Dictionary<Guid, Guid?>> StoredAsync(string cs)
    {
        var result = new Dictionary<Guid, Guid?>();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, originating_account_id FROM txn_headers", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetGuid(0)] = await reader.IsDBNullAsync(1) ? null : reader.GetGuid(1);
        return result;
    }

    private static readonly Dictionary<Guid, string> Labels = new()
    {
        [BankA] = "BankA", [BankB] = "BankB", [BankC] = "BankC",
        [CatExpense] = "CatExpense", [CatIncome] = "CatIncome", [CatX] = "CatX", [CatY] = "CatY",
        [Uncategorized] = "Uncategorized", [BrokF] = "BrokF", [HoldF] = "HoldF",
        [BrokP] = "BrokP", [HoldP] = "HoldP", [BrokQ] = "BrokQ", [HoldQ] = "HoldQ",
        [BrokR] = "BrokR", [BrokS] = "BrokS", [HoldShared] = "HoldShared",
    };

    private static string Label(Guid? id) =>
        id is null ? "NULL" : Labels.TryGetValue(id.Value, out var name) ? name : id.Value.ToString();

    private static async Task<bool> ColumnExistsAsync(string cs) =>
        await ScalarAsync(cs, """
            SELECT 1 FROM information_schema.columns
             WHERE table_name = 'txn_headers' AND column_name = 'originating_account_id'
            """) is not null;

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" || ", parts);
    }
}
