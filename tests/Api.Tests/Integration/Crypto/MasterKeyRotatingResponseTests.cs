using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Crypto;
using Coffer.Api.Tests.Integration.Infra;

using static Coffer.Api.Contracts.BackupContracts;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// What a caller sees when it tries to save a secret while the master key is being rotated
/// (FU-078): a 503 that says to retry, not a 500 — and nothing saved under the retired key.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MasterKeyRotatingResponseTests
{
    private readonly PostgresFixture _fixture;

    public MasterKeyRotatingResponseTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Saving_a_secret_after_a_rotation_committed_is_a_503_to_retry()
    {
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<MasterKeyWriteGate>().Retire();   // as a committed rotation leaves it

        var resp = await client.PutAsJsonAsync(
            "/api/admin/backups/passphrase", new SetBackupPassphraseRequest("a-good-passphrase"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.True(resp.Headers.RetryAfter is not null, "a 503 without Retry-After tells the client nothing");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("master-key-rotating", doc.RootElement.GetProperty("code").GetString());
    }
}
