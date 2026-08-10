using System.Net;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Backup;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The authenticated-admin restore endpoint (ADR-0071 D3):
/// <c>POST /api/admin/backups/restore</c>. Covers the new guards — RequireAdmin,
/// the typed-confirmation gate, and the D4 KEK-mismatch check — up to the
/// stage → restart request; the over-the-DB apply happens at the next boot and
/// isn't replayable here. A fake IApplicationRestarter records the restart.
/// Staging is a shared filesystem singleton, so each test clears it (the
/// collection runs sequentially).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminRestoreEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AdminRestoreEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed class FakeRestarter : IApplicationRestarter
    {
        public bool Requested { get; private set; }
        public void RequestRestart() => Requested = true;
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
        byte[] archive, string passphrase, string? confirm, bool acknowledgeKek = false,
        string? sourceKeyBase64 = null)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(archive), "archive", "dr.cofferbak" },
            { new StringContent(passphrase), "passphrase" },
        };
        if (confirm is not null) content.Add(new StringContent(confirm), "confirm");
        if (acknowledgeKek) content.Add(new StringContent("true"), "acknowledgeKekMismatch");
        if (sourceKeyBase64 is not null)
            content.Add(new StringContent(sourceKeyBase64), "sourceMasterKeyBase64");
        return content;
    }

    /// <summary>A 32-byte key that isn't the fixture's, standing in for another
    /// install's.</summary>
    private static byte[] SourceKeyBytes()
    {
        var b = new byte[32];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(i + 7);
        return b;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    [Fact]
    public async Task Requires_the_typed_confirmation_phrase()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();   // dev-auth ⇒ admin

        using var content = Multipart(MakeArtifact("pw"), "pw", confirm: "not the phrase");
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-restore-confirm-required", await CodeOf(resp));
        Assert.False(fake.Requested);
        Assert.False(BootstrapRestoreStaging.IsPending());
    }

    [Fact]
    public async Task Flags_a_KEK_mismatch_when_not_acknowledged()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // A v2 artifact whose fingerprint can't match the test install's KEK.
        var artifact = MakeArtifact("pw", fingerprint: new byte[16]);
        using var content = Multipart(artifact, "pw", confirm: AdminBackupsEndpoints.RestoreConfirmPhrase);
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-kek-mismatch", await CodeOf(resp));
        Assert.False(fake.Requested);
        Assert.False(BootstrapRestoreStaging.IsPending());   // cleared
    }

    [Fact]
    public async Task Stages_and_requests_restart_on_a_confirmed_valid_restore()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // v1 artifact (no fingerprint ⇒ KEK check skipped), correct passphrase + confirm.
        using var content = Multipart(MakeArtifact("pw"), "pw", confirm: AdminBackupsEndpoints.RestoreConfirmPhrase);
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        Assert.True(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }

    // --- adopt the source install's key (ADR-0092 D4) ------------------------

    [Fact]
    public async Task A_malformed_source_key_is_rejected_and_nothing_is_staged()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw"), "pw",
            confirm: AdminBackupsEndpoints.RestoreConfirmPhrase,
            sourceKeyBase64: "not base64 at all!!");
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-source-key-invalid", await CodeOf(resp));
        Assert.False(fake.Requested);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_wrong_length_source_key_is_rejected()
    {
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => new FakeRestarter());
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw"), "pw",
            confirm: AdminBackupsEndpoints.RestoreConfirmPhrase,
            sourceKeyBase64: Convert.ToBase64String(new byte[16]));   // AES-128, not 256
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-source-key-invalid", await CodeOf(resp));
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_source_key_that_does_not_match_the_archives_fingerprint_is_rejected()
    {
        // The check that earns this feature its keep: catch a pasted-the-wrong-key
        // mistake BEFORE the restore replaces everything, rather than after, when the
        // install can no longer open its own secrets.
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // v2 archive fingerprinted for one key; a different key supplied.
        var artifact = MakeArtifact("pw", fingerprint: KekFingerprint.Compute(SourceKeyBytes()));
        using var content = Multipart(artifact, "pw",
            confirm: AdminBackupsEndpoints.RestoreConfirmPhrase,
            sourceKeyBase64: Convert.ToBase64String(new byte[32]));   // not SourceKeyBytes()
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-source-key-invalid", await CodeOf(resp));
        Assert.False(fake.Requested);
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task A_matching_source_key_is_staged_without_needing_the_mismatch_acknowledgement()
    {
        // A verified source key makes the mismatch warning moot: the whole point is
        // that the secrets WILL carry over, so the operator shouldn't have to
        // acknowledge losing them.
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        var sourceKey = SourceKeyBytes();
        var artifact = MakeArtifact("pw", fingerprint: KekFingerprint.Compute(sourceKey));
        using var content = Multipart(artifact, "pw",
            confirm: AdminBackupsEndpoints.RestoreConfirmPhrase,
            acknowledgeKek: false,
            sourceKeyBase64: Convert.ToBase64String(sourceKey));
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        Assert.True(BootstrapRestoreStaging.HasSourceKey());
        Assert.Equal(Convert.ToBase64String(sourceKey), BootstrapRestoreStaging.ReadSourceKey());
        Assert.True(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task An_unfingerprinted_v1_archive_accepts_a_source_key_unverified()
    {
        // Nothing to check against, so it proceeds — and if the key is wrong, D5's
        // reconciliation on the post-adopt boot clears what won't open, which is the
        // same outcome as not supplying one.
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture)
            .WithService<IApplicationRestarter>(_ => new FakeRestarter());
        using var client = factory.CreateClient();

        using var content = Multipart(MakeArtifact("pw"), "pw",
            confirm: AdminBackupsEndpoints.RestoreConfirmPhrase,
            sourceKeyBase64: Convert.ToBase64String(SourceKeyBytes()));
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.HasSourceKey());
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task Clear_shreds_the_staged_source_key()
    {
        // It's key material on disk; Clear() must not leave it behind.
        BootstrapRestoreStaging.EnsureDir();
        await BootstrapRestoreStaging.StageSourceKeyAsync(Convert.ToBase64String(SourceKeyBytes()));
        Assert.True(BootstrapRestoreStaging.HasSourceKey());

        BootstrapRestoreStaging.Clear();

        Assert.False(BootstrapRestoreStaging.HasSourceKey());
    }

    [Fact]
    public async Task Non_admin_cookie_is_forbidden()
    {
        BootstrapRestoreStaging.Clear();
        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        await using var factory = new ApiFactory(_fixture).WithoutDevAuth();
        var cookie = await alice.IssueSessionCookieAsync();
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        using var content = Multipart(MakeArtifact("pw"), "pw", confirm: AdminBackupsEndpoints.RestoreConfirmPhrase);
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(BootstrapRestoreStaging.IsPending());
    }
}
