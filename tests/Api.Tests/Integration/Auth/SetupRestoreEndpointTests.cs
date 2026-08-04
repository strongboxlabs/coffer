using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Backup;
using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Services;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Auth;

/// <summary>
/// The bootstrap restore endpoint (ADR-0061): `POST /api/auth/setup/{token}/restore`.
/// Pre-auth, bootstrap-token-gated; stages an uploaded .cofferbak + passphrase
/// (after verifying the passphrase opens it) and requests a restart. The actual
/// over-the-DB apply happens at the next boot (Program.cs) and isn't replayable
/// in WebApplicationFactory; these cover the endpoint contract. A fake
/// IApplicationRestarter records the restart request without stopping the host.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SetupRestoreEndpointTests
{
    private readonly PostgresFixture _fixture;

    public SetupRestoreEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed class FakeRestarter : IApplicationRestarter
    {
        public bool Requested { get; private set; }
        public void RequestRestart() => Requested = true;
    }

    private async Task<string> SeedTokenAsync()
    {
        await using var db = _fixture.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE bootstrap_tokens CASCADE;");
        var (plaintext, hash) = BootstrapTokenService.GenerateToken();
        db.BootstrapTokens.Add(new BootstrapTokenRow { TokenHash = hash, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return plaintext;
    }

    private static byte[] MakeArtifact(string passphrase)
    {
        using var plain = new MemoryStream(Encoding.UTF8.GetBytes("fake-pg_dump-archive-bytes"));
        using var enc = new MemoryStream();
        BackupCrypto.EncryptAsync(plain, passphrase, enc).GetAwaiter().GetResult();
        return enc.ToArray();
    }

    private static MultipartFormDataContent Multipart(byte[] archive, string? passphrase)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(archive), "archive", "dr.cofferbak");
        if (passphrase is not null) content.Add(new StringContent(passphrase), "passphrase");
        return content;
    }

    [Fact]
    public async Task Stages_the_backup_and_requests_restart_on_a_valid_upload()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        const string pass = "a-good-passphrase";
        using var content = Multipart(MakeArtifact(pass), pass);
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());   // archive + passphrase + marker staged
        Assert.True(fake.Requested);                         // restart requested
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task Rejects_a_wrong_passphrase_and_stages_nothing()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // Encrypted under one passphrase, uploaded with a different one.
        using var content = Multipart(MakeArtifact("the-real-pass"), "the-wrong-pass");
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal("backup-passphrase-invalid", doc.RootElement.GetProperty("code").GetString());
        Assert.False(BootstrapRestoreStaging.IsPending());   // cleared
        Assert.False(fake.Requested);                         // no restart
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task Rejects_an_invalid_token()
    {
        BootstrapRestoreStaging.Clear();
        await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        const string pass = "a-good-passphrase";
        using var content = Multipart(MakeArtifact(pass), pass);
        var resp = await client.PostAsync("/api/auth/setup/not-a-real-token/restore", content);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(fake.Requested);
    }

    [Fact]
    public async Task Rejects_a_request_missing_the_passphrase()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("x"), passphrase: null);
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.False(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }
}
