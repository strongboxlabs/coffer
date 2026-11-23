using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Domain.Annotations;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// The two API writers that turn a name into a tag — the dictionary behind every tag
/// assignment and the rename — decide "the same name" by <c>tag_name_key</c> alone,
/// the database's definition, never by a .NET fold.
/// </summary>
/// <remarks>
/// <para>The vectors are the folding cases where .NET's invariant lower-casing and the
/// database's collation can disagree (dotted capital I, sharp s, the capital sharp s,
/// Greek), and the white space only one side would trim. Each says whether a second
/// spelling is the same tag as the first.</para>
/// <para>Non-ASCII characters are spelled as <c>&lt;hhhh&gt;</c> code points, as in
/// <c>TagNameKeyTests</c>.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed partial class TagNameFoldingWritersTests : DatabaseTest
{
    public TagNameFoldingWritersTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public static TheoryData<string, string, bool> Vectors => new()
    {
        { "work", "Work", true },
        { "work", "  WORK<0009>", true },
        { "work", "<00A0>Work<3000>", true },
        { "istanbul", "<0130>stanbul", true },
        { "strasse", "STRASSE", true },
        { "<00DF>", "<1E9E>", true },
        { "<03BF><03B4><03BF><03C3>", "<039F><0394><039F><03A3>", true },
        // Different keys under the shipped collation.
        { "strasse", "Stra<00DF>e", false },
        { "work", "<200B>Work", false },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Assignment_resolves_a_spelling_to_the_tag_with_its_key(string existing, string requested, bool same)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var tagId = await ledger.AddBareTagAsync(U(existing));

        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            var resolved = await TagDictionary.ResolveAsync(db, ledger.LedgerId, [U(requested)], CancellationToken.None);
            var id = Assert.Single(resolved.Values);
            Assert.Equal(same, id == tagId);
            await db.SaveChangesAsync();
        }

        Assert.Equal(same ? 1 : 2, await TagCountAsync(ledger));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task A_rename_to_another_tags_key_is_refused(string existing, string requested, bool same)
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        await ledger.AddBareTagAsync(U(existing));
        var other = await ledger.AddBareTagAsync("renamed one");

        await using var db = Db.NewAppDbContextAsUser(ledger.UserId);
        var result = await new TagsRepository(db).PatchAsync(ledger.LedgerId, other, U(requested), null);

        Assert.Equal(same ? TagsRepository.PatchTagResult.NameExists : TagsRepository.PatchTagResult.Ok, result);
    }

    [Fact]
    public async Task A_case_only_self_rename_succeeds()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var tag = await ledger.AddBareTagAsync("istanbul");

        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            var result = await new TagsRepository(db).PatchAsync(ledger.LedgerId, tag, U("<0130>STANBUL"), null);
            Assert.Equal(TagsRepository.PatchTagResult.Ok, result);
        }

        await using var check = Db.NewServiceDbContext();
        Assert.Equal(U("<0130>STANBUL"), await check.Tags.Where(t => t.Id == tag).Select(t => t.Name).SingleAsync());
    }

    [Fact]
    public async Task A_rename_that_loses_a_race_for_its_name_is_name_exists_not_a_failure()
    {
        // The check passes, then another writer takes the name before the save: the
        // unique index refuses the save, and the answer is the one the check gives.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var tag = await ledger.AddBareTagAsync("before");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.AppConnectionString)
            .AddInterceptors(
                new FixedUserConnectionInterceptor(ledger.UserId),
                new TakeTheNameFirst(Db.ServiceConnectionString, ledger.LedgerId, "after"))
            .Options;
        await using var db = new AppDbContext(options);

        var result = await new TagsRepository(db).PatchAsync(ledger.LedgerId, tag, "after", null);

        Assert.Equal(TagsRepository.PatchTagResult.NameExists, result);
    }

    [Fact]
    public async Task A_rename_that_loses_a_race_for_its_key_is_name_exists_not_a_failure()
    {
        // Another writer takes the name in another case between the check and the save:
        // only the key index refuses the save, and the answer is the same.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var tag = await ledger.AddBareTagAsync("before");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Db.AppConnectionString)
            .AddInterceptors(
                new FixedUserConnectionInterceptor(ledger.UserId),
                new TakeTheNameFirst(Db.ServiceConnectionString, ledger.LedgerId, "AFTER"))
            .Options;
        await using var db = new AppDbContext(options);

        var result = await new TagsRepository(db).PatchAsync(ledger.LedgerId, tag, "after", null);

        Assert.Equal(TagsRepository.PatchTagResult.NameExists, result);
    }

    [Theory]
    [InlineData("Holiday", "Holiday")]
    [InlineData("Holiday", " HOLIDAY")]
    public async Task Two_writers_creating_one_new_name_at_once_end_with_one_tag(string first, string second)
    {
        // Both units of work resolve the name before either saves, so both create it.
        // The unique index lets the first in and refuses the second's save; the second
        // re-reads by key, links the tag the first created and saves again.
        var ledger = await SyntheticLedger.CreateAsync(Db);
        var bank = await ledger.AddBankAccountAsync("Account A");
        var category = await ledger.AddCategoryAsync("Category A");
        var day = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var (legA, _) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -5m, day);
        var (legB, _) = await ledger.AddTransactionPairAsync(bank.Id, category.Id, -6m, day);
        var headerA = await ledger.ResolveHeaderIdAsync(legA);
        var headerB = await ledger.ResolveHeaderIdAsync(legB);

        await using var one = Db.NewServiceFactory().Create();
        await using var two = Db.NewServiceFactory().Create();
        await TagFromBankAsync(one, ledger.LedgerId, headerA, bank.Id, first);
        await TagFromBankAsync(two, ledger.LedgerId, headerB, bank.Id, second);
        await one.SaveChangesAsync();
        await two.SaveChangesAsync();

        await using var check = Db.NewServiceDbContext();
        var tags = await check.Tags.Where(t => t.LedgerId == ledger.LedgerId).ToListAsync();
        var tag = Assert.Single(tags);
        Assert.Equal("Holiday", tag.Name);
        foreach (var header in new[] { headerA, headerB })
            Assert.True(await check.TxnLegTags.AnyAsync(t => t.TagId == tag.Id
                            && check.TxnLegs.Any(l => l.Id == t.LegId && l.HeaderId == header && l.AccountId == bank.Id)),
                $"header {header} lost its tag");
        Assert.Empty(await LegNotes.DifferencesAsync(check, ledger.LedgerId, CancellationToken.None));
    }

    /// <summary>Plan a tag onto the bank side of a one-split, through the write primitive, unsaved.</summary>
    private static async Task TagFromBankAsync(AppDbContext db, Guid ledgerId, Guid headerId, Guid bankId, string name)
    {
        var header = await db.TxnHeaders.SingleAsync(h => h.Id == headerId);
        var stored = await AnnotationWriter.LoadAsync(db, header, CancellationToken.None);
        var resolved = await AnnotationWriter.ResolveAsync(db, ledgerId,
            new AnnotationIntent<string>(bankId, FieldEdit<string>.Leave, FieldEdit<IReadOnlyList<string>>.Set([name])),
            CancellationToken.None);
        var plan = AnnotationPlanner.Plan(ShapeChange<Guid>.Unchanged(stored.Shape, stored.Notes), resolved);
        Assert.True(plan.IsValid);
        await AnnotationWriter.ApplyAsync(db, header, stored.Legs, plan, CancellationToken.None);
    }

    [Fact]
    public async Task Two_spellings_of_one_new_name_in_one_unit_of_work_create_one_tag()
    {
        var ledger = await SyntheticLedger.CreateAsync(Db);

        await using (var db = Db.NewAppDbContextAsUser(ledger.UserId))
        {
            var first = await TagDictionary.ResolveAsync(db, ledger.LedgerId, [U("Caf<00E9>"), U("CAF<00C9>")], CancellationToken.None);
            var second = await TagDictionary.ResolveAsync(db, ledger.LedgerId, [U("caf<00E9> ")], CancellationToken.None);
            Assert.Single(first.Values.Concat(second.Values).Distinct());
            await db.SaveChangesAsync();
        }

        await using var check = Db.NewServiceDbContext();
        // The first spelling in input order is the one stored.
        Assert.Equal([U("Caf<00E9>")], await check.Tags.Where(t => t.LedgerId == ledger.LedgerId).Select(t => t.Name).ToListAsync());
    }

    // ----- helpers -----

    /// <summary>Inserts a tag named <c>name</c> the moment the rename's UPDATE is about to run.</summary>
    private sealed class TakeTheNameFirst(string serviceConnectionString, Guid ledgerId, string name) : DbCommandInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await TakeAsync(command).ConfigureAwait(false);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await TakeAsync(command).ConfigureAwait(false);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private async Task TakeAsync(DbCommand command)
        {
            if (_done || !command.CommandText.Contains("UPDATE tags", StringComparison.Ordinal)) return;
            _done = true;
            await using var connection = new NpgsqlConnection(serviceConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO tags (id, ledger_id, name) VALUES (@id, @ledger, @name)", connection);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("ledger", ledgerId);
            insert.Parameters.AddWithValue("name", name);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> TagCountAsync(SyntheticLedger ledger)
    {
        await using var db = Db.NewServiceDbContext();
        return await db.Tags.CountAsync(t => t.LedgerId == ledger.LedgerId);
    }

    [GeneratedRegex("<([0-9A-F]{4})>")]
    private static partial Regex CodePoint();

    /// <summary>"<c>&lt;00A0&gt;</c>" to the character it names.</summary>
    private static string U(string spelled) =>
        CodePoint().Replace(spelled, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
}
