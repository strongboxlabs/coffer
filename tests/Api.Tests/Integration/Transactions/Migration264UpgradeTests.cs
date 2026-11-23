using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Migrations;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Migration 264's data steps on an install as an earlier release left it: staged at
/// 259, seeded with the memos and tags that release's writers stored, then upgraded
/// through MigrationRunner (260 to 266, the stored-notes backfill step after 264) the
/// way an install upgrades.
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> Every register shows what it showed before, except the
/// display changes ADR-0108 D16 accepts, each named at its fixture: blank memos become
/// NULL, a one-split's own leg values start showing on the other side, a split's line
/// seen from another account stops falling back to the transaction, case variants
/// merge, and a memo no side showed is joined onto the originating side. The upgraded
/// rows are read twice: as stored (the header, each leg and their tag links) and as
/// the registers read them (the stored presentation), which must also be what the rule
/// computes (stored == computed).</para>
/// <para><b>The seeds are SQL</b>, standing in for what the earlier release's writers
/// stored: the header memo on every transaction, a side's own memo on its leg, header
/// tags, a tag on one posting, an investment event's memo copied onto its legs. That is
/// the accepted exception to the no-raw-writes rule, and it holds because that release
/// could reach those states. Moneydance rows are the vintage tests' (Importer tests).</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class Migration264UpgradeTests : DatabaseTest
{
    public Migration264UpgradeTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string FirstStaged = "260_backfill_md_brokerage_transfers_check_numbers_and_originals.sql";
    private const string Mig264 = "264_annotation_data.sql";

    private static readonly string[] Upgrade =
    [
        FirstStaged,
        "261_drop_account_folders_cycle_trigger.sql",
        "262_reissue_stored_text_without_dataset_figures.sql",
        "263_originating_account_tag_name_key_and_saved_report_repoint.sql",
        Mig264,
        "265_oauth_connection_meta.sql",
        "266_drop_view_notes_columns_and_register_functions.sql",
    ];

    private static Guid Id(int n) => new($"26410000-0000-0000-0000-{n:D12}");

    private static readonly Guid Ledger = Id(1);
    private static readonly Guid OtherLedger = Id(2);
    private static readonly Guid Security = Id(3);
    private static readonly Guid UserA = Id(4);
    private static readonly Guid UserB = Id(5);
    private static readonly Guid BankA = Id(10);
    private static readonly Guid BankB = Id(11);
    private static readonly Guid CatA = Id(20);
    private static readonly Guid CatB = Id(21);
    private static readonly Guid Fee = Id(22);
    private static readonly Guid BrokP = Id(30);
    private static readonly Guid HoldP = Id(31);
    private static readonly Guid BrokQ = Id(32);
    private static readonly Guid HoldQ = Id(33);
    private static readonly Guid OtherCat = Id(40);

    // Tags. Work is the earliest of its key; work and WORK are its case variants.
    private static readonly Guid TagWork = Id(50);
    private static readonly Guid TagWorkLower = Id(51);
    private static readonly Guid TagWorkUpper = Id(52);
    private static readonly Guid TagTrip = Id(53);
    private static readonly Guid TagHome = Id(54);
    private static readonly Guid TagProj = Id(55);
    private static readonly Guid OtherLedgerWork = Id(56);

    private (string Cs, string Work) InstallBefore260(string dbName)
    {
        var work = Directory.CreateTempSubdirectory("coffer-mig264-data-").FullName;
        var cs = Db.EmptyDatabaseConnectionString(dbName);
        UpgradeRehearsal.RunStaged(cs, UpgradeRehearsal.StageBefore(FirstStaged, work));
        return (cs, work);
    }

    private static void UpgradeThrough(string cs, string work, string last)
    {
        foreach (var script in Upgrade)
        {
            UpgradeRehearsal.Apply(cs, work, script);
            if (script == last) return;
        }
    }

    [Fact]
    public async Task Every_register_keeps_what_it_showed_and_the_stored_values_are_the_rules()
    {
        var (cs, work) = InstallBefore260("mig264_native");
        await SeedAsync(cs);

        UpgradeThrough(cs, work, Upgrade[^1]);

        var journal = await JournalAsync(cs);
        Assert.Equal(LegNotesBackfill.Name, journal[journal.IndexOf(Mig264) + 1]);

        var wrong = new List<string>();
        foreach (var e in Expected)
        {
            var actual = await LegStateAsync(cs, e.Header, e.Account);
            if (actual != e.State) wrong.Add($"{e.Name}: expected {e.State}, stored {actual}");
        }
        foreach (var (name, header, memo, tags) in ExpectedHeaders)
        {
            var actual = await HeaderStateAsync(cs, header);
            if (actual != (memo, tags)) wrong.Add($"{name} (header): expected {(memo, tags)}, stored {actual}");
        }
        Assert.True(wrong.Count == 0, "264 left these rows other than every register showed:\n  " + string.Join("\n  ", wrong));

        // What the registers read is what the rule computes from the rows 264 left.
        await using (var db = Context(cs))
        {
            Assert.Empty(await LegNotes.DifferencesAsync(db, ledgerId: null, CancellationToken.None));
            Assert.Equal(await db.TxnLegs.CountAsync(), await db.TxnLegs.CountAsync(l => l.NotesKind != null));
        }
    }

    [Fact]
    public async Task Every_register_shows_what_the_earlier_release_showed_but_the_intended_changes()
    {
        var (cs, work) = InstallBefore260("mig264_display");
        await SeedAsync(cs);
        // The earlier release's read, on the database as that release left it.
        var before = await RegisterDisplay.BeforeAsync(cs, Ledger);

        UpgradeThrough(cs, work, Upgrade[^1]);

        var after = await RegisterDisplay.AfterAsync(cs, Ledger);
        Assert.True(before.Count > 30, $"the baseline read only {before.Count} register rows");
        AssertOnlyIntendedChanges(RegisterDisplay.Differences(before, after), IntendedDisplayChanges);
    }

    /// <summary>
    /// The register rows' display changes are exactly <paramref name="intended"/>: each
    /// one seen, nothing else.
    /// </summary>
    internal static void AssertOnlyIntendedChanges(
        IReadOnlySet<string> differences, IReadOnlyList<(string Change, string Reason)> intended)
    {
        var expected = intended.Select(c => c.Change).ToHashSet(StringComparer.Ordinal);
        var unexpected = differences.Where(d => !expected.Contains(d)).ToList();
        var missing = intended.Where(c => !differences.Contains(c.Change)).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Register display after 264 against the earlier release's:"
            + string.Concat(unexpected.Select(u => "\n  unexpected:            " + u))
            + string.Concat(missing.Select(m => $"\n  intended, not seen:    {m.Change} ({m.Reason})")));
    }

    /// <summary>
    /// Every register row whose display 264 changes, with the decision that changes it;
    /// any other difference fails. A reason is a design decision, or the migration step
    /// that applies one.
    /// </summary>
    private static readonly (string Change, string Reason)[] IntendedDisplayChanges =
    [
        // Both sides held their own memo and the header's was shown nowhere: it is
        // joined onto the entered side.
        ($"{Id(103)} Account A row: 'x4' [] → 'h4 · x4' []", "ADR-0108 D16, a memo no side showed is kept"),
        // A blank memo is no memo, so the row shows what it inherits.
        ($"{Id(105)} Account A row: '   ' [] → 'h6' []", "ADR-0108 D16, a blank memo no longer hides one"),
        ($"{Id(114)} Account A parent: ' \t ' [] → — []", "ADR-0108 D16, a blank memo no longer hides one"),
        ($"{Id(114)} Category A line0: ' \t ' [] → — []", "ADR-0108 D16, a blank memo no longer hides one; split lines stop falling back"),
        ($"{Id(114)} Category B line1: ' \t ' [] → — []", "ADR-0108 D16, a blank memo no longer hides one; split lines stop falling back"),
        // A one-split's own leg values, hidden behind the header before, now show.
        ($"{Id(107)} Account A row: — [trip] → — [home,trip]", "ADR-0108 D16, a one-split's own leg values now show"),
        ($"{Id(108)} Category A row: — [] → 'x9' []", "ADR-0108 D16, a one-split's own leg values now show"),
        // A split's line seen from another account no longer falls back to the
        // transaction's memo and tags; it shows its own, else its line's other leg's.
        ($"{Id(109)} Category A line0: 'whole' [trip] → 'line0' []", "ADR-0108 D16, split lines stop falling back"),
        ($"{Id(109)} Category B line1: 'whole' [trip] → — []", "ADR-0108 D16, split lines stop falling back"),
        ($"{Id(117)} Category A line0: — [WORK,Work,trip,work] → — []", "ADR-0108 D16, split lines stop falling back; case variants merge"),
        ($"{Id(117)} Category B line1: — [WORK,Work,trip,work] → — []", "ADR-0108 D16, split lines stop falling back; case variants merge"),
        // Case variants merge into the earliest created, the spelling every row now shows.
        ($"{Id(116)} Account A row: — [work] → — [Work]", "ADR-0108 D16, case variants merge"),
        ($"{Id(116)} Category A row: — [work] → — [Work]", "ADR-0108 D16, case variants merge"),
        ($"{Id(117)} Account A parent: — [WORK,Work,trip,work] → — [Work,trip]", "ADR-0108 D16, case variants merge"),
        // Step D: an event keeps one memo and one tag set, on its header. Every distinct
        // memo on its investment side joins the header's with " · ", in posting order, a
        // repeat once; the tags are their union. Every row of the event shows that value.
        ($"{Id(111)} Brokerage P event: 'a' [] → 'a · b' [proj]", "step D"),
        ($"{Id(111)} Category C line1: — [] → 'a · b' [proj]", "step D"),
        ($"{Id(119)} Brokerage P event: 'p' [] → 'q · p' []", "step D"),
        ($"{Id(119)} Brokerage Q event: 'q' [] → 'q · p' []", "step D"),
    ];

    [Fact]
    public async Task Case_variants_merge_into_the_earliest_and_saved_reports_follow()
    {
        var (cs, work) = InstallBefore260("mig264_variants");
        await SeedAsync(cs);
        var untouched = await UpgradeRehearsal.DigestAsync(cs,
            $"SELECT id, spec::text, updated_at FROM saved_reports WHERE id NOT IN ('{Id(400)}', '{Id(401)}', '{Id(402)}') ORDER BY id");
        var stamps = await UpgradeRehearsal.DigestAsync(cs, "SELECT id, updated_at FROM saved_reports ORDER BY id");

        UpgradeThrough(cs, work, Upgrade[^1]);

        // One tag per key: the earliest created survives; the variants are gone, and the
        // other ledger's tag of the same name is its own.
        Assert.Equal(
            ["Work", "home", "proj", "trip"],
            await StringsAsync(cs, $"SELECT name FROM tags WHERE ledger_id = '{Ledger}' ORDER BY name COLLATE \"C\""));
        Assert.Equal(["work"], await StringsAsync(cs, $"SELECT name FROM tags WHERE ledger_id = '{OtherLedger}'"));
        // Every transaction keeps the tag: the one-split that had only a variant, and the
        // split that had two variants and the survivor once each, now once.
        Assert.Equal(["Work"], await LegTagNamesAsync(cs, Id(116), BankA));
        Assert.Equal(["Work", "trip"], await HeaderTagNamesAsync(cs, Id(117)));

        // Reports on a variant (one private, one shared, one spelling the uuid in upper
        // case) name the survivor; every other report is byte for byte as it was, and no
        // report's updated_at moved.
        foreach (var report in new[] { Id(400), Id(401), Id(402) })
            Assert.Equal(TagWork.ToString(), await ScalarAsync(cs, $"SELECT spec ->> 'tagId' FROM saved_reports WHERE id = '{report}'"));
        Assert.Equal(untouched, await UpgradeRehearsal.DigestAsync(cs,
            $"SELECT id, spec::text, updated_at FROM saved_reports WHERE id NOT IN ('{Id(400)}', '{Id(401)}', '{Id(402)}') ORDER BY id"));
        Assert.Equal(stamps, await UpgradeRehearsal.DigestAsync(cs, "SELECT id, updated_at FROM saved_reports ORDER BY id"));

        // And a variant can no longer be created.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecAsync(cs, $"INSERT INTO tags (id, ledger_id, name) VALUES ('{Id(59)}', '{Ledger}', ' TRIP ')"));
        Assert.Equal("uq_tags_name_key_per_ledger", refused.ConstraintName);
    }

    [Fact]
    public async Task A_rerun_over_its_own_result_changes_nothing()
    {
        var (cs, work) = InstallBefore260("mig264_data_rerun");
        await SeedAsync(cs);
        UpgradeThrough(cs, work, "263_originating_account_tag_name_key_and_saved_report_repoint.sql");

        const string everything = """
            SELECT 'h', h.id::text, h.memo, h.originating_account_id::text, NULL, NULL FROM txn_headers h
            UNION ALL SELECT 'l', l.id::text, l.leg_memo, l.account_id::text, l.posting_index::text, NULL FROM txn_legs l
            UNION ALL SELECT 't', t.id::text, t.name, t.created_at::text, NULL, NULL FROM tags t
            UNION ALL SELECT 'ht', ht.header_id::text, ht.tag_id::text, ht.created_at::text, NULL, NULL FROM txn_header_tags ht
            UNION ALL SELECT 'lt', lt.leg_id::text, lt.tag_id::text, lt.created_at::text, NULL, NULL FROM txn_leg_tags lt
            UNION ALL SELECT 'r', r.id::text, r.spec::text, r.updated_at::text, NULL, NULL FROM saved_reports r
            ORDER BY 1, 2, 3, 4
            """;
        await UpgradeRehearsal.ApplyAndProveRerunnableAsync(cs, work, Mig264);
        var once = await UpgradeRehearsal.DigestAsync(cs, everything);

        // A third run over what the first two left.
        await ExecAsync(cs, $"DELETE FROM public.__schema_migrations WHERE scriptname LIKE '%{Mig264}'");
        UpgradeRehearsal.RunStaged(cs, work);
        Assert.Equal(once, await UpgradeRehearsal.DigestAsync(cs, everything));
    }

    [Fact]
    public async Task A_broken_invariant_fails_the_upgrade_and_leaves_everything_as_it_was()
    {
        var (cs, work) = InstallBefore260("mig264_invariant");
        await SeedAsync(cs);
        UpgradeThrough(cs, work, "263_originating_account_tag_name_key_and_saved_report_repoint.sql");

        // An event owned by its holdings account rather than its brokerage: no writer
        // leaves it, and 263 refuses it on its own run, so it is set after 263 as a later
        // writer bug would. 264 must refuse to finish over it.
        await ExecAsync(cs, $"UPDATE txn_headers SET originating_account_id = '{HoldP}' WHERE id = '{Id(110)}'");
        const string everything = """
            SELECT h.id, h.memo FROM txn_headers h
            UNION ALL SELECT l.id, l.leg_memo FROM txn_legs l
            ORDER BY 1
            """;
        var before = await UpgradeRehearsal.DigestAsync(cs, everything);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => UpgradeRehearsal.Apply(cs, work, Mig264)));
        Assert.Contains("(d) actioned event not owned by a brokerage on every posting: 1", Flatten(ex), StringComparison.Ordinal);

        Assert.Equal(before, await UpgradeRehearsal.DigestAsync(cs, everything));
        Assert.DoesNotContain(Mig264, await JournalAsync(cs));
        Assert.Null(await ScalarAsync(cs,
            "SELECT 1 FROM pg_attribute WHERE attrelid = 'txn_legs'::regclass AND attname = 'notes_kind'"));
    }

    [Theory]
    // A one-split owned by an account none of its legs is on: step C has no side to move
    // the header's memo to, so a one-posting header keeps a memo.
    [InlineData(101, "Category B", "(a) one-posting header with a memo or tags: 1;", "(c) originating account not on a leg, or NULL with legs: 1;")]
    // An in-kind transfer owned by its destination.
    [InlineData(119, "Brokerage P", "(e) in-kind transfer not owned by its source brokerage: 1;", null)]
    // A split owned by a category on one line only, though the bank is on every line.
    [InlineData(109, "Category A", "(f) multi-split not owned by an account on every line: 1;", null)]
    public async Task Each_invariant_a_writer_can_break_fails_the_upgrade_by_its_own_clause(
        int header, string owner, string clause, string? alsoClause)
    {
        // Set after 263, which refuses each of them on its own run, as a later writer
        // defect would leave them. Clauses (b), (g), (h) and (i) are states the steps
        // themselves always clear (each step's mutation fails them).
        var (cs, work) = InstallBefore260($"mig264_inv_{header}");
        await SeedAsync(cs);
        UpgradeThrough(cs, work, "263_originating_account_tag_name_key_and_saved_report_repoint.sql");
        var account = (Guid)(await ScalarAsync(cs, $"SELECT id FROM accounts WHERE name = '{owner}'"))!;
        await ExecAsync(cs, $"UPDATE txn_headers SET originating_account_id = '{account}' WHERE id = '{Id(header)}'");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(() => UpgradeRehearsal.Apply(cs, work, Mig264)));
        Assert.Contains(clause, Flatten(ex), StringComparison.Ordinal);
        if (alsoClause is not null) Assert.Contains(alsoClause, Flatten(ex), StringComparison.Ordinal);
        Assert.DoesNotContain(Mig264, await JournalAsync(cs));
    }

    // ----- expectations -----

    /// <summary>A leg as stored and as its register shows it.</summary>
    private sealed record LegState(
        string? LegMemo, string LegTags, string? ShownMemo, bool? Inherited, string ShownTags);

    private static readonly (string Name, Guid Header, Guid Account, LegState State)[] Expected =
    [
        // A one-split's memo on the header: the originating side takes it as its own, the
        // other side inherits it.
        ("header memo only, entered side", Id(100), BankA, new("h1", "", "h1", false, "")),
        ("header memo only, other side",   Id(100), CatA,  new(null, "", "h1", true, "")),
        // The entered side's own memo kept; the other side keeps showing the header's.
        ("entered side's own, entered",    Id(101), BankA, new("x2", "", "x2", false, "")),
        ("entered side's own, other",      Id(101), CatA,  new("h2", "", "h2", false, "")),
        ("other side's own, entered",      Id(102), BankA, new("h3", "", "h3", false, "")),
        ("other side's own, other",        Id(102), CatA,  new("y3", "", "y3", false, "")),
        // Both sides' own and a third header memo neither showed: joined on the entered side (ADR-0108 D16).
        ("invisible header memo, entered", Id(103), BankA, new("h4 · x4", "", "h4 · x4", false, "")),
        ("invisible header memo, other",   Id(103), CatA,  new("y4", "", "y4", false, "")),
        ("header memo equal to own",       Id(104), BankA, new("same", "", "same", false, "")),
        ("header memo equal to own, other", Id(104), CatA, new("y5", "", "y5", false, "")),
        // A blank leg memo hid the header's; it is NULL now and the header's shows (ADR-0108 D16).
        ("blank leg memo, entered",        Id(105), BankA, new("h6", "", "h6", false, "")),
        ("blank leg memo, other",          Id(105), CatA,  new(null, "", "h6", true, "")),
        // Header tags: onto the entered side; the other side inherits them.
        ("header tags, entered",           Id(106), BankA, new(null, "trip", null, false, "trip")),
        ("header tags, other",             Id(106), CatA,  new(null, "", null, false, "trip")),
        // Header tags beside a leg's own: both sides keep the header's; the leg's own,
        // hidden before, now shows (ADR-0108 D16).
        ("header and leg tags, entered",   Id(107), BankA, new(null, "home,trip", null, false, "home,trip")),
        ("header and leg tags, other",     Id(107), CatA,  new(null, "trip", null, false, "trip")),
        // Nothing on the header: untouched; the other side now inherits the entered
        // side's memo (ADR-0108 D16).
        ("empty header, entered",          Id(108), BankA, new("x9", "", "x9", false, "")),
        ("empty header, other",            Id(108), CatA,  new(null, "", "x9", true, "")),
        // A split keeps its transaction memo and tags and its lines' own; a split-side
        // line shows its own tags (the transaction's count for it, on the row).
        ("split line, split side",         Id(109), BankA, new("line0", "", "line0", false, "")),
        // A buy with a fee whose memo the investment editor copied onto every leg: the
        // investment side's copies go, a repeat of the header's memo joining it once; the
        // fee's counterpart keeps its own.
        ("event copy, brokerage",          Id(110), BrokP, new(null, "", "buy", false, "")),
        ("event copy, holdings",           Id(110), HoldP, new(null, "", "buy", false, "")),
        ("event copy, fee",                Id(110), Fee,   new("buy", "", "buy", false, "")),
        // An event whose values sat on its brokerage and holdings legs only: they join the header.
        ("event legs, brokerage",          Id(111), BrokP, new(null, "", "a · b", false, "proj")),
        ("event legs, holdings",           Id(111), HoldP, new(null, "", "a · b", false, "proj")),
        // A one-posting cash transfer: the brokerage holds it, the bank inherits.
        ("one-posting event, brokerage",   Id(112), BrokP, new("x13", "trip", "x13", false, "trip")),
        ("one-posting event, bank",        Id(112), BankA, new(null, "", "x13", true, "trip")),
        // A reminder template: the same move as a live one-split.
        ("template, source",               Id(113), BankA, new("rent", "", "rent", false, "")),
        ("template, other",                Id(113), CatA,  new(null, "", "rent", true, "")),
        // The funding brokerage of a buy is not the event's investment side: its copy stays.
        ("funded buy, funding leg",        Id(118), BrokQ, new("bx", "", "bx", false, "")),
        ("funded buy, brokerage",          Id(118), BrokP, new(null, "", "bx", false, "")),
        // An in-kind transfer's memos on both brokerages join the header.
        ("in-kind transfer, source",       Id(119), BrokQ, new(null, "", "q · p", false, "")),
        ("in-kind transfer, destination",  Id(119), BrokP, new(null, "", "q · p", false, "")),
        // A one-split that carried only a case variant now carries the survivor.
        ("variant only, entered",          Id(116), BankA, new(null, "Work", null, false, "Work")),
    ];

    private static readonly (string Name, Guid Header, string? Memo, string Tags)[] ExpectedHeaders =
    [
        ("header memo only", Id(100), null, ""),
        ("invisible header memo", Id(103), null, ""),
        ("header tags", Id(106), null, ""),
        ("split", Id(109), "whole", "trip"),
        ("event copy", Id(110), "buy", ""),
        ("event legs", Id(111), "a · b", "proj"),
        ("one-posting event", Id(112), null, ""),
        ("template", Id(113), null, ""),
        // A blank memo on a split's header is NULL.
        ("blank split header", Id(114), null, ""),
        // A template with no legs keeps its memo: nothing else can show it.
        ("template with no legs", Id(115), "legless", ""),
        ("two variants and the survivor", Id(117), null, "Work,trip"),
        ("funded buy", Id(118), "bx", ""),
        // Both brokerages' memos join the header, source first.
        ("in-kind transfer", Id(119), "q · p", ""),
    ];

    // ----- seeds -----

    private static async Task SeedAsync(string cs)
    {
        await ExecAsync(cs, $"""
            INSERT INTO users (id, display_name, username, created_by)
            VALUES ('{UserA}', 'User A', 'user-a', 'test'), ('{UserB}', 'User B', 'user-b', 'test');
            INSERT INTO ledgers (id, name) VALUES ('{Ledger}', 'Ledger A'), ('{OtherLedger}', 'Ledger B');
            INSERT INTO accounts (id, ledger_id, name, account_type, category_kind, currency_code, is_system)
            VALUES ('{BankA}',    '{Ledger}', 'Account A',  'bank',       NULL,      'USD', FALSE),
                   ('{BankB}',    '{Ledger}', 'Account B',  'bank',       NULL,      'USD', FALSE),
                   ('{CatA}',     '{Ledger}', 'Category A', 'category',   'expense', 'USD', FALSE),
                   ('{CatB}',     '{Ledger}', 'Category B', 'category',   'expense', 'USD', FALSE),
                   ('{Fee}',      '{Ledger}', 'Category C', 'category',   'expense', 'USD', FALSE),
                   ('{HoldP}',    '{Ledger}', 'Holdings P', 'investment', NULL,      'USD', TRUE),
                   ('{HoldQ}',    '{Ledger}', 'Holdings Q', 'investment', NULL,      'USD', TRUE),
                   ('{OtherCat}', '{OtherLedger}', 'Category D', 'category', 'expense', 'USD', FALSE);
            INSERT INTO accounts (id, ledger_id, name, account_type, currency_code, holdings_account_id)
            VALUES ('{BrokP}', '{Ledger}', 'Brokerage P', 'investment', 'USD', '{HoldP}'),
                   ('{BrokQ}', '{Ledger}', 'Brokerage Q', 'investment', 'USD', '{HoldQ}');
            INSERT INTO securities (id, ledger_id, name) VALUES ('{Security}', '{Ledger}', 'Security A');
            INSERT INTO tags (id, ledger_id, name, created_at)
            VALUES ('{TagWork}',      '{Ledger}', 'Work', '2026-01-01'),
                   ('{TagWorkLower}', '{Ledger}', 'work', '2026-02-01'),
                   ('{TagWorkUpper}', '{Ledger}', 'WORK', '2026-03-01'),
                   ('{TagTrip}',      '{Ledger}', 'trip', '2026-01-01'),
                   ('{TagHome}',      '{Ledger}', 'home', '2026-01-01'),
                   ('{TagProj}',      '{Ledger}', 'proj', '2026-01-01'),
                   ('{OtherLedgerWork}', '{OtherLedger}', 'work', '2026-01-01');
            """);

        await OneSplitAsync(cs, Id(100), "h1", (BankA, null), (CatA, null));
        await OneSplitAsync(cs, Id(101), "h2", (BankA, "x2"), (CatA, null));
        await OneSplitAsync(cs, Id(102), "h3", (BankA, null), (CatA, "y3"));
        await OneSplitAsync(cs, Id(103), "h4", (BankA, "x4"), (CatA, "y4"));
        await OneSplitAsync(cs, Id(104), "same", (BankA, "same"), (CatA, "y5"));
        await OneSplitAsync(cs, Id(105), "h6", (BankA, "   "), (CatA, null));
        await OneSplitAsync(cs, Id(106), null, (BankA, null), (CatA, null));
        await HeaderTagAsync(cs, Id(106), TagTrip);
        await OneSplitAsync(cs, Id(107), null, (BankA, null), (CatA, null));
        await HeaderTagAsync(cs, Id(107), TagTrip);
        await LegTagAsync(cs, Id(107), BankA, 0, TagHome);
        await OneSplitAsync(cs, Id(108), null, (BankA, "x9"), (CatA, null));

        // A split: the transaction's memo and tag, a line's own memo on its split side.
        await HeaderAsync(cs, Id(109), memo: "whole");
        await LegsAsync(cs, Id(109),
            (BankA, 0, -10m, "line0", null, null, null), (CatA, 0, 10m, null, null, null, null),
            (BankA, 1, -5m, null, null, null, null),     (CatB, 1, 5m, null, null, null, null));
        await HeaderTagAsync(cs, Id(109), TagTrip);

        // A buy with a fee, its memo copied onto every leg as the investment editor did.
        await HeaderAsync(cs, Id(110), memo: "buy", action: "buy");
        await LegsAsync(cs, Id(110),
            (HoldP, 0, 100m, "buy", Security, 2m, "security"), (BrokP, 0, -100m, "buy", null, null, "security"),
            (Fee, 1, 5m, "buy", null, null, "fee"),             (BrokP, 1, -5m, "buy", null, null, "fee"));

        // A buy with a fee whose values sit on its investment side only.
        await HeaderAsync(cs, Id(111), action: "buy");
        await LegsAsync(cs, Id(111),
            (HoldP, 0, 100m, "b", Security, 2m, "security"), (BrokP, 0, -100m, "a", null, null, "security"),
            (Fee, 1, 5m, null, null, null, "fee"),            (BrokP, 1, -5m, null, null, null, "fee"));
        await LegTagAsync(cs, Id(111), BrokP, 1, TagProj);

        // A one-posting cash transfer out of the brokerage.
        await HeaderAsync(cs, Id(112), memo: "x13", action: "transfer");
        await LegsAsync(cs, Id(112),
            (BrokP, 0, -40m, null, null, null, "transfer"), (BankA, 0, 40m, null, null, null, "transfer"));
        await HeaderTagAsync(cs, Id(112), TagTrip);

        // A manual reminder's template, and a template with no legs (a Moneydance
        // reminder with no field values).
        await HeaderAsync(cs, Id(113), memo: "rent", template: true);
        await LegsAsync(cs, Id(113), (BankA, 0, -30m, null, null, null, null), (CatA, 0, 30m, null, null, null, null));
        await SeriesAsync(cs, Id(300), Id(113), BankA);
        await HeaderAsync(cs, Id(115), memo: "legless", template: true);
        await SeriesAsync(cs, Id(301), Id(115), source: null);

        // A split with a blank transaction memo.
        await HeaderAsync(cs, Id(114), memo: " \t ");
        await LegsAsync(cs, Id(114),
            (BankA, 0, -1m, null, null, null, null), (CatA, 0, 1m, null, null, null, null),
            (BankA, 1, -2m, null, null, null, null), (CatB, 1, 2m, null, null, null, null));

        // Case variants: a one-split carrying only one, and a split carrying two of them,
        // the survivor and another tag.
        await OneSplitAsync(cs, Id(116), null, (BankA, null), (CatA, null));
        await HeaderTagAsync(cs, Id(116), TagWorkLower);
        await HeaderAsync(cs, Id(117));
        await LegsAsync(cs, Id(117),
            (BankA, 0, -1m, null, null, null, null), (CatA, 0, 1m, null, null, null, null),
            (BankA, 1, -2m, null, null, null, null), (CatB, 1, 2m, null, null, null, null));
        await HeaderTagAsync(cs, Id(117), TagWorkLower);
        await HeaderTagAsync(cs, Id(117), TagWorkUpper);
        await HeaderTagAsync(cs, Id(117), TagWork);
        await HeaderTagAsync(cs, Id(117), TagTrip);

        // Saved reports: three on a variant (two owners, private and shared, one with the
        // uuid in upper case), and every other kind of tagId, which stay as they are.
        await ReportAsync(cs, Id(400), Ledger, UserA, "private", $$"""{"tagId": "{{TagWorkLower}}"}""");
        await ReportAsync(cs, Id(401), Ledger, UserB, "ledger", $$"""{"tagId": "{{TagWorkUpper}}"}""");
        await ReportAsync(cs, Id(402), Ledger, UserB, "private", $$"""{"tagId": "{{TagWorkLower.ToString().ToUpperInvariant()}}"}""");
        await ReportAsync(cs, Id(403), Ledger, UserA, "ledger", $$"""{"tagId": "{{TagWork}}"}""");
        await ReportAsync(cs, Id(404), Ledger, UserA, "private", """{"kind": "summary"}""");
        await ReportAsync(cs, Id(405), Ledger, UserB, "private", """{"tagId": null}""");
        await ReportAsync(cs, Id(406), Ledger, UserA, "private", """{"tagId": "not-a-uuid"}""");
        await ReportAsync(cs, Id(407), Ledger, UserA, "ledger", $$"""{"tagId": "{{TagTrip}}"}""");
        await ReportAsync(cs, Id(408), OtherLedger, UserA, "private", $$"""{"tagId": "{{TagWorkLower}}"}""");

        // A buy with a fee funded from Q, its memo copied onto every leg: Q's funding
        // leg is not the event's investment side and keeps its copy.
        await HeaderAsync(cs, Id(118), memo: "bx", action: "buyx");
        await LegsAsync(cs, Id(118),
            (HoldP, 0, 100m, "bx", Security, 2m, "security"), (BrokP, 0, -100m, "bx", null, null, "security"),
            (Fee, 1, 5m, "bx", null, null, "fee"),             (BrokP, 1, -5m, "bx", null, null, "fee"),
            (BrokP, 2, 105m, "bx", null, null, "transfer"),     (BrokQ, 2, -105m, "bx", null, null, "transfer"));

        // An in-kind transfer from Q to P with a memo on each brokerage's leg and none
        // on the header.
        await HeaderAsync(cs, Id(119), action: "transfer_shares");
        await LegsAsync(cs, Id(119),
            (HoldQ, 0, -100m, null, Security, -2m, "security"), (BrokQ, 0, 0m, "q", null, null, "security"),
            (HoldP, 1, 100m, null, Security, 2m, "security"),   (BrokP, 1, 0m, "p", null, null, "security"));

        for (var h = 100; h <= 119; h++)
            await ExecAsync(cs, $"SELECT fn_recompute_posting_counts_for_header('{Id(h)}');");
    }

    private static async Task OneSplitAsync(
        string cs, Guid header, string? memo, (Guid Account, string? Memo) entered, (Guid Account, string? Memo) other)
    {
        await HeaderAsync(cs, header, memo: memo);
        await LegsAsync(cs, header,
            (entered.Account, 0, -10m, entered.Memo, null, null, null),
            (other.Account, 0, 10m, other.Memo, null, null, null));
    }

    private static Task HeaderAsync(string cs, Guid header, string? memo = null, string? action = null, bool template = false) =>
        ExecAsync(cs, $"""
            INSERT INTO txn_headers (id, ledger_id, origin, action, payee, memo, posted_at, transacted_at,
                                     created_at, is_recurring_template)
            VALUES ('{header}', '{Ledger}', 'manual', {Lit(action)}, 'Payee {header.ToString()[^3..]}', {Lit(memo)},
                    TIMESTAMPTZ '2026-03-01 00:00:00+00', TIMESTAMPTZ '2026-03-01 00:00:00+00',
                    TIMESTAMPTZ '2026-03-01 00:00:00+00', {(template ? "TRUE" : "FALSE")});
            """);

    private static async Task LegsAsync(
        string cs, Guid header,
        params (Guid Account, int Posting, decimal Amount, string? Memo, Guid? Security, decimal? Quantity, string? Role)[] legs)
    {
        foreach (var leg in legs)
        {
            await ExecAsync(cs, $"""
                INSERT INTO txn_legs (id, header_id, ledger_id, account_id, posting_index, amount, leg_memo,
                                      security_id, quantity, unit_price, posting_role)
                VALUES ('{Guid.NewGuid()}', '{header}', '{Ledger}', '{leg.Account}', {leg.Posting},
                        {leg.Amount.ToString(CultureInfo.InvariantCulture)}, {Lit(leg.Memo)},
                        {Lit(leg.Security?.ToString())}, {leg.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "NULL"},
                        {(leg.Quantity is null ? "NULL" : "50")}, {Lit(leg.Role)});
                """);
        }
    }

    private static Task HeaderTagAsync(string cs, Guid header, Guid tag) =>
        ExecAsync(cs, $"""
            INSERT INTO txn_header_tags (header_id, tag_id, ledger_id, created_at)
            VALUES ('{header}', '{tag}', '{Ledger}', TIMESTAMPTZ '2026-03-02 00:00:00+00');
            """);

    private static Task LegTagAsync(string cs, Guid header, Guid account, int posting, Guid tag) =>
        ExecAsync(cs, $"""
            INSERT INTO txn_leg_tags (leg_id, tag_id, ledger_id, created_at)
            SELECT l.id, '{tag}', '{Ledger}', TIMESTAMPTZ '2026-03-02 00:00:00+00'
              FROM txn_legs l
             WHERE l.header_id = '{header}' AND l.account_id = '{account}' AND l.posting_index = {posting};
            """);

    private static Task SeriesAsync(string cs, Guid series, Guid template, Guid? source) =>
        ExecAsync(cs, $"""
            INSERT INTO recurring_transactions
                (id, ledger_id, start_date, rrule, next_due_date, template_header_id, source_account_id, origin)
            VALUES ('{series}', '{Ledger}', DATE '2026-01-01', 'FREQ=MONTHLY;BYMONTHDAY=1', DATE '2026-01-01',
                    '{template}', {Lit(source?.ToString())}, 'manual');
            """);

    private static Task ReportAsync(string cs, Guid id, Guid ledger, Guid owner, string visibility, string spec) =>
        ExecAsync(cs, $"""
            INSERT INTO saved_reports (id, ledger_id, owner_user_id, name, spec, visibility, created_at, updated_at)
            VALUES ('{id}', '{ledger}', '{owner}', 'Report {id.ToString()[^3..]}', {Lit(spec)}::jsonb, '{visibility}',
                    TIMESTAMPTZ '2026-04-01 00:00:00+00', TIMESTAMPTZ '2026-04-02 00:00:00+00');
            """);

    // ----- reads -----

    private static async Task<LegState> LegStateAsync(string cs, Guid header, Guid account)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT l.leg_memo,
                   COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                               FROM txn_leg_tags lt JOIN tags t ON t.id = lt.tag_id WHERE lt.leg_id = l.id), ''),
                   l.notes_memo, l.notes_memo_inherited,
                   COALESCE((SELECT string_agg(t.name, ',' ORDER BY t.name COLLATE "C")
                               FROM txn_leg_note_tags n JOIN tags t ON t.id = n.tag_id
                              WHERE n.leg_id = l.id AND n.shown), '')
              FROM txn_legs l
             WHERE l.header_id = @h AND l.account_id = @a
             ORDER BY l.posting_index
             LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("h", header);
        command.Parameters.AddWithValue("a", account);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"no leg of {header} on {account}");
        // An inherited flag only means something where a memo is shown.
        var shown = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new LegState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1),
            shown,
            shown is null ? false : reader.GetBoolean(3),
            reader.GetString(4));
    }

    private static async Task<(string? Memo, string Tags)> HeaderStateAsync(string cs, Guid header)
    {
        var memo = await ScalarAsync(cs, $"SELECT memo FROM txn_headers WHERE id = '{header}'") as string;
        return (memo, string.Join(',', await HeaderTagNamesAsync(cs, header)));
    }

    private static Task<List<string>> HeaderTagNamesAsync(string cs, Guid header) =>
        StringsAsync(cs, $"""
            SELECT t.name FROM txn_header_tags ht JOIN tags t ON t.id = ht.tag_id
             WHERE ht.header_id = '{header}' ORDER BY t.name COLLATE "C"
            """);

    private static Task<List<string>> LegTagNamesAsync(string cs, Guid header, Guid account) =>
        StringsAsync(cs, $"""
            SELECT t.name FROM txn_leg_tags lt JOIN txn_legs l ON l.id = lt.leg_id JOIN tags t ON t.id = lt.tag_id
             WHERE l.header_id = '{header}' AND l.account_id = '{account}' ORDER BY t.name COLLATE "C"
            """);

    private static async Task<List<string>> StringsAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<List<string>> JournalAsync(string cs)
    {
        var names = await StringsAsync(cs, "SELECT scriptname FROM public.__schema_migrations ORDER BY schemaversionsid");
        return [.. names.Select(n => n[(n.LastIndexOfAny(['/', '\\']) + 1)..])];
    }

    private static AppDbContext Context(string cs) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static string Lit(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

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
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }
}
