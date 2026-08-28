using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Coffer.Api.Provisioning;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Import;

/// <summary>
/// End-to-end coverage for the Moneydance import endpoints, which had none.
/// </summary>
/// <remarks>
/// <para>
/// The point of these is LIFETIME, not mapping (the mappers are covered in
/// Importer.Moneydance.Tests). An <c>MdExport</c> owns the parsed
/// <c>JsonDocument</c> and every <c>MdItem</c> is a view into it, so the document
/// has to stay alive for exactly as long as the import reads from it — and the
/// in-app import is fire-and-forget: <c>ImportJobRunner</c> hands the export to a
/// <c>Task.Run</c> that outlives the HTTP request. Disposing in the endpoint (the
/// obvious <c>using</c>) would tear the document out mid-import, and NOTHING else
/// in the suite would catch it: it fails only on the real background path.
/// </para>
/// <para>
/// The fixture is the embedded demo export — a real 195-item file that is known to
/// survive the whole pipeline, so a failure here is about lifetime rather than
/// about hand-written JSON being subtly invalid.
/// </para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MoneydanceImportEndpointTests
{
    private readonly PostgresFixture _fixture;

    public MoneydanceImportEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Preview_reports_item_counts_and_writes_nothing()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        using var content = Upload(ledgerName: null);
        var response = await client.PostAsync("/api/imports/moneydance/preview", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Preview owns the document for the duration of the request and disposes it
        // before returning — so the DTO must already be materialized. A lazy
        // projection over the items would throw ObjectDisposedException here.
        Assert.Equal(DemoItemCount, body.RootElement.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Start_hands_the_export_to_the_background_run_and_imports_it()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        var ledgerName = "Import lifetime " + Guid.NewGuid().ToString("N")[..8];
        using var content = Upload(ledgerName);
        var started = await client.PostAsync("/api/imports/moneydance", content);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var startedBody = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var jobId = startedBody.RootElement.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrEmpty(jobId));

        var (state, ledgerId, error) = await PollToCompletionAsync(client, jobId!);

        // "failed" with an ObjectDisposedException in the logs is the shape this
        // test exists to catch, so surface the error rather than asserting bare.
        Assert.Equal("succeeded", state);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(ledgerId), "a successful import must report its new ledger");
    }

    private static async Task<(string? State, string? LedgerId, string? Error)> PollToCompletionAsync(
        HttpClient client, string jobId)
    {
        // The import is a background task, so this is a genuine wait, not a
        // convenience. 195 items is quick; the ceiling is only here so a hang fails
        // the test instead of hanging the lane.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var response = await client.GetAsync($"/api/imports/moneydance/{jobId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var state = body.RootElement.GetProperty("state").GetString();
            if (state != "running")
            {
                return (state,
                    Text(body.RootElement, "ledgerId"),
                    Text(body.RootElement, "error"));
            }

            await Task.Delay(250);
        }

        Assert.Fail("The import job never left the 'running' state.");
        return (null, null, null);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private const int DemoItemCount = 195;

    private static MultipartFormDataContent Upload(string? ledgerName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(DemoExportBytes());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "moneydance-export-demo.json");
        if (ledgerName is not null)
            content.Add(new StringContent(ledgerName), "ledgerName");
        return content;
    }

    /// <summary>
    /// The same embedded resource demo provisioning imports, read through the
    /// assembly rather than copied into the test project so the two cannot drift.
    /// </summary>
    private static byte[] DemoExportBytes()
    {
        const string resource = "Coffer.Api.Provisioning.moneydance-export-demo.json";
        using var stream = typeof(ProvisioningService).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
