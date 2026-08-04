using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Entities;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.LedgerOperations;

/// <summary>
/// The ledger-wide provider-activity timeline endpoint (ADR-0055 slice C):
/// <c>GET /api/ledgers/{id}/ledger-operations</c> lists runs across families,
/// newest first, filterable by provider + recency.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LedgerOperationsEndpointsTests
{
    private readonly PostgresFixture _fixture;

    public LedgerOperationsEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<HttpClient> AuthedClientAsync(ApiFactory factory, SyntheticLedger ledger)
    {
        var cookie = await ledger.IssueSessionCookieAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");
        return client;
    }

    [Fact]
    public async Task Lists_runs_across_families_filterable_by_provider_and_days()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        await SeedRunAsync(ledger, "ingest", "simplefin", "manual", DateTime.UtcNow.AddHours(-1));
        await SeedRunAsync(ledger, "ingest", "ofx", "file-upload", DateTime.UtcNow.AddHours(-2));
        await SeedRunAsync(ledger, "quote", "quote-refresh", "manual", DateTime.UtcNow.AddHours(-3));
        await SeedRunAsync(ledger, "ingest", "simplefin", "manual", DateTime.UtcNow.AddDays(-10)); // old

        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        using var client = await AuthedClientAsync(factory, ledger);
        var basePath = $"/api/ledgers/{ledger.LedgerId}/ledger-operations";

        // No filter → all four, newest first.
        var all = await GetRunsAsync(client, basePath);
        Assert.Equal(4, all.Count);
        Assert.True(all[0].StartedAt >= all[1].StartedAt);
        Assert.Contains(all, r => r.Family == "quote");
        Assert.Contains(all, r => r.Family == "ingest");

        // Filter by provider → only the two SimpleFIN runs.
        var simplefin = await GetRunsAsync(client, $"{basePath}?provider=simplefin");
        Assert.Equal(2, simplefin.Count);
        Assert.All(simplefin, r => Assert.Equal("simplefin", r.ProviderKey));

        // Filter by recency → the 10-day-old run drops out.
        var recent = await GetRunsAsync(client, $"{basePath}?days=1");
        Assert.Equal(3, recent.Count);
    }

    private static async Task<List<LedgerOperationSummaryDto>> GetRunsAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<LedgerOperationSummaryDto>>())!;
    }

    private static async Task SeedRunAsync(
        SyntheticLedger ledger, string family, string providerKey, string triggeredVia, DateTime startedAt)
    {
        await using var db = ledger.NewDbContext();
        db.LedgerOperations.Add(new LedgerOperationRow
        {
            Id = Guid.NewGuid(),
            LedgerId = ledger.LedgerId,
            Family = family,
            ProviderKey = providerKey,
            TriggeredVia = triggeredVia,
            Status = "completed",
            StartedAt = startedAt,
            CompletedAt = startedAt,
        });
        await db.SaveChangesAsync();
    }
}
