using Npgsql;

using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Tags;

/// <summary>
/// Two ledgers of saved reports filtering on tags: the fixture every test of a tag
/// merge's saved-report repoint shares, called directly or through the merge.
/// </summary>
/// <remarks>
/// In A: the owner, an editor, a viewer, and members X (viewer) and Y (owner) with a
/// private and a shared report each on tag S, the editor's own private one, beside
/// reports on another tag, with no tagId, a null tagId, a non-uuid tagId, a number,
/// and an uppercase uuid equal to S. In B: a report whose tagId string is A's S, and
/// one on B's own tag. A user with no grant at all.
/// </remarks>
internal sealed record SavedReportWorld(
    SyntheticLedger A, SyntheticLedger B,
    Guid Editor, Guid Viewer, Guid X, Guid Y, Guid Stranger,
    Guid S, Guid T, Guid Other, Guid TagOfB,
    IReadOnlyList<Guid> OnS, Guid EditorsOwnOnS, IReadOnlyList<Guid> SharedOnS,
    IReadOnlyList<Guid> UntouchedInA, IReadOnlyList<Guid> InB,
    IReadOnlyDictionary<Guid, Guid> OwnerOf)
{
    /// <summary>Every report was seeded with this created and updated stamp.</summary>
    public static readonly DateTime Stamp = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static async Task<SavedReportWorld> BuildAsync(TestDatabase database)
    {
        var a = await SyntheticLedger.CreateAsync(database);
        var b = await SyntheticLedger.CreateAsync(database);
        var editor = await a.AddMemberAsync("editor");
        var viewer = await a.AddMemberAsync("viewer");
        var x = await a.AddMemberAsync("viewer");
        var y = await a.AddMemberAsync("owner");
        var stranger = await AddUserWithoutGrantsAsync(database);

        var s = await a.AddBareTagAsync("Tag S");
        var t = await a.AddBareTagAsync("Tag T");
        var other = await a.AddBareTagAsync("Tag O");
        var tagOfB = await b.AddBareTagAsync("Tag S");

        var owners = new Dictionary<Guid, Guid>();
        async Task<Guid> Report(SyntheticLedger ledger, Guid owner, string visibility, string spec)
        {
            var id = await ReportAsync(database, ledger, owner, visibility, spec);
            owners[id] = owner;
            return id;
        }

        var onS = new List<Guid>
        {
            await Report(a, x, "private", Spec(Tag(s))),
            await Report(a, x, "ledger", Spec(Tag(s))),
            await Report(a, y, "private", Spec(Tag(s))),
            await Report(a, y, "ledger", Spec(Tag(s))),
            // The same uuid in upper case: the parser accepts either case.
            await Report(a, y, "private", Spec($"\"{s.ToString().ToUpperInvariant()}\"")),
        };
        var editorsOwn = await Report(a, editor, "private", Spec(Tag(s)));
        onS.Add(editorsOwn);
        var shared = new[] { onS[1], onS[3] };

        var untouched = new List<Guid>
        {
            await Report(a, x, "ledger", Spec(Tag(other))),
            await Report(a, y, "private", Spec(Tag(t))),
            await Report(a, x, "private", """{"groupBy": "category"}"""),
            await Report(a, y, "ledger", Spec("null")),
            await Report(a, x, "private", Spec("\"not-a-uuid\"")),
            await Report(a, y, "private", Spec("42")),
            await Report(a, editor, "ledger", Spec($"\"{s}-0\"")),
        };
        var inB = new List<Guid>
        {
            // A's tag id, in another ledger: never reached by a merge in A.
            await Report(b, b.UserId, "private", Spec(Tag(s))),
            await Report(b, b.UserId, "ledger", Spec(Tag(tagOfB))),
        };

        return new SavedReportWorld(a, b, editor, viewer, x, y, stranger, s, t, other, tagOfB,
            onS, editorsOwn, shared, untouched, inB, owners);
    }

    private static string Spec(string tagIdJson) =>
        $$"""{"tagId": {{tagIdJson}}, "groupBy": "category", "from": "2026-01-01"}""";

    private static string Tag(Guid id) => $"\"{id}\"";

    private static async Task<Guid> AddUserWithoutGrantsAsync(TestDatabase database)
    {
        var id = Guid.NewGuid();
        await using var db = database.NewServiceDbContext();
        db.Users.Add(new UserRow
        {
            Id = id,
            DisplayName = $"stranger-{id:N}",
            Username = $"stranger-{id:N}",
            CreatedBy = "integration-test",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> ReportAsync(
        TestDatabase database, SyntheticLedger ledger, Guid owner, string visibility, string spec)
    {
        var id = Guid.NewGuid();
        await using var connection = database.OpenServiceConnection();
        await using var command = new NpgsqlCommand("""
            INSERT INTO saved_reports (id, ledger_id, owner_user_id, name, spec, visibility, created_at, updated_at)
            VALUES (@id, @ledger, @owner, @name, @spec::jsonb, @visibility, @stamp, @stamp)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("ledger", ledger.LedgerId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("name", $"Report {id:N}");
        command.Parameters.AddWithValue("spec", spec);
        command.Parameters.AddWithValue("visibility", visibility);
        command.Parameters.AddWithValue("stamp", Stamp);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>One saved report as stored, with its tagId and the rest of its spec apart.</summary>
    public sealed record Report(
        Guid LedgerId, Guid Owner, string Name, string Visibility,
        string? Spec, string? TagId, string SpecWithoutTagId, DateTime CreatedAt, DateTime UpdatedAt);

    /// <summary>
    /// Every saved report of the world's two ledgers, keyed by id — a refusal must leave
    /// all of them. Other tests write saved reports on the same database, so the read
    /// stops at these two; B's reports, one of which names A's tag, are what would catch
    /// a repoint that ignores its ledger.
    /// </summary>
    public async Task<Dictionary<Guid, Report>> ReportsAsync(TestDatabase database)
    {
        await using var connection = database.OpenServiceConnection();
        await using var command = new NpgsqlCommand("""
            SELECT id, ledger_id, owner_user_id, name, visibility, spec::text,
                   CASE WHEN jsonb_typeof(spec -> 'tagId') = 'string' THEN spec ->> 'tagId' END,
                   (spec - 'tagId')::text, created_at, updated_at
              FROM saved_reports
             WHERE ledger_id IN (@a, @b)
            """, connection);
        command.Parameters.AddWithValue("a", A.LedgerId);
        command.Parameters.AddWithValue("b", B.LedgerId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<Guid, Report>();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = new Report(
                reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.GetDateTime(8), reader.GetDateTime(9));
        }
        return result;
    }

    /// <summary>
    /// The repoint left exactly what it should: every report on S names T, in the
    /// lower-case "D" form, with nothing else about it changed; every other report of
    /// both ledgers is as it was; nothing was added, removed or stamped as edited.
    /// </summary>
    public void AssertRepointedOnly(Dictionary<Guid, Report> before, Dictionary<Guid, Report> after)
    {
        foreach (var id in OnS)
        {
            Assert.Equal(T.ToString(), after[id].TagId);
            Assert.Equal(before[id] with { Spec = null, TagId = null }, after[id] with { Spec = null, TagId = null });
            Assert.Equal(before[id].SpecWithoutTagId, after[id].SpecWithoutTagId);
        }
        foreach (var id in UntouchedInA.Concat(InB))
            Assert.Equal(before[id], after[id]);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(after.Values, r => Assert.Equal(Stamp, r.UpdatedAt));
    }
}
