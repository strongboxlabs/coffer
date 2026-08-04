using System.Globalization;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Coffer.Api.Contracts;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Meta;

/// <summary>
/// ADR-0044 — the installation-wide version endpoint that feeds the
/// SPA's About panel. Verifies the DB axis reflects the latest applied
/// migration, the API axis is stamped, and the endpoint is gated behind
/// authentication.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MetaVersionTests
{
    private readonly PostgresFixture _fixture;

    public MetaVersionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(
        ApiFactory factory, SyntheticLedger ledger)
    {
        var cookieValue = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookieValue}");
        return client;
    }

    [Fact]
    public async Task Reports_db_schema_version_matching_the_latest_applied_migration()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);

        // Expected DB version = the latest migration DbUp recorded in
        // this bootstrapped database. Derive it from the journal the
        // same way the endpoint does, so the test tracks new migrations
        // automatically instead of hard-coding today's number.
        string latestScript;
        await using (var db = _fixture.NewDbContext())
        {
            latestScript = await db.SchemaMigrations.AsNoTracking()
                .OrderByDescending(m => m.SchemaVersionsId)
                .Select(m => m.ScriptName)
                .FirstAsync();
        }
        var expectedVersion = LeadingNumber(latestScript);
        Assert.True(expectedVersion > 0,
            $"fixture should have applied numbered migrations; got '{latestScript}'");

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);

        var resp = await client.GetAsync("/api/meta/version");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.NotNull(body);

        // DB axis mirrors the journal.
        Assert.Equal(expectedVersion, body!.Db.SchemaVersion);
        Assert.StartsWith(
            expectedVersion.ToString(CultureInfo.InvariantCulture), body.Db.Script);
        // The script display name is cleaned of path + .sql extension.
        Assert.DoesNotContain('/', body.Db.Script);
        Assert.DoesNotContain('\\', body.Db.Script);
        Assert.False(body.Db.Script.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));

        // API axis: version + commit always present; build is the git
        // commit count (>= 0, or 0 on a .git-less build). Don't assert
        // an exact SHA — it changes every commit.
        Assert.False(string.IsNullOrWhiteSpace(body.Api.Version));
        Assert.False(string.IsNullOrWhiteSpace(body.Api.Commit));
        Assert.True(body.Api.Build >= 0);
    }

    [Fact]
    public async Task Requires_authentication()
    {
        // Anonymous request to a RequireAuthorization endpoint → 401.
        await SyntheticLedger.CreateAsync(_fixture);
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var resp = await client.GetAsync("/api/meta/version");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // Mirror of the endpoint's parse: strip any path + .sql extension,
    // then read the leading NNN.
    private static int LeadingNumber(string script)
    {
        var name = script;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }
}
