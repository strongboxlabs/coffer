using Dapper;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Npgsql;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// Migration 260 — bring a ledger the OLD importer bootstrapped to exactly what
/// the new one writes for the same Moneydance data: one-posting brokerage cash
/// moves on the bank route become 'transfer', the investment route's dropped
/// <c>chk</c> comes back as the check number, and differing feed text is seeded
/// into <c>txn_header_originals</c> (FU-024).
/// </summary>
/// <remarks>
/// <para>MD import is a one-shot bootstrap with no re-import onto an existing
/// ledger, so the migration is the only way an already-imported ledger gets
/// these fixes — and it cannot be re-run against production to correct a
/// mistake.</para>
///
/// <para>The pre-260 state is built from real output. The NEW importer runs on
/// synthetic MD JSON, its output is snapshotted, and then only the fields 260
/// restores are rewound: action and posting role on the bank-route brokerage rows,
/// check number on the investment route, and the seeded originals rows. Every
/// other column is what a real code path wrote, so 260 must reproduce the snapshot
/// exactly.</para>
///
/// <para>Memo and tag placement is left as the new importer writes it, which is
/// not where the old importer put it (the old one kept every memo on the header).
/// So this state is the new importer's output with 260's fields undone, not a
/// ledger an old importer left behind. The originals seed on the header placement
/// an older importer wrote is covered only for one-splits whose curated memo has
/// surrounding white space; every other upgrade shape is the 260-then-264 tests'.</para>
///
/// <para>The fixture applies every migration at startup against empty tables;
/// these tests re-execute the same file, which is the re-run it is written to
/// tolerate.</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class BrokerageTransferAndOriginalsBackfillTests : DatabaseTest
{
    private const string ImportSource = "moneydance_export";

    public BrokerageTransferAndOriginalsBackfillTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    // Every QIF cash word the bank route sees on a brokerage, against every kind
    // of other side: named income and expense categories, MD's blank category,
    // a bank account and a second brokerage.
    private static readonly string[] QifCashWords = ["XOut", "XIn", "Cash", "ContribX", "MiscIncX"];
    private static readonly string[] CashTargets  = ["a-inc", "a-exp", "a-blank", "a-bank", "a-brk2"];

    /// <summary>Bank-route rows the new importer stamps 'transfer'.</summary>
    private static readonly string[] BankRouteTransfers =
    [
        .. QifCashWords.SelectMany(word => CashTargets.Select(target => $"t-{word}-{target}")),
        "t-plain-cat", "t-plain-bank", "t-self", "t-brk-orig", "t-bad-txntype", "t-bad-split",
        "mdreminder:rem-brk",
    ];

    /// <summary>Rows with no investment action under either importer.</summary>
    private static readonly string[] StaysActionless =
    [
        "t-multi", "t-bank-xfr", "t-bank-plain",
        "t-orig-payee", "t-orig-same", "t-orig-fallback", "t-orig-blankfeed", "t-bad-orig",
        "t-orig-trim", "t-orig-split-same", "t-orig-split-diff", "t-old-trim", "t-old-payee",
        "mdreminder:rem-bank", "mdreminder:rem-brk-multi", "mdreminder:rem-brk-inv", "mdreminder:rem-brk-div",
    ];

    /// <summary>Investment-route rows whose <c>chk</c> the old importer dropped, and that chk.</summary>
    private static readonly Dictionary<string, string> RestoredCheckNumbers = new(StringComparer.Ordinal)
    {
        ["t-inv-xout"]    = "XOut",
        ["t-inv-intincx"] = "IntIncX",
        ["t-orig-memo"]   = "XIn",
    };

    /// <summary>Investment-route rows with no usable <c>chk</c>.</summary>
    private static readonly string[] NoCheckNumber = ["t-inv-blank", "t-inv-nbsp", "t-inv-nochk", "t-bad-chk"];

    /// <summary>Headers whose feed text differs from the curated text.</summary>
    private static readonly string[] SeededOriginals = ["t-brk-orig", "t-old-payee", "t-orig-memo", "t-orig-payee", "t-orig-split-diff"];

    /// <summary>One-splits whose curated memo has surrounding white space, the shape the header placement test rewinds.</summary>
    private static readonly string[] OldHeaderPlacement = ["t-old-trim", "t-old-payee"];

    [Fact]
    public async Task Rewound_old_importer_state_is_restored_to_exactly_what_the_new_importer_writes()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);

        var expected = await SnapshotAsync(conn, ledgerId);
        AssertFixtureExercisesEveryShape(expected);
        // The importer's seed carries the amount as written, its flag false (ADR-0114 D15).
        var imported = await OriginalAmountsAsync(conn, ledgerId);
        Assert.All(imported, a => Assert.Equal((a.HomeLegSum, false), (a.Amount, a.RoundedAtImport)));
        Assert.All(imported, a => Assert.NotNull(a.Amount));

        await RewindToOldImporterAsync(conn, ledgerId);
        await RunMigrationAsync(conn);

        var actual = await SnapshotAsync(conn, ledgerId);
        Assert.Equal(expected.Headers,   actual.Headers);
        Assert.Equal(expected.Legs,      actual.Legs);
        Assert.Equal(expected.Originals, actual.Originals);
        // 260's seeds keep no amount (ADR-0114 D15): it ran before the column existed, and
        // a leg's amount on an old ledger may be an edit, not the import's figure. So the
        // amount and the flag are not among the columns the snapshot compares.
        Assert.All(await OriginalAmountsAsync(conn, ledgerId),
            a => Assert.Equal(((decimal?)null, false), (a.Amount, a.RoundedAtImport)));

        // The importer's captured_at is its insert's clock; the migration's must be
        // derived from the data. The header's own created_at is that value.
        var undated = await conn.ExecuteScalarAsync<int>("""
            SELECT count(*)
              FROM txn_header_originals o
              JOIN txn_headers h ON h.id = o.header_id
             WHERE o.ledger_id = @L AND o.captured_at IS DISTINCT FROM h.created_at;
            """, new { L = ledgerId });
        Assert.Equal(0, undated);
    }

    [Fact]
    public async Task On_the_header_placement_an_older_importer_wrote_the_seed_matches_a_fresh_import()
    {
        // An importer before the per-side placement kept a one-split's memo on the
        // header as Moneydance holds it, surrounding white space included, and no
        // leg memo. 260 must seed from that state exactly what a fresh import
        // seeds: the feed memo compared over trimmed text, and the written memo a
        // differing payee's seed carries stored trimmed.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        var expected = (await SnapshotAsync(conn, ledgerId)).Originals;
        Assert.Contains(expected, o => o.ExternalId == "t-old-payee" && o.Memo == "kept memo");
        Assert.DoesNotContain(expected, o => o.ExternalId == "t-old-trim");

        await RewindToOldImporterAsync(conn, ledgerId);
        await conn.ExecuteAsync("""
            UPDATE txn_legs SET leg_memo = NULL
             WHERE header_id IN (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = ANY(@Ids));
            UPDATE txn_headers SET memo = ' kept memo '
             WHERE ledger_id = @L AND external_id = ANY(@Ids);
            """, new { L = ledgerId, Ids = OldHeaderPlacement });

        await RunMigrationAsync(conn);

        Assert.Equal(expected, (await SnapshotAsync(conn, ledgerId)).Originals);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Running_it_again_changes_nothing()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        await RunMigrationAsync(conn);
        var once = await FullSnapshotAsync(conn, ledgerId);

        await RunMigrationAsync(conn);
        await RunMigrationAsync(conn);
        var thrice = await FullSnapshotAsync(conn, ledgerId);

        Assert.Equal(once.Headers,   thrice.Headers);
        Assert.Equal(once.Legs,      thrice.Legs);
        Assert.Equal(once.Originals, thrice.Originals);
    }

    [Fact]
    public async Task Values_the_user_already_set_are_never_overwritten()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        // The user classified a brokerage cash row themselves, as the investment
        // editor writes it: action on the header, role on both legs.
        await conn.ExecuteAsync("""
            UPDATE txn_headers SET action = 'misc' WHERE ledger_id = @L AND external_id = 't-XOut-a-inc';
            UPDATE txn_legs SET posting_role = 'income'
             WHERE header_id = (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-XOut-a-inc');
            """, new { L = ledgerId });

        // First edits after the old import: a check number set on an investment
        // row, and payees renamed.
        await FirstEditAsync(conn, ledgerId, ["t-inv-xout"],
            "UPDATE txn_headers SET check_number = 'USER-SET'");
        await FirstEditAsync(conn, ledgerId, ["t-orig-payee", "t-inv-intincx"],
            "UPDATE txn_headers SET payee = 'Renamed'");

        await RunMigrationAsync(conn);
        var after = await FullSnapshotAsync(conn, ledgerId);

        var classified = Header(after, "t-XOut-a-inc");
        Assert.Equal("misc", classified.Action);
        Assert.All(LegsOf(after, "t-XOut-a-inc"), l => Assert.Equal("income", l.PostingRole));

        Assert.Equal("USER-SET", Header(after, "t-inv-xout").CheckNumber);
        Assert.Equal("Renamed",  Header(after, "t-orig-payee").Payee);
        Assert.Equal("Renamed",  Header(after, "t-inv-intincx").Payee);
        // A header check number the user did not set is restored as usual.
        Assert.Equal("IntIncX",  Header(after, "t-inv-intincx").CheckNumber);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task An_originals_row_captured_before_the_upgrade_ends_as_the_new_importer_leaves_it()
    {
        // A first edit after the OLD import captured the row as that importer
        // left it: MD's curated payee and memo (the feed's were never written)
        // and, on the investment route, no check number. Under the new importer
        // the sidecar holds the feed's text from the import on, and a later
        // edit's capture-once leaves it there; the check number is chk. So the
        // capture's curated text gives way to a non-blank feed value that
        // differs, and its NULL check number to chk: neither was a user's choice.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        string[] edited =
        [
            "t-orig-payee",      // bank route, feed payee differs
            "t-orig-memo",       // investment route, feed memo differs, chk dropped
            "t-orig-same",       // feed text equals the curated text
            "t-orig-blankfeed",  // feed payee is blank
            "t-inv-intincx",     // investment route, chk dropped
            "t-inv-xout",        // investment route, chk dropped, then set by the user
            "t-bank-plain",      // bank route, no feed text at all
        ];
        await FirstEditAsync(conn, ledgerId, edited, "UPDATE txn_headers SET payee = 'Renamed'");
        await conn.ExecuteAsync(
            "UPDATE txn_headers SET check_number = 'USER-SET' WHERE ledger_id = @L AND external_id = 't-inv-xout';",
            new { L = ledgerId });
        var captured = (await FullSnapshotAsync(conn, ledgerId)).Originals.ToDictionary(o => o.ExternalId);
        Assert.Equal(edited.Order(StringComparer.Ordinal), captured.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Shop", captured["t-orig-payee"].Payee);              // the curated text
        Assert.Equal("curated memo", captured["t-orig-memo"].Memo);
        Assert.Null(captured["t-orig-memo"].CheckNumber);                  // the old importer's drop

        await RunMigrationAsync(conn);
        var after = (await FullSnapshotAsync(conn, ledgerId)).Originals.ToDictionary(o => o.ExternalId);

        // The captures, plus the fresh seed of a header nobody edited.
        Assert.Equal(
            captured.Keys.Union(SeededOriginals).Order(StringComparer.Ordinal),
            after.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(captured["t-orig-payee"]  with { Payee = "SHOP RAW" },                 after["t-orig-payee"]);
        Assert.Equal(captured["t-orig-memo"]   with { Memo = "raw memo", CheckNumber = "XIn" }, after["t-orig-memo"]);
        Assert.Equal(captured["t-inv-intincx"] with { CheckNumber = "IntIncX" },            after["t-inv-intincx"]);
        // The header's check number is the user's and stays; the sidecar's NULL
        // was never theirs.
        Assert.Equal(captured["t-inv-xout"]    with { CheckNumber = "XOut" },               after["t-inv-xout"]);
        Assert.Equal(captured["t-orig-same"],      after["t-orig-same"]);
        Assert.Equal(captured["t-orig-blankfeed"], after["t-orig-blankfeed"]);
        Assert.Equal(captured["t-bank-plain"],     after["t-bank-plain"]);

        // And a second run moves nothing.
        await RunMigrationAsync(conn);
        Assert.Equal(
            after.Values.OrderBy(o => o.ExternalId, StringComparer.Ordinal),
            (await FullSnapshotAsync(conn, ledgerId)).Originals);
    }

    [Fact]
    public async Task An_investment_shaped_row_the_user_set_back_to_no_action_is_left_alone()
    {
        // The investment route stamped it 'transfer'; the user cleared the action
        // and the roles. Its payload passes every other bank-route test (one
        // posting, brokerage home), so only the investment-shape exclusion keeps
        // the migration off it: the importer would never have run the rule on it.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);
        Assert.Equal("transfer", Header(await SnapshotAsync(conn, ledgerId), "t-inv-nochk").Action);

        await conn.ExecuteAsync("""
            UPDATE txn_headers SET action = NULL WHERE ledger_id = @L AND external_id = 't-inv-nochk';
            UPDATE txn_legs SET posting_role = NULL
             WHERE header_id = (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-inv-nochk');
            """, new { L = ledgerId });

        await RunMigrationAsync(conn);
        var after = await SnapshotAsync(conn, ledgerId);

        Assert.Null(Header(after, "t-inv-nochk").Action);
        Assert.All(LegsOf(after, "t-inv-nochk"), l => Assert.Null(l.PostingRole));
        Assert.Equal("transfer", Header(after, "t-XIn-a-inc").Action);   // the migration did run
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task A_bank_route_row_whose_legs_now_carry_a_security_is_left_alone()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        var securityId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO securities (id, ledger_id, ticker, name, asset_class, is_active, share_decimals)
            VALUES (@S, @L, 'TST', 'Test Security', 'equity', true, 4);
            UPDATE txn_legs SET security_id = @S
             WHERE header_id = (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-XOut-a-exp')
               AND account_id = (SELECT id FROM accounts WHERE ledger_id = @L AND external_id = 'a-brk');
            """, new { L = ledgerId, S = securityId });

        await RunMigrationAsync(conn);
        var after = await SnapshotAsync(conn, ledgerId);

        Assert.Null(Header(after, "t-XOut-a-exp").Action);
        Assert.All(LegsOf(after, "t-XOut-a-exp"), l => Assert.Null(l.PostingRole));
        Assert.Equal("transfer", Header(after, "t-XOut-a-inc").Action);  // the migration did run
    }

    [Fact]
    public async Task Occurrences_fired_from_a_stamped_template_become_transfers_too()
    {
        // A reminder fired before the upgrade cloned its then action-less
        // template (RemindersRepository.FireAsync). Once 260 stamps the
        // template, the occurrences already fired from it are what firing it
        // now would write — unless the user has since changed their shape.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        var fired     = await FireOccurrenceAsync(conn, ledgerId, "rem-brk",       new DateOnly(2026, 2, 1));
        var firedToo  = await FireOccurrenceAsync(conn, ledgerId, "rem-brk",       new DateOnly(2026, 3, 1));
        var reshaped  = await FireOccurrenceAsync(conn, ledgerId, "rem-brk",       new DateOnly(2026, 4, 1));
        var moved     = await FireOccurrenceAsync(conn, ledgerId, "rem-brk",       new DateOnly(2026, 5, 1));
        var classed   = await FireOccurrenceAsync(conn, ledgerId, "rem-brk",       new DateOnly(2026, 6, 1));
        var multi     = await FireOccurrenceAsync(conn, ledgerId, "rem-brk-multi", new DateOnly(2026, 2, 1));
        var multiCut  = await FireOccurrenceAsync(conn, ledgerId, "rem-brk-multi", new DateOnly(2026, 3, 1));
        var bank      = await FireOccurrenceAsync(conn, ledgerId, "rem-bank",      new DateOnly(2026, 2, 1));
        var invShaped = await FireOccurrenceAsync(conn, ledgerId, "rem-brk-inv",   new DateOnly(2026, 2, 1));

        // Two templates the rule does not cover, which the user set to 'transfer'
        // after firing them: one investment-shaped, one with two splits, whose
        // occurrence the user then cut to a single posting. Their templates'
        // action alone must not pull the occurrences along.
        await conn.ExecuteAsync("""
            UPDATE txn_headers SET action = 'transfer'
             WHERE ledger_id = @L AND external_id IN ('mdreminder:rem-brk-inv', 'mdreminder:rem-brk-multi');
            DELETE FROM txn_legs
             WHERE header_id = @MultiCut
               AND posting_index = (SELECT max(posting_index) FROM txn_legs WHERE header_id = @MultiCut);
            """, new { L = ledgerId, MultiCut = multiCut });

        var brokerageId = await AccountIdAsync(conn, ledgerId, "a-brk");
        var otherBrokerageId = await AccountIdAsync(conn, ledgerId, "a-brk2");
        var incomeId = await AccountIdAsync(conn, ledgerId, "a-inc");
        await conn.ExecuteAsync("""
            -- A second posting added to one occurrence.
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT gen_random_uuid(), l.header_id, l.ledger_id,
                   CASE WHEN l.account_id = @Brk THEN @Brk ELSE @Inc END,
                   l.posting_index + 1, l.amount
              FROM txn_legs l WHERE l.header_id = @Reshaped;
            -- Another moved off the template's brokerage.
            UPDATE txn_legs SET account_id = @Brk2 WHERE header_id = @Moved AND account_id = @Brk;
            -- A third classified by the user.
            UPDATE txn_headers SET action = 'misc' WHERE id = @Classed;
            """, new { Brk = brokerageId, Brk2 = otherBrokerageId, Inc = incomeId,
                       Reshaped = reshaped, Moved = moved, Classed = classed });

        await RunMigrationAsync(conn);

        foreach (var id in new[] { fired, firedToo })
        {
            var (action, roles) = await OccurrenceAsync(conn, id);
            Assert.Equal("transfer", action);
            Assert.Equal(2, roles.Count);
            Assert.All(roles, r => Assert.Equal("transfer", r));
        }
        foreach (var id in new[] { reshaped, moved, multi, multiCut, bank, invShaped })
        {
            var (action, roles) = await OccurrenceAsync(conn, id);
            Assert.Null(action);
            Assert.All(roles, r => Assert.Null(r));
        }
        var (classedAction, classedRoles) = await OccurrenceAsync(conn, classed);
        Assert.Equal("misc", classedAction);
        Assert.All(classedRoles, r => Assert.Null(r));

        // The template itself was stamped, and a second run changes nothing.
        Assert.Equal("transfer", Header(await SnapshotAsync(conn, ledgerId), "mdreminder:rem-brk").Action);
        await RunMigrationAsync(conn);
        Assert.Equal("transfer", (await OccurrenceAsync(conn, fired)).Action);
        Assert.Null((await OccurrenceAsync(conn, reshaped)).Action);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task A_row_whose_postings_no_longer_match_the_payload_is_left_alone()
    {
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        var brokerageId = await AccountIdAsync(conn, ledgerId, "a-brk");
        var incomeId    = await AccountIdAsync(conn, ledgerId, "a-inc");

        // A one-split row the user grew to two postings, and a two-split row the
        // user cut to one. Neither is the shape the importer classified. The new
        // posting copies the first one's amounts so it balances the same way.
        await conn.ExecuteAsync("""
            INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount)
            SELECT gen_random_uuid(), l.header_id, l.ledger_id,
                   CASE WHEN l.account_id = @Brk THEN @Brk ELSE @Inc END,
                   l.posting_index + 1, l.amount
              FROM txn_legs l
              JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @L AND h.external_id = 't-XIn-a-exp';
            DELETE FROM txn_legs
             WHERE header_id = (SELECT id FROM txn_headers WHERE ledger_id = @L AND external_id = 't-multi')
               AND posting_index = (SELECT max(posting_index) FROM txn_legs
                                     WHERE header_id = (SELECT id FROM txn_headers
                                                         WHERE ledger_id = @L AND external_id = 't-multi'));
            """, new { L = ledgerId, Brk = brokerageId, Inc = incomeId });

        await RunMigrationAsync(conn);
        var after = await SnapshotAsync(conn, ledgerId);

        foreach (var reshaped in new[] { "t-XIn-a-exp", "t-multi" })
        {
            Assert.Null(Header(after, reshaped).Action);
            Assert.All(LegsOf(after, reshaped), l => Assert.Null(l.PostingRole));
        }
        // The untouched neighbours still converted, so the migration did run.
        Assert.Equal("transfer", Header(after, "t-XIn-a-inc").Action);
        Assert.Equal("transfer", Header(after, "t-XOut-a-exp").Action);
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    [Fact]
    public async Task Malformed_payload_values_are_read_the_way_the_importer_reads_them()
    {
        // Moneydance writes every field as a JSON string. A non-string value is
        // ABSENT to the importer (MdItem.GetString), and must be absent here too —
        // reading it with ->> would see "3" as a txntype or "5" as a check number.
        // None of them may abort the migration either: one odd row must not block
        // the upgrade for everyone.
        await using var conn = Db.OpenConnection();
        var ledgerId = TestLedger.Id;
        await ImportAsync(conn, ledgerId);
        await RewindToOldImporterAsync(conn, ledgerId);

        await RunMigrationAsync(conn);
        var after = await SnapshotAsync(conn, ledgerId);

        // A numeric txntype does not make the row investment-shaped.
        Assert.Equal("transfer", Header(after, "t-bad-txntype").Action);
        // A numeric split account ends the split list, as it ends MD's walk.
        Assert.Equal("transfer", Header(after, "t-bad-split").Action);
        // A numeric chk is no check number.
        Assert.Null(Header(after, "t-bad-chk").CheckNumber);
        // Non-string feed text is no feed text.
        Assert.DoesNotContain(after.Originals, o => o.ExternalId == "t-bad-orig");
    }

    // ---------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------

    private static void AssertFixtureExercisesEveryShape(Snapshot imported)
    {
        foreach (var id in BankRouteTransfers)
        {
            Assert.Equal("transfer", Header(imported, id).Action);
            Assert.All(LegsOf(imported, id), l => Assert.Equal("transfer", l.PostingRole));
        }
        foreach (var id in StaysActionless)
            Assert.Null(Header(imported, id).Action);
        foreach (var (id, chk) in RestoredCheckNumbers)
            Assert.Equal(chk, Header(imported, id).CheckNumber);
        foreach (var id in NoCheckNumber)
            Assert.Null(Header(imported, id).CheckNumber);
        Assert.Equal(SeededOriginals, imported.Originals.Select(o => o.ExternalId));
    }

    /// <summary>
    /// Undo, on the new importer's output, the fields 260 restores (action, posting
    /// role, check number, originals) and nothing else. Memo and tag placement stay
    /// where the new importer put them (see the class remarks).
    /// </summary>
    private static async Task RewindToOldImporterAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        await conn.ExecuteAsync("""
            UPDATE txn_legs SET posting_role = NULL
             WHERE header_id IN (SELECT id FROM txn_headers
                                  WHERE ledger_id = @L AND external_id = ANY(@Transfers));
            UPDATE txn_headers SET action = NULL
             WHERE ledger_id = @L AND external_id = ANY(@Transfers);
            UPDATE txn_headers SET check_number = NULL
             WHERE ledger_id = @L AND external_id = ANY(@CheckNumbers);
            DELETE FROM txn_header_originals WHERE ledger_id = @L;
            """,
            new { L = ledgerId, Transfers = BankRouteTransfers, CheckNumbers = RestoredCheckNumbers.Keys.ToArray() });
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
    }

    private static async Task ImportAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var context = new ImportContext(MdItemReader.ReadString(ExportJson()), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, ImportSource);
        await InvestmentTransactionImportStep.RunAsync(conn, context, ImportSource);
        await ReminderImportStep.RunAsync(conn, context, ImportSource);
        await StoredNotesSeed.ComputeImportedNotesAsync(conn, ledgerId);
    }

    private static string ExportJson()
    {
        var items = new List<string>
        {
            """{"obj_type":"curr","id":"c-usd","currid":"USD","name":"US Dollar","dec":"2","isbase":"1"}""",
            """{"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"}""",
            """{"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"}""",
            """{"obj_type":"acct","id":"a-brk","type":"v","name":"Brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"}""",
            """{"obj_type":"acct","id":"a-brk2","type":"v","name":"Other brokerage","parentid":"a-root","currid":"c-usd","sbal":"0"}""",
            """{"obj_type":"acct","id":"a-inc","type":"i","name":"Interest","parentid":"a-root","currid":"c-usd"}""",
            """{"obj_type":"acct","id":"a-exp","type":"e","name":"Fees","parentid":"a-root","currid":"c-usd"}""",
            """{"obj_type":"acct","id":"a-blank","type":"e","name":"","parentid":"a-root","currid":"c-usd"}""",
        };

        foreach (var word in QifCashWords)
        foreach (var target in CashTargets)
        {
            items.Add($$"""
                {"obj_type":"txn","id":"t-{{word}}-{{target}}","acctid":"a-brk","desc":"Cash move","dt":"20260101",
                 "chk":"{{word}}","qif_invst_action":"{{word}}","xfer_type":"xfrtp_bank",
                 "0.id":"s-{{word}}-{{target}}","0.acctid":"{{target}}","0.invest.splittype":"xfr",
                 "0.samt":"1000","0.pamt":"-1000"}
                """);
        }

        items.AddRange(
        [
            // Bank route, brokerage home, one posting: plain rows with no xfer_type.
            """
            {"obj_type":"txn","id":"t-plain-cat","acctid":"a-brk","desc":"Plain","dt":"20260102",
             "0.id":"s-pc","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-plain-bank","acctid":"a-brk","desc":"Plain","dt":"20260102",
             "0.id":"s-pb","0.acctid":"a-bank","0.samt":"1000","0.pamt":"-1000"}
            """,
            // A self-referencing split is dropped, leaving one posting.
            """
            {"obj_type":"txn","id":"t-self","acctid":"a-brk","desc":"Self","dt":"20260102",
             "0.id":"s-self-0","0.acctid":"a-brk","0.samt":"0","0.pamt":"0",
             "1.id":"s-self-1","1.acctid":"a-exp","1.samt":"1000","1.pamt":"-1000"}
            """,
            // Brokerage, feed payee differs: a transfer AND an originals seed.
            """
            {"obj_type":"txn","id":"t-brk-orig","acctid":"a-brk","desc":"Cash move","dt":"20260102",
             "ol.orig-payee":"CASH MOVE RAW","chk":"XOut","qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
             "0.id":"s-bo","0.acctid":"a-exp","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Two postings: fails closed, no action.
            """
            {"obj_type":"txn","id":"t-multi","acctid":"a-brk","desc":"Split","dt":"20260103",
             "chk":"XOut","qif_invst_action":"XOut","xfer_type":"xfrtp_bank",
             "0.id":"s-m0","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000",
             "1.id":"s-m1","1.acctid":"a-inc","1.samt":"1000","1.pamt":"-1000"}
            """,
            // Bank home: never an investment action, even into a brokerage.
            """
            {"obj_type":"txn","id":"t-bank-xfr","acctid":"a-bank","desc":"To brokerage","dt":"20260103",
             "xfer_type":"xfrtp_bank","0.id":"s-bx","0.acctid":"a-brk","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-bank-plain","acctid":"a-bank","desc":"Shop","dt":"20260103",
             "0.id":"s-bp","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Investment route: the chk is a QIF word, or blank, or missing.
            """
            {"obj_type":"txn","id":"t-inv-xout","acctid":"a-brk","desc":"Sweep","dt":"20260104",
             "chk":"XOut","invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-ix","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Investment-shaped by xfer_type alone, no txntype.
            """
            {"obj_type":"txn","id":"t-inv-intincx","acctid":"a-brk","desc":"Interest","dt":"20260104",
             "chk":"IntIncX","xfer_type":"xfrtp_dividend",
             "0.id":"s-ii0","0.acctid":"a-inc","0.invest.splittype":"inc","0.samt":"-1000","0.pamt":"1000",
             "1.id":"s-ii1","1.acctid":"a-bank","1.invest.splittype":"xfr","1.samt":"1000","1.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-inv-blank","acctid":"a-brk","desc":"Sweep","dt":"20260104",
             "chk":"   ","invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-ib","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            // .NET whitespace beyond ASCII is blank to the importer too.
            """
            {"obj_type":"txn","id":"t-inv-nbsp","acctid":"a-brk","desc":"Sweep","dt":"20260104",
             "chk":" 　","invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-iw","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-inv-nochk","acctid":"a-brk","desc":"Sweep","dt":"20260104",
             "invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-in","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Feed text against curated text.
            """
            {"obj_type":"txn","id":"t-orig-payee","acctid":"a-bank","desc":"Shop","memo":"curated memo",
             "ol.orig-payee":"SHOP RAW","dt":"20260105","td":"20260104","chk":"7",
             "0.id":"s-op","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-orig-memo","acctid":"a-brk","desc":"Sweep","memo":"curated memo",
             "ol.orig-memo":"raw memo","dt":"20260105","chk":"XIn","invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-om","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-orig-same","acctid":"a-bank","desc":"Shop","memo":"curated memo",
             "ol.orig-payee":"Shop","ol.orig-memo":"curated memo","dt":"20260105",
             "0.id":"s-os","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Memos are stored trimmed: a feed memo that differs only by surrounding
            // white space does not differ.
            """
            {"obj_type":"txn","id":"t-orig-trim","acctid":"a-bank","desc":"Shop","memo":"curated memo",
             "ol.orig-memo":" curated memo ","dt":"20260105",
             "0.id":"s-ot","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // A curated memo with surrounding white space: stored trimmed, so a feed
            // memo equal to it trimmed does not differ, and a seed for a differing
            // payee carries it trimmed.
            """
            {"obj_type":"txn","id":"t-old-trim","acctid":"a-bank","desc":"Shop","memo":" kept memo ",
             "ol.orig-memo":"kept memo","dt":"20260105",
             "0.id":"s-oldt","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-old-payee","acctid":"a-bank","desc":"Shop","memo":" kept memo ",
             "ol.orig-payee":"SHOP RAW","dt":"20260105",
             "0.id":"s-oldp","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // A split keeps its memo on the header: that is the memo the feed's is
            // compared with, and the line memos are not.
            """
            {"obj_type":"txn","id":"t-orig-split-same","acctid":"a-bank","desc":"Shop","memo":"split memo",
             "ol.orig-memo":"split memo","dt":"20260105",
             "0.id":"s-oss0","0.acctid":"a-exp","0.desc":"line one","0.samt":"600","0.pamt":"-600",
             "1.id":"s-oss1","1.acctid":"a-inc","1.desc":"line two","1.samt":"400","1.pamt":"-400"}
            """,
            """
            {"obj_type":"txn","id":"t-orig-split-diff","acctid":"a-bank","desc":"Shop","memo":"split memo",
             "ol.orig-memo":"line one","dt":"20260105",
             "0.id":"s-osd0","0.acctid":"a-exp","0.desc":"line one","0.samt":"600","0.pamt":"-600",
             "1.id":"s-osd1","1.acctid":"a-inc","1.desc":"line two","1.samt":"400","1.pamt":"-400"}
            """,
            // Blank curated payee: the feed payee IS what was written.
            """
            {"obj_type":"txn","id":"t-orig-fallback","acctid":"a-bank","desc":"","ol.orig-payee":"SHOP RAW",
             "dt":"20260105","0.id":"s-of","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-orig-blankfeed","acctid":"a-bank","desc":"Shop","ol.orig-payee":"   ",
             "dt":"20260105","0.id":"s-ob","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Non-string values, which the importer reads as absent.
            """
            {"obj_type":"txn","id":"t-bad-txntype","acctid":"a-brk","desc":"Odd","dt":"20260106","invest.txntype":3,
             "0.id":"s-bt","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-bad-split","acctid":"a-brk","desc":"Odd","dt":"20260106",
             "0.id":"s-bs0","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000",
             "1.id":"s-bs1","1.acctid":5,"1.samt":"1000","1.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-bad-chk","acctid":"a-brk","desc":"Sweep","dt":"20260106","chk":5,
             "invest.txntype":"bank","xfer_type":"xfrtp_bank",
             "0.id":"s-bc","0.acctid":"a-bank","0.invest.splittype":"xfr","0.samt":"1000","0.pamt":"-1000"}
            """,
            """
            {"obj_type":"txn","id":"t-bad-orig","acctid":"a-bank","desc":"Shop","dt":"20260106",
             "ol.orig-payee":12,"ol.orig-memo":{"x":"y"},
             "0.id":"s-bo2","0.acctid":"a-exp","0.samt":"1000","0.pamt":"-1000"}
            """,
            // Reminder templates: the same rule, read from the series' payload.
            """
            {"obj_type":"reminder","id":"rem-brk","desc":"Recurring cash","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
             "is_loan_reminder":"0","txn.acctid":"a-brk","txn.desc":"Recurring cash",
             "txn.0.acctid":"a-exp","txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """,
            """
            {"obj_type":"reminder","id":"rem-bank","desc":"Recurring bill","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
             "is_loan_reminder":"0","txn.acctid":"a-bank","txn.desc":"Recurring bill",
             "txn.0.acctid":"a-exp","txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """,
            """
            {"obj_type":"reminder","id":"rem-brk-multi","desc":"Recurring split","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
             "is_loan_reminder":"0","txn.acctid":"a-brk","txn.desc":"Recurring split",
             "txn.0.acctid":"a-exp","txn.0.samt":"1000","txn.0.pamt":"-1000",
             "txn.1.acctid":"a-inc","txn.1.samt":"1000","txn.1.pamt":"-1000"}
            """,
            // Investment-shaped (a txntype; an investment xfer_type): the bank
            // route would never see the txn, so the template gets no action.
            """
            {"obj_type":"reminder","id":"rem-brk-inv","desc":"Recurring sweep","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
             "is_loan_reminder":"0","txn.acctid":"a-brk","txn.desc":"Recurring sweep","txn.invest.txntype":"bank",
             "txn.0.acctid":"a-bank","txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """,
            """
            {"obj_type":"reminder","id":"rem-brk-div","desc":"Recurring income","type":"monthly","sdt":"20260101",
             "monthlydays":"1","monthlymod":"1","daily":"0","weeklydays":"","yearly":"0","acdays":"-1",
             "is_loan_reminder":"0","txn.acctid":"a-brk","txn.desc":"Recurring income","txn.xfer_type":"xfrtp_dividend",
             "txn.0.acctid":"a-inc","txn.0.samt":"1000","txn.0.pamt":"-1000"}
            """,
        ]);

        return $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
            {{string.Join(",\n", items)}}
              ]
            }
            """;
    }

    // ---------------------------------------------------------------------------
    // Migration + snapshots
    // ---------------------------------------------------------------------------

    private static string MigrationSql()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName, "db", "migrations",
                "260_backfill_md_brokerage_transfers_check_numbers_and_originals.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate migration 260.");
    }

    /// <summary>
    /// Migration 260, then the stored memo-and-tags backfill an upgrade runs after it:
    /// 260 changes the rule's inputs (the action), and the upgrade's later step fills
    /// every leg from what 260 left.
    /// </summary>
    private static async Task RunMigrationAsync(NpgsqlConnection conn)
    {
        await conn.ExecuteAsync(MigrationSql());
        await Coffer.Api.Migrations.LegNotesBackfill.RunAsync(conn, ledgerId: null, CancellationToken.None);
    }

    private sealed record HeaderSnap(
        string ExternalId, string? Action, string? CheckNumber, string? Payee, string? Memo);

    private sealed record LegSnap(
        string ExternalId, int PostingIndex, Guid AccountId, string? PostingRole);

    private sealed record OriginalSnap(
        string ExternalId, Guid LedgerId, string? Payee, string? Memo,
        DateTime? PostedAt, DateTime? TransactedAt, string? CheckNumber, DateTime? CapturedAt);

    private sealed record Snapshot(
        IReadOnlyList<HeaderSnap> Headers, IReadOnlyList<LegSnap> Legs, IReadOnlyList<OriginalSnap> Originals);

    /// <summary>Each originals row's amount and rounded-at-import flag, beside the sum of
    /// its header's legs on the originating account.</summary>
    private static async Task<List<(decimal? Amount, bool RoundedAtImport, decimal? HomeLegSum)>> OriginalAmountsAsync(
        NpgsqlConnection conn, Guid ledgerId) =>
        (await conn.QueryAsync<(decimal? Amount, bool RoundedAtImport, decimal? HomeLegSum)>("""
            SELECT o.amount, o.rounded_at_import,
                   (SELECT sum(l.amount) FROM txn_legs l
                     WHERE l.header_id = h.id AND l.account_id = h.originating_account_id)
              FROM txn_header_originals o JOIN txn_headers h ON h.id = o.header_id
             WHERE h.ledger_id = @L;
            """, new { L = ledgerId })).ToList();

    /// <summary>Every column the migration may write, less originals.captured_at. The
    /// originals' amount and rounded_at_import are not among them: 260 writes neither
    /// (ADR-0114 D15).</summary>
    private static async Task<Snapshot> SnapshotAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var full = await FullSnapshotAsync(conn, ledgerId);
        return full with { Originals = [.. full.Originals.Select(o => o with { CapturedAt = null })] };
    }

    /// <summary>Every column the migration may write.</summary>
    private static async Task<Snapshot> FullSnapshotAsync(NpgsqlConnection conn, Guid ledgerId)
    {
        var headers = await conn.QueryAsync<HeaderSnap>("""
            SELECT external_id AS ExternalId, action AS Action, check_number AS CheckNumber,
                   payee AS Payee, memo AS Memo
              FROM txn_headers WHERE ledger_id = @L
             ORDER BY external_id;
            """, new { L = ledgerId });
        var legs = await conn.QueryAsync<LegSnap>("""
            SELECT h.external_id AS ExternalId, l.posting_index AS PostingIndex,
                   l.account_id AS AccountId, l.posting_role AS PostingRole
              FROM txn_legs l JOIN txn_headers h ON h.id = l.header_id
             WHERE h.ledger_id = @L
             ORDER BY h.external_id, l.posting_index, l.account_id;
            """, new { L = ledgerId });
        var originals = await conn.QueryAsync<OriginalSnap>("""
            SELECT h.external_id AS ExternalId, o.ledger_id AS LedgerId, o.payee AS Payee, o.memo AS Memo,
                   o.posted_at AS PostedAt, o.transacted_at AS TransactedAt,
                   o.check_number AS CheckNumber, o.captured_at AS CapturedAt
              FROM txn_header_originals o JOIN txn_headers h ON h.id = o.header_id
             WHERE h.ledger_id = @L
             ORDER BY h.external_id;
            """, new { L = ledgerId });
        return new Snapshot([.. headers], [.. legs], [.. originals]);
    }

    /// <summary>
    /// A first edit with the originals capture the per-side writers make: snapshot
    /// the whole row into the sidecar once per header, then change the canonical
    /// row with <paramref name="update"/>, which this scopes to
    /// <paramref name="externalIds"/>. The memo captured is the transaction's
    /// wherever it sits: the originating account's leg of one posting, the header
    /// of several. Until the API's writers move to the per-side model, its capture
    /// still reads the header memo; this models the capture they move to.
    /// </summary>
    private static Task FirstEditAsync(
        NpgsqlConnection conn, Guid ledgerId, string[] externalIds, string update) =>
        conn.ExecuteAsync($"""
            INSERT INTO txn_header_originals
                (header_id, ledger_id, payee, memo, posted_at, transacted_at, check_number)
            SELECT h.id, h.ledger_id, h.payee,
                   CASE WHEN (SELECT count(DISTINCT l.posting_index) FROM txn_legs l WHERE l.header_id = h.id) = 1
                        THEN (SELECT l.leg_memo FROM txn_legs l
                               WHERE l.header_id = h.id AND l.account_id = h.originating_account_id)
                        ELSE h.memo END,
                   h.posted_at, h.transacted_at, h.check_number
              FROM txn_headers h
             WHERE h.ledger_id = @L AND h.external_id = ANY(@Ids)
            ON CONFLICT (header_id) DO NOTHING;
            {update} WHERE ledger_id = @L AND external_id = ANY(@Ids);
            """, new { L = ledgerId, Ids = externalIds });

    /// <summary>
    /// Fire one occurrence of the series imported from Moneydance reminder
    /// <paramref name="reminderMdId"/> the way <c>RemindersRepository.FireAsync</c>
    /// writes it: the template's header and legs cloned into a live, committed
    /// header stamped with the series and the occurrence date. Raw SQL mirroring
    /// FireAsync's columns, as the API cannot run in this test project; the same
    /// stand-in <see cref="FirstEditAsync"/> is for CaptureAsync.
    /// </summary>
    private static async Task<Guid> FireOccurrenceAsync(
        NpgsqlConnection conn, Guid ledgerId, string reminderMdId, DateOnly occurrenceDate)
    {
        var headerId = Guid.NewGuid();
        var postedAt = new DateTime(occurrenceDate, TimeOnly.MinValue, DateTimeKind.Utc);
        await conn.ExecuteAsync("""
            INSERT INTO txn_headers
                (id, ledger_id, origin, external_id, payee, memo, posted_at, transacted_at,
                 check_number, is_pending, is_hidden, is_merged_into, action,
                 is_recurring_template, recurring_transaction_id, occurrence_date,
                 originating_account_id)
            SELECT @Id, rt.ledger_id, 'manual', NULL, t.payee, t.memo, @PostedAt, @PostedAt,
                   t.check_number, FALSE, FALSE, NULL, t.action,
                   FALSE, rt.id, @OccurrenceDate,
                   t.originating_account_id
              FROM recurring_transactions rt
              JOIN txn_headers t ON t.id = rt.template_header_id
             WHERE rt.ledger_id = @L AND rt.external_id = @Rem;
            INSERT INTO txn_legs
                (id, header_id, ledger_id, account_id, posting_index, leg_memo, amount,
                 security_id, quantity, unit_price, posting_role)
            SELECT gen_random_uuid(), @Id, l.ledger_id, l.account_id, l.posting_index, l.leg_memo, l.amount,
                   l.security_id, l.quantity, l.unit_price, l.posting_role
              FROM recurring_transactions rt
              JOIN txn_legs l ON l.header_id = rt.template_header_id
             WHERE rt.ledger_id = @L AND rt.external_id = @Rem;
            """, new { Id = headerId, L = ledgerId, Rem = reminderMdId, PostedAt = postedAt, OccurrenceDate = occurrenceDate });
        await StoredNotesSeed.BackfillNotesAsync(conn, ledgerId);
        return headerId;
    }

    private static async Task<(string? Action, IReadOnlyList<string?> Roles)> OccurrenceAsync(
        NpgsqlConnection conn, Guid headerId)
    {
        var action = await conn.QuerySingleAsync<string?>(
            "SELECT action FROM txn_headers WHERE id = @H AND NOT is_recurring_template;", new { H = headerId });
        var roles = (await conn.QueryAsync<string?>(
            "SELECT posting_role FROM txn_legs WHERE header_id = @H ORDER BY posting_index, account_id;",
            new { H = headerId })).ToList();
        Assert.NotEmpty(roles);
        return (action, roles);
    }

    private static Task<Guid> AccountIdAsync(NpgsqlConnection conn, Guid ledgerId, string mdId) =>
        conn.QuerySingleAsync<Guid>(
            "SELECT id FROM accounts WHERE ledger_id = @L AND external_id = @X;",
            new { L = ledgerId, X = mdId });


    private static HeaderSnap Header(Snapshot snapshot, string externalId) =>
        snapshot.Headers.Single(h => h.ExternalId == externalId);

    private static IReadOnlyList<LegSnap> LegsOf(Snapshot snapshot, string externalId)
    {
        var legs = snapshot.Legs.Where(l => l.ExternalId == externalId).ToList();
        Assert.NotEmpty(legs);
        return legs;
    }
}
