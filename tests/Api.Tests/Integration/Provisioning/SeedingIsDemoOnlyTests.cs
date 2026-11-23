using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Tests.Integration.Infra;
using Coffer.Api.Tests.Support;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// The examples belong to the hub's Demo alone: a person who uploads the very document the
/// Demo imports, through the Moneydance import, gets that history and nothing the examples
/// phase adds, and no record of one.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SeedingIsDemoOnlyTests : DatabaseTest
{
    public SeedingIsDemoOnlyTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task A_user_upload_of_the_Demo_s_document_gets_no_examples_and_no_examples_record()
    {
        await using var factory = DemoSeedingHarness.Factory(Db);
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(DemoTestExports.TinyBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "demo-export.json");
        content.Add(new StringContent("Uploaded " + Guid.NewGuid().ToString("N")[..8]), "ledgerName");
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var ledgerId = await SucceededLedgerAsync(client, startedBody.RootElement.GetProperty("jobId").GetString()!);

        await using var db = Db.NewDbContext();
        // The history is there: this is the import the Demo's examples would follow.
        Assert.True(await db.TxnHeaders.AnyAsync(h => h.LedgerId == ledgerId && h.ExternalId != null));

        // Its one record is the import's own, and none of the examples was added.
        Assert.Equal(
            [LedgerOperationsRepository.MoneydanceImportProviderKey],
            await db.LedgerOperations.AsNoTracking().Where(o => o.LedgerId == ledgerId).Select(o => o.ProviderKey).ToListAsync());
        Assert.False(await db.BudgetTargets.AnyAsync(t => t.LedgerId == ledgerId));
        Assert.False(await db.SavedReports.AnyAsync(r => r.LedgerId == ledgerId));
        Assert.False(await db.ReportPins.AnyAsync(p => p.LedgerId == ledgerId));
        Assert.False(await db.AccountFolders.AnyAsync(f => f.LedgerId == ledgerId));
        Assert.False(await db.UserPreferences.AnyAsync(p => p.LedgerId == ledgerId));
        Assert.False(await db.RecurringOccurrenceExceptions.AnyAsync(e => e.LedgerId == ledgerId));
        Assert.False(await db.RecurringTransactions.AnyAsync(r => r.LedgerId == ledgerId && r.ExternalId == null));
    }

    /// <summary>The import job's ledger once it has succeeded, for at most 60 s.</summary>
    private static async Task<Guid> SucceededLedgerAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var body = JsonDocument.Parse(await client.GetStringAsync($"/api/imports/moneydance/{jobId}"));
            var state = body.RootElement.GetProperty("state").GetString();
            if (state != "running")
            {
                Assert.Equal("succeeded", state);
                return Guid.Parse(body.RootElement.GetProperty("ledgerId").GetString()!);
            }
            await Task.Delay(250);
        }
        Assert.Fail("The import job never left the 'running' state.");
        return default;
    }
}
