using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Reporting;

/// <summary>
/// Memorized reports (ADR-0104 D3, migration 244) — the HTTP surface, and the
/// visibility rule underneath it.
/// </summary>
/// <remarks>
/// <para>Split on purpose. The endpoint behaviours (round-trip, partial patch,
/// validation) go over HTTP as the owner. The WHO-can-see-what rule does not:
/// it lives in a row-level security policy keyed on
/// <c>current_app_user_id()</c>, so proving it needs a connection acting as a
/// second user — <c>NewAppDbContextAsUser</c>, the same vehicle
/// <c>LedgerRoleTests</c> uses. A test that drove both actors over HTTP would
/// have proved nothing, which is how this file started.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SavedReportsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public SavedReportsEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private const string SpecJson =
        """{"measure":"Spending","groupBy":"Category","dateMode":"Sliding","windowCount":12}""";

    private static object Body(string name, string? visibility = null) => new
    {
        name,
        spec = new { measure = "Spending", groupBy = "Category", dateMode = "Sliding", windowCount = 12 },
        visibility,
    };

    private static string Url(Guid ledgerId) => $"/api/ledgers/{ledgerId}/reports/saved";

    private static async Task<HttpClient> OwnerClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task A_saved_report_round_trips_with_its_spec_as_an_object()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var owner = await OwnerClientAsync(factory, ledger);

        var created = await owner.PostAsJsonAsync(Url(ledger.LedgerId), Body("Groceries, 12 months"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var spec = body.RootElement.GetProperty("spec");
        // An object, not a quoted string: the column is jsonb and the API
        // surface matches it, so no client has to parse a payload the server
        // already had structured.
        Assert.Equal(JsonValueKind.Object, spec.ValueKind);
        Assert.Equal(12, spec.GetProperty("windowCount").GetInt32());
        Assert.Equal("private", body.RootElement.GetProperty("visibility").GetString());

        var listed = await owner.GetFromJsonAsync<JsonElement>(Url(ledger.LedgerId));
        Assert.Equal(1, listed.GetArrayLength());
    }

    [Fact]
    public async Task A_patch_leaves_out_what_it_does_not_change()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var owner = await OwnerClientAsync(factory, ledger);

        var created = await owner.PostAsJsonAsync(Url(ledger.LedgerId), Body("Before"));
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        var patched = await owner.PatchAsJsonAsync(
            $"{Url(ledger.LedgerId)}/{id}", new { visibility = "ledger" });
        using var body = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());

        // Re-sharing did not require resending the spec, and did not wipe it.
        Assert.Equal("ledger", body.RootElement.GetProperty("visibility").GetString());
        Assert.Equal("Before", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(12, body.RootElement.GetProperty("spec").GetProperty("windowCount").GetInt32());
    }

    [Fact]
    public async Task A_spec_that_is_not_an_object_is_refused()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture);
        using var owner = await OwnerClientAsync(factory, ledger);

        var response = await owner.PostAsJsonAsync(
            Url(ledger.LedgerId), new { name = "Bad", spec = "not an object" });

        // The database CHECK says the same thing, but a 400 names the field;
        // letting it reach Postgres would surface as a 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_private_report_is_invisible_to_another_member_of_the_same_ledger()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");

        await using (var ownerDb = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            await new SavedReportsRepository(ownerDb, TimeProvider.System).CreateAsync(
                ledger.LedgerId, ledger.UserId, "My private cut", SpecJson, "private");
        }

        // Same ledger, an EDITOR grant, and still nothing — the default is
        // private because sharing is a decision someone makes, not one they
        // discover they made.
        await using var otherDb = _fixture.NewAppDbContextAsUser(otherUserId);
        var theirs = await new SavedReportsRepository(otherDb, TimeProvider.System).ListAsync(ledger.LedgerId);
        Assert.Empty(theirs);
    }

    [Fact]
    public async Task A_shared_report_is_readable_by_the_ledger_and_writable_only_by_its_owner()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var otherUserId = await ledger.AddMemberAsync("editor");

        Guid reportId;
        await using (var ownerDb = _fixture.NewAppDbContextAsUser(ledger.UserId))
        {
            var created = await new SavedReportsRepository(ownerDb, TimeProvider.System).CreateAsync(
                ledger.LedgerId, ledger.UserId, "Household spending", SpecJson, "ledger");
            reportId = created.Id;
        }

        await using (var otherDb = _fixture.NewAppDbContextAsUser(otherUserId))
        {
            var repo = new SavedReportsRepository(otherDb, TimeProvider.System);

            // Readable.
            Assert.NotNull(await repo.GetAsync(ledger.LedgerId, reportId));

            // Not writable. "visibility = ledger" means readable-by, not
            // writable-by, and the policy's WITH CHECK is what enforces the
            // difference — without it any member could edit a shared report.
            Assert.Null(await repo.UpdateAsync(
                ledger.LedgerId, reportId, otherUserId, "Renamed by someone else", null, null));
            Assert.False(await repo.DeleteAsync(ledger.LedgerId, reportId, otherUserId));
        }

        // And the owner still can, so the refusals above are about WHO is
        // asking rather than about the report being unreachable.
        await using var ownerAgain = _fixture.NewAppDbContextAsUser(ledger.UserId);
        Assert.NotNull(await new SavedReportsRepository(ownerAgain, TimeProvider.System).UpdateAsync(
            ledger.LedgerId, reportId, ledger.UserId, "Household spending v2", null, null));
    }
}
