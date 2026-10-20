using Dapper;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Mappers;
using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Db;

/// <summary>
/// An imported tag name obeys the same 64-character cap the API enforces.
/// </summary>
/// <remarks>
/// <para>It did not. <c>tags.name</c> is bare <c>TEXT</c> with no CHECK and the
/// importer only trimmed, so a Moneydance export could mint names the
/// application's own tag management would refuse to create — and any later
/// rename of one would be judged against a limit the stored value already
/// broke. Two endpoints each held their own <c>const int 64</c>; the importer
/// held neither.</para>
///
/// <para>Truncate rather than reject, decided by the maintainer: refusing a
/// whole import over a label is worse than shortening the label, and a tag
/// name carries no identity — nothing downstream keys on it.</para>
/// </remarks>
[Collection(DbCollection.Name)]
public sealed class TagNameTruncationTests
{
    private readonly PostgresFixture _fixture;

    public TagNameTruncationTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Prefix =
        "A tag name long enough to be cut by the sixty-four character cap";   // exactly 64

    [Fact]
    public void The_prefix_this_file_is_built_on_really_is_sixty_four()
    {
        // The other tests mean nothing if this drifts: a 63-character prefix
        // would make the collision case pass for the wrong reason.
        Assert.Equal(TagName.MaxLength, Prefix.Length);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Normalize_leaves_an_acceptable_name_alone(string raw, string? expected) =>
        Assert.Equal(expected, TagName.Normalize(raw));

    [Fact]
    public void A_cut_that_lands_on_a_space_does_not_leave_one()
    {
        // The second trim. Without it a name ending in a space is stored with
        // it, which reads identically to one without in every UI while being a
        // different row.
        var raw = Prefix[..60].TrimEnd() + "      tail";
        var normalized = TagName.Normalize(raw)!;

        Assert.True(normalized.Length <= TagName.MaxLength);
        Assert.Equal(normalized.TrimEnd(), normalized);
    }

    [Fact]
    public async Task An_over_length_imported_tag_is_stored_at_the_cap()
    {
        await using var conn = _fixture.OpenConnection();
        var ledgerId = await FreshLedgerAsync(conn);

        var longName = Prefix + " and then a great deal more that cannot fit";
        await ImportOneTaggedTxnAsync(conn, ledgerId, longName);

        var stored = await conn.QuerySingleAsync<string>(
            "SELECT name FROM tags WHERE ledger_id = @L;", new { L = ledgerId });

        Assert.Equal(Prefix, stored);
        Assert.Equal(TagName.MaxLength, stored.Length);
    }

    [Fact]
    public async Task The_link_survives_truncation_rather_than_being_dropped()
    {
        // The failure mode that made WHERE the truncation happens matter. The
        // import step resolves links with `tagIdByName.TryGetValue(name, ...)`
        // and ignores a miss. Truncating at the repository instead of the
        // mapper would key that dictionary by the short name while the caller
        // asked with the long one — no exception, no log, just a tag that
        // quietly failed to attach.
        await using var conn = _fixture.OpenConnection();
        var ledgerId = await FreshLedgerAsync(conn);

        await ImportOneTaggedTxnAsync(conn, ledgerId, Prefix + " overflowing well past the cap");

        var links = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM txn_header_tags WHERE ledger_id = @L;", new { L = ledgerId });
        Assert.Equal(1, links);
    }

    [Fact]
    public async Task Two_names_sharing_the_first_64_characters_become_ONE_tag()
    {
        // THE COST OF TRUNCATING, pinned rather than discovered. Rejecting
        // would have avoided this at the price of refusing the import; the
        // maintainer chose truncation knowing it. If this test ever starts
        // failing, the policy changed — it is not a bug on its own.
        await using var conn = _fixture.OpenConnection();
        var ledgerId = await FreshLedgerAsync(conn);

        await ImportOneTaggedTxnAsync(
            conn, ledgerId, Prefix + " ending one way", Prefix + " ending quite another");

        var names = (await conn.QueryAsync<string>(
            "SELECT name FROM tags WHERE ledger_id = @L;", new { L = ledgerId })).ToList();

        Assert.Single(names);
        Assert.Equal(Prefix, names[0]);
    }

    /// <summary>Import one bank txn carrying <paramref name="tagNames"/> as its
    /// header tags, through the real pipeline rather than a direct insert.</summary>
    private static async Task ImportOneTaggedTxnAsync(
        Npgsql.NpgsqlConnection conn, Guid ledgerId, params string[] tagNames)
    {
        var tags = string.Join(",", tagNames);
        var json = $$"""
            {
              "metadata": {"exporter":"x","moneydance_build":1,"export_date":1,"file_name":"y"},
              "all_items": [
                {"obj_type":"acct","id":"a-root","type":"r","name":"Root","currid":"c-usd"},
                {"obj_type":"acct","id":"a-bank","type":"b","name":"Checking","parentid":"a-root","currid":"c-usd","sbal":"0"},
                {"obj_type":"acct","id":"a-cat","type":"e","name":"Groceries","parentid":"a-root","currid":"c-usd"},
                {"obj_type":"txn","id":"t-1","acctid":"a-bank","desc":"Shop","dt":"20260101",
                 "xfer_type":"xfrtp_bank","tags":{{System.Text.Json.JsonSerializer.Serialize(tags)}},
                 "0.id":"s-1","0.acctid":"a-cat","0.samt":"1000","0.pamt":"-1000","0.desc":"Shop"}
              ]
            }
            """;

        var context = new ImportContext(MdItemReader.ReadString(json), ledgerId);
        await AccountImportStep.RunAsync(conn, context);
        await TransactionImportStep.RunAsync(conn, context, "test:tag-truncation");
    }

    private static async Task<Guid> FreshLedgerAsync(Npgsql.NpgsqlConnection conn)
    {
        await conn.ExecuteAsync("""
            TRUNCATE txn_header_tags, txn_leg_tags, tags, txn_legs, txn_headers, accounts
            RESTART IDENTITY CASCADE;
            """);
        var id = TestLedger.Id;
        await conn.ExecuteAsync(
            "INSERT INTO ledgers (id, name) VALUES (@Id, 'Default') ON CONFLICT (id) DO NOTHING;",
            new { Id = id });
        return id;
    }
}
