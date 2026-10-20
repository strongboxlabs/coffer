using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Pinned saved reports (ADR-0104 D2, migration 245).
/// </summary>
/// <remarks>
/// Driven through <c>NewAppDbContextAsUser</c> rather than HTTP, because every
/// claim here is about WHO is asking: a pin is per-user, and the policy keys
/// on <c>current_app_user_id()</c>. A repository opened without an acting user
/// is not acting as anyone and would prove nothing.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReportPinsTests
{
    private readonly PostgresFixture _fixture;

    public ReportPinsTests(PostgresFixture fixture) => _fixture = fixture;

    private const string SpecJson = """{"measure":"Spending","groupBy":"Category"}""";

    private async Task<Guid> SaveAsync(SyntheticLedger ledger, string name, string visibility)
    {
        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var created = await new SavedReportsRepository(db, TimeProvider.System)
            .CreateAsync(ledger.LedgerId, ledger.UserId, name, SpecJson, visibility);
        return created.Id;
    }

    [Fact]
    public async Task A_pinned_report_appears_in_the_rail_with_its_name()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var reportId = await SaveAsync(ledger, "Groceries, 12 months", "private");

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var pins = new ReportPinsRepository(db, TimeProvider.System);
        Assert.True(await pins.PinSavedAsync(ledger.LedgerId, reportId, ledger.UserId));

        var rail = await pins.ListAsync(ledger.LedgerId);
        // The name comes from the joined report: a pin row carries none, and
        // the rail renders labels.
        Assert.Equal("Groceries, 12 months", Assert.Single(rail).Name);
    }

    [Fact]
    public async Task Pinning_twice_does_not_duplicate_the_rail_entry()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var reportId = await SaveAsync(ledger, "Twice", "private");

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var pins = new ReportPinsRepository(db, TimeProvider.System);
        await pins.PinSavedAsync(ledger.LedgerId, reportId, ledger.UserId);
        await pins.PinSavedAsync(ledger.LedgerId, reportId, ledger.UserId);

        // The caller is a toggle; two clicks racing is not an error, and the
        // primary key would otherwise turn it into one.
        Assert.Single(await pins.ListAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task A_canned_report_can_be_pinned_without_being_saved_first()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var pins = new ReportPinsRepository(db, TimeProvider.System);
        await pins.PinCatalogAsync(ledger.LedgerId, "income-and-expenses", ledger.UserId);

        var entry = Assert.Single(await pins.ListAsync(ledger.LedgerId));
        // This is the half of ADR-0104 D2 that stops the rail requiring a
        // NAME: without it, getting the report you always read into the
        // sidebar means inventing a title for it first.
        Assert.Equal("income-and-expenses", entry.CatalogKey);
        Assert.Null(entry.SavedReportId);
        // Name and spec stay null on purpose — the client resolves both from
        // its own catalog, so improving a preset never needs a data migration.
        Assert.Null(entry.Name);
        Assert.Null(entry.Spec);
    }

    [Fact]
    public async Task Pinning_the_same_canned_report_twice_is_a_no_op()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var pins = new ReportPinsRepository(db, TimeProvider.System);
        await pins.PinCatalogAsync(ledger.LedgerId, "income-and-expenses", ledger.UserId);
        await pins.PinCatalogAsync(ledger.LedgerId, "income-and-expenses", ledger.UserId);

        Assert.Single(await pins.ListAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task The_two_kinds_of_pin_sit_in_one_ordered_rail()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var reportId = await SaveAsync(ledger, "Groceries", "private");

        await using var db = _fixture.NewAppDbContextAsUser(ledger.UserId);
        var pins = new ReportPinsRepository(db, TimeProvider.System);
        await pins.PinCatalogAsync(ledger.LedgerId, "income-and-expenses", ledger.UserId);
        await pins.PinSavedAsync(ledger.LedgerId, reportId, ledger.UserId);

        var rail = await pins.ListAsync(ledger.LedgerId);
        Assert.Equal(2, rail.Count);
        // Appended in the order pinned, so adding one does not reshuffle the
        // rail someone has already arranged.
        Assert.Equal("income-and-expenses", rail[0].CatalogKey);
        Assert.Equal(reportId, rail[1].SavedReportId);
    }

    [Fact]
    public async Task Two_people_on_one_ledger_do_not_share_a_rail()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");
        // Shared, so the OTHER user can genuinely see it — this test is about
        // the pin being private, not about the report being invisible.
        var reportId = await SaveAsync(ledger, "Household spending", "ledger");

        await using (var ownerDb = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            await new ReportPinsRepository(ownerDb, TimeProvider.System)
                .PinSavedAsync(ledger.LedgerId, reportId, ledger.UserId);
        }

        await using var otherDb = _fixture.NewAppDbContextAsUser(otherUserId);
        var theirRail = await new ReportPinsRepository(otherDb, TimeProvider.System).ListAsync(ledger.LedgerId);

        // A pin says "I come back to this". It is not a property of the report
        // and its author does not get to put it in everyone's sidebar.
        Assert.Empty(theirRail);
    }

    [Fact]
    public async Task Someone_can_pin_a_shared_report_they_did_not_write()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");
        var reportId = await SaveAsync(ledger, "Household spending", "ledger");

        await using var otherDb = _fixture.NewAppDbContextAsUser(otherUserId);
        var pins = new ReportPinsRepository(otherDb, TimeProvider.System);
        Assert.True(await pins.PinSavedAsync(ledger.LedgerId, reportId, otherUserId));

        // Readable-but-not-writable is the shared-report contract; pinning is
        // a read-side act, so it is allowed where editing is not.
        Assert.Single(await pins.ListAsync(ledger.LedgerId));
    }

    [Fact]
    public async Task A_report_that_is_not_visible_cannot_be_pinned()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");
        var privateId = await SaveAsync(ledger, "Mine alone", "private");

        await using var otherDb = _fixture.NewAppDbContextAsUser(otherUserId);
        var pinned = await new ReportPinsRepository(otherDb, TimeProvider.System)
            .PinSavedAsync(ledger.LedgerId, privateId, otherUserId);

        // Otherwise the rail gains an entry that opens nothing, and the
        // refusal doubles as the answer to "does that id exist".
        Assert.False(pinned);
    }

    [Fact]
    public async Task Un_sharing_a_report_drops_it_from_other_peoples_rails()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");
        var reportId = await SaveAsync(ledger, "Was shared", "ledger");

        await using (var otherDb = _fixture.NewAppDbContextAsUser(otherUserId))
        {
            await new ReportPinsRepository(otherDb, TimeProvider.System)
                .PinSavedAsync(ledger.LedgerId, reportId, otherUserId);
            Assert.Single(await new ReportPinsRepository(otherDb, TimeProvider.System).ListAsync(ledger.LedgerId));
        }

        await using (var ownerDb = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            await new SavedReportsRepository(ownerDb, TimeProvider.System)
                .UpdateAsync(ledger.LedgerId, reportId, ledger.UserId, null, null, "private");
        }

        await using var again = _fixture.NewAppDbContextAsUser(otherUserId);
        // Their pin ROW survives — nothing deleted it — but the join to a
        // report they can no longer see drops it, so the rail does not show an
        // entry that would 404 on click.
        Assert.Empty(await new ReportPinsRepository(again, TimeProvider.System).ListAsync(ledger.LedgerId));
    }
}
