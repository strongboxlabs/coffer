using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Backup;
using Coffer.Api.Crypto;
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

    private static byte[] MakeArtifact(string passphrase, byte[]? fingerprint = null)
    {
        using var plain = new MemoryStream(Encoding.UTF8.GetBytes("fake-pg_dump-archive-bytes"));
        using var enc = new MemoryStream();
        if (fingerprint is null)
            BackupCrypto.EncryptAsync(plain, passphrase, enc).GetAwaiter().GetResult();          // v1
        else
            BackupCrypto.EncryptAsync(plain, passphrase, enc, fingerprint).GetAwaiter().GetResult(); // v2
        return enc.ToArray();
    }

    private static MultipartFormDataContent Multipart(
        byte[] archive, string? passphrase, string? sourceKeyBase64 = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(archive), "archive", "dr.cofferbak");
        if (passphrase is not null) content.Add(new StringContent(passphrase), "passphrase");
        if (sourceKeyBase64 is not null)
            content.Add(new StringContent(sourceKeyBase64), "sourceMasterKeyBase64");
        return content;
    }

    /// <summary>A 32-byte key standing in for the lost install's.</summary>
    private static byte[] SourceKeyBytes()
    {
        var b = new byte[32];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(i + 11);
        return b;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    // --- the source install's master key (FU-074) ---------------------------
    //
    // This screen is the disaster-recovery path, and its install minted a new key, so
    // without the old one every sealed secret is cleared on the first boot. operations.md
    // tells the operator to paste the key here; there was nowhere to paste it.

    [Fact]
    public async Task An_upload_that_would_fill_the_data_volume_is_refused_and_nothing_is_staged()
    {
        // FU-072, on the pre-auth path a setup-token holder can reach.
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => fake)
            .WithConfig("Api:Backup:RestoreFreeSpaceReserveMb", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw-for-dr"), "pw-for-dr");
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-restore-invalid", await CodeOf(resp));
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(fake.Requested);
    }

    [Fact]
    public async Task A_matching_source_key_is_staged_for_adoption()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        var sourceKey = SourceKeyBytes();
        using var content = Multipart(
            MakeArtifact("pw-for-dr", fingerprint: KekFingerprint.Compute(sourceKey)), "pw-for-dr",
            sourceKeyBase64: Convert.ToBase64String(sourceKey));
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        Assert.True(BootstrapRestoreStaging.HasSourceKey());
        Assert.Equal(Convert.ToBase64String(sourceKey), BootstrapRestoreStaging.ReadSourceKey());
        Assert.True(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task A_source_key_that_did_not_seal_the_backup_is_refused_before_anything_changes()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        using var content = Multipart(
            MakeArtifact("pw-for-dr", fingerprint: KekFingerprint.Compute(SourceKeyBytes())), "pw-for-dr",
            sourceKeyBase64: Convert.ToBase64String(new byte[32]));   // not SourceKeyBytes()
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-source-key-invalid", await CodeOf(resp));
        Assert.False(fake.Requested);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_malformed_source_key_is_refused()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => new FakeRestarter());
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw-for-dr"), "pw-for-dr", sourceKeyBase64: "not base64!!");
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-source-key-invalid", await CodeOf(resp));
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_wrong_passphrase_clears_the_staged_source_key_too()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => new FakeRestarter());
        using var client = factory.CreateClient();

        var sourceKey = SourceKeyBytes();
        using var content = Multipart(
            MakeArtifact("the-real-pass", fingerprint: KekFingerprint.Compute(sourceKey)), "the-wrong-pass",
            sourceKeyBase64: Convert.ToBase64String(sourceKey));
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal("backup-passphrase-invalid", await CodeOf(resp));
        // A key left staged after a refused restore would be adopted by the next one.
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_key_left_staged_by_an_earlier_attempt_is_not_adopted_by_a_restore_without_one()
    {
        // FU-073's hazard on this path: a source key from an attempt that never finished
        // would be adopted by a later restore that supplied none, replacing the live key.
        BootstrapRestoreStaging.Clear();
        BootstrapRestoreStaging.EnsureDir();
        await BootstrapRestoreStaging.StageSourceKeyAsync(Convert.ToBase64String(SourceKeyBytes()));
        var token = await SeedTokenAsync();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => new FakeRestarter());
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw-for-dr"), "pw-for-dr");   // no key
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
        BootstrapRestoreStaging.Clear();
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

    // --- an artifact too big to send in one request (ADR-0101) --------------
    //
    // This is the path that needs parts most. Rolling a running install back
    // can read the artifact off its own disk; a FRESH install cannot — the
    // machine it is replacing is gone, the backup is on a laptop, and the only
    // way in is an upload through whatever proxy was just stood up. On
    // Cloudflare Free or Pro that proxy refuses a body over 100 MB and there
    // is no setting to change, so "send it whole" is not slow, it is impossible.

    /// <summary>The size these tests cut at — the CLIENT's to declare, so it is
    /// deliberately not read from the server's configuration.</summary>
    private const long TestPartSize = 1024 * 1024;

    private static MultipartFormDataContent PartForm(byte[] bytes, int part, int partCount)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "archive", "part.cofferbak" },
            { new StringContent(part.ToString(inv)), "part" },
            { new StringContent(partCount.ToString(inv)), "partCount" },
            { new StringContent(TestPartSize.ToString(inv)), "partSizeBytes" },
        };
    }

    private static byte[] MakeOversizeArtifact(string passphrase)
    {
        var plaintext = new byte[TestPartSize + 4096];
        for (var i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i % 251);
        using var plain = new MemoryStream(plaintext);
        using var enc = new MemoryStream();
        BackupCrypto.EncryptAsync(plain, passphrase, enc).GetAwaiter().GetResult();
        return enc.ToArray();
    }

    [Fact]
    public async Task A_fresh_install_restores_an_artifact_sent_in_parts()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        var artifact = MakeOversizeArtifact("pw");
        Assert.True(artifact.Length > TestPartSize);
        var first = artifact[..(int)TestPartSize];
        var second = artifact[(int)TestPartSize..];

        using (var form = PartForm(first, 1, 2))
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync($"/api/auth/setup/{token}/restore/parts", form)).StatusCode);
        using (var form = PartForm(second, 2, 2))
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync($"/api/auth/setup/{token}/restore/parts", form)).StatusCode);

        using var restore = new MultipartFormDataContent
        {
            { new StringContent("true"), "useUploadedParts" },
            { new StringContent("pw"), "passphrase" },
        };
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", restore);

        // The endpoint decrypts before staging, so success means the two parts
        // were joined in the right order with nothing lost at the seam.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        Assert.Equal(artifact, await File.ReadAllBytesAsync(BootstrapRestoreStaging.ArchivePath));
        Assert.True(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task A_short_part_set_is_refused_before_the_restart_not_after()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // Part 1 of 2 arrives; part 2 never does. The operator clicks Restore.
        var artifact = MakeOversizeArtifact("pw");
        using (var form = PartForm(artifact[..(int)TestPartSize], 1, 2))
            await client.PostAsync($"/api/auth/setup/{token}/restore/parts", form);

        using var restore = new MultipartFormDataContent
        {
            { new StringContent("true"), "useUploadedParts" },
            { new StringContent("pw"), "passphrase" },
        };
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", restore);

        // Caught by the passphrase verification, which runs BEFORE the restart —
        // a truncated archive does not decrypt. The wording will say passphrase,
        // which is why the client refuses an incomplete set by name first; what
        // matters here is that nothing was staged and no reboot was requested.
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(fake.Requested);
    }

    [Fact]
    public async Task Naming_uploaded_parts_when_none_arrived_is_refused()
    {
        BootstrapRestoreStaging.Clear();
        var token = await SeedTokenAsync();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // The upload never started, or died on its first part. Without an
        // explicit refusal this walks on to verify a passphrase against an
        // archive that is not there.
        using var restore = new MultipartFormDataContent
        {
            { new StringContent("true"), "useUploadedParts" },
            { new StringContent("pw"), "passphrase" },
        };
        var resp = await client.PostAsync($"/api/auth/setup/{token}/restore", restore);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(fake.Requested);
    }

    [Fact]
    public async Task Parts_need_a_valid_bootstrap_token()
    {
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        using var form = PartForm([1, 2, 3], 1, 1);
        var resp = await client.PostAsync("/api/auth/setup/not-a-token/restore/parts", form);

        // Pre-auth does not mean unguarded: the bootstrap token is the auth, and
        // without it this would be an unauthenticated write to the filesystem of
        // every Coffer install on the internet.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Null(BootstrapRestoreStaging.UploadedBytes());
    }

    [Fact]
    public async Task Setup_info_carries_the_configured_part_size()
    {
        var token = await SeedTokenAsync();
        await using var factory = new ApiFactory(_fixture)
            .WithConfig("Api:Backup:PartSizeMb", "7");
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/api/auth/setup/{token}/info");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync());
        // The pre-auth page has no other read to learn this from, and a value it
        // hardcoded would make Api:Backup:PartSizeMb silently not apply here.
        Assert.Equal(7L * 1024 * 1024,
            doc.RootElement.GetProperty("restorePartSizeBytes").GetInt64());
    }
}
