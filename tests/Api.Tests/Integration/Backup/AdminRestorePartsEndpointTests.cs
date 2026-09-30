using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Mvc.Testing;

using Coffer.Api.Backup;
using Coffer.Api.Endpoints;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Backup;

/// <summary>
/// The multi-part restore upload (ADR-0101):
/// <c>POST /api/admin/backups/restore/parts</c>, then a restore that names those
/// parts with <c>useUploadedParts=true</c>.
/// <para>It exists because the one cap an operator cannot raise — Cloudflare's
/// 100 MB proxied body on Free and Pro — is smaller than a real
/// <c>.cofferbak</c>. What these tests are really protecting is the ASSEMBLY:
/// a set of parts stitched together wrong produces an archive that fails to
/// decrypt, which is indistinguishable from a wrong passphrase, on the one day
/// an operator has no patience for a misleading error.</para>
/// Staging is a shared filesystem singleton, so each test clears it (the
/// collection runs sequentially).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminRestorePartsEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AdminRestorePartsEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed class FakeRestarter : IApplicationRestarter
    {
        public bool Requested { get; private set; }
        public void RequestRestart() => Requested = true;
    }

    private static byte[] MakeArtifact(string passphrase) =>
        MakeArtifact(passphrase, Encoding.UTF8.GetBytes("fake-pg_dump-archive-bytes"));

    private static byte[] MakeArtifact(string passphrase, byte[] plaintext)
    {
        using var plain = new MemoryStream(plaintext);
        using var enc = new MemoryStream();
        BackupCrypto.EncryptAsync(plain, passphrase, enc).GetAwaiter().GetResult();
        return enc.ToArray();
    }

    /// <summary>
    /// A real artifact that genuinely spans more than one part.
    /// </summary>
    /// <remarks>
    /// Padding a small artifact to length would have been cheaper and would have
    /// proved nothing: the trailing bytes are not part of the ciphertext, so the
    /// reassembled file fails to decrypt whether or not the seam is correct —
    /// which is precisely the failure this feature must not produce. The
    /// plaintext is incompressible-agnostic (pg_dump output is not compressed by
    /// BackupCrypto), so its size carries through.
    /// </remarks>
    private static byte[] MakeOversizeArtifact(string passphrase)
    {
        var plaintext = new byte[TestPartSize + 4096];
        // Deterministic filler — a fixed-seed pattern rather than random, so a
        // failure is reproducible and a diff points at an offset.
        for (var i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i % 251);
        return MakeArtifact(passphrase, plaintext);
    }

    /// <summary>
    /// The size these tests cut at. Deliberately NOT read from the server's
    /// configuration: the part size is the CLIENT's to declare (ADR-0101), and
    /// a test that took the server's value could not tell the difference
    /// between a server that honours the declaration and one that ignores it.
    /// </summary>
    private const long TestPartSize = 1024 * 1024;

    private static MultipartFormDataContent PartForm(
        byte[] bytes, int part, int partCount, long partSize = TestPartSize)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new MultipartFormDataContent
        {
            { new ByteArrayContent(bytes), "archive", "part.cofferbak" },
            { new StringContent(part.ToString(inv)), "part" },
            { new StringContent(partCount.ToString(inv)), "partCount" },
            { new StringContent(partSize.ToString(inv)), "partSizeBytes" },
        };
    }

    private static MultipartFormDataContent RestoreFromParts(string passphrase) =>
        new()
        {
            { new StringContent("true"), "useUploadedParts" },
            { new StringContent(passphrase), "passphrase" },
            { new StringContent(AdminBackupsEndpoints.RestoreConfirmPhrase), "confirm" },
        };

    private static async Task<string?> CodeOf(HttpResponseMessage resp)
    {
        var problem = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return problem is not null && problem.TryGetValue("code", out var code) ? code?.ToString() : null;
    }

    [Fact]
    public async Task A_single_part_upload_restores_exactly_the_bytes_that_were_sent()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        var artifact = MakeArtifact("pw");
        using (var form = PartForm(artifact, part: 1, partCount: 1))
        {
            var accepted = await client.PostAsync("/api/admin/backups/restore/parts", form);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var restore = RestoreFromParts("pw");
        var resp = await client.PostAsync("/api/admin/backups/restore", restore);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(BootstrapRestoreStaging.IsPending());
        // The bytes matter more than the status: an assembly that reordered or
        // dropped anything would still stage SOMETHING, and only fail later at
        // decrypt, wearing a wrong-passphrase error.
        Assert.Equal(artifact, await File.ReadAllBytesAsync(BootstrapRestoreStaging.ArchivePath));
        Assert.True(fake.Requested);
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task Parts_are_refused_out_of_order()
    {
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        // Part 2 with nothing received: the server expects part 1, and knows so
        // from the byte count rather than from anything the client asserts.
        using var form = PartForm([1, 2, 3], part: 2, partCount: 2);
        var resp = await client.PostAsync("/api/admin/backups/restore/parts", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-restore-invalid", await CodeOf(resp));
        Assert.Null(BootstrapRestoreStaging.UploadedBytes());
    }

    [Fact]
    public async Task A_short_non_final_part_is_refused()
    {
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        // Every part but the last must be exactly PartSizeBytes — that invariant
        // is what makes "which part comes next" derivable from the byte count, so
        // accepting a short one here would silently break ordering for the rest.
        using var form = PartForm([1, 2, 3], part: 1, partCount: 3);
        var resp = await client.PostAsync("/api/admin/backups/restore/parts", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-restore-invalid", await CodeOf(resp));
        Assert.Null(BootstrapRestoreStaging.UploadedBytes());
    }

    [Fact]
    public async Task Restoring_with_no_uploaded_parts_is_refused_and_nothing_is_staged()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        using var restore = RestoreFromParts("pw");
        var resp = await client.PostAsync("/api/admin/backups/restore", restore);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("backup-restore-invalid", await CodeOf(resp));
        Assert.False(BootstrapRestoreStaging.IsPending());
        Assert.False(fake.Requested);
    }

    [Fact]
    public async Task Naming_uploaded_parts_and_an_upload_together_is_refused()
    {
        BootstrapRestoreStaging.Clear();
        await using var factory = new ApiFactory(_fixture);
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(MakeArtifact("pw")), "archive", "dr.cofferbak" },
            { new StringContent("true"), "useUploadedParts" },
            { new StringContent("pw"), "passphrase" },
            { new StringContent(AdminBackupsEndpoints.RestoreConfirmPhrase), "confirm" },
        };
        var resp = await client.PostAsync("/api/admin/backups/restore", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.False(BootstrapRestoreStaging.IsPending());
    }

    [Fact]
    public async Task Restarting_at_part_one_abandons_a_stalled_upload_rather_than_appending_to_it()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // A first attempt that went nowhere — the browser was closed, the laptop
        // slept. Without this, the retry appends to the corpse and the archive is
        // the two runs concatenated.
        using (var abandoned = PartForm(Encoding.UTF8.GetBytes("junk from a dead attempt"), 1, 1))
            await client.PostAsync("/api/admin/backups/restore/parts", abandoned);

        var artifact = MakeArtifact("pw");
        using (var retry = PartForm(artifact, part: 1, partCount: 1))
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsync("/api/admin/backups/restore/parts", retry)).StatusCode);

        using var restore = RestoreFromParts("pw");
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsync("/api/admin/backups/restore", restore)).StatusCode);
        Assert.Equal(artifact, await File.ReadAllBytesAsync(BootstrapRestoreStaging.ArchivePath));
        BootstrapRestoreStaging.Clear();
    }

    [Fact]
    public async Task A_multi_part_upload_reassembles_in_order()
    {
        BootstrapRestoreStaging.Clear();
        var fake = new FakeRestarter();
        await using var factory = new ApiFactory(_fixture).WithService<IApplicationRestarter>(_ => fake);
        using var client = factory.CreateClient();

        // A real two-part artifact: the first part is exactly PartSizeBytes (the
        // invariant the ordering check leans on) and the second is the remainder.
        // 64 MiB of request body per part — slow, and the only way to exercise
        // the boundary the whole feature exists for.
        var artifact = MakeOversizeArtifact("pw");
        Assert.True(artifact.Length > TestPartSize);

        var first = artifact[..(int)TestPartSize];
        var second = artifact[(int)TestPartSize..];

        using (var form = PartForm(first, part: 1, partCount: 2))
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsync("/api/admin/backups/restore/parts", form)).StatusCode);

        // Skipping ahead is refused: the server expects part 2, and knows so from
        // the byte count rather than from anything the client claims.
        using (var skip = PartForm(second, part: 3, partCount: 3))
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await client.PostAsync("/api/admin/backups/restore/parts", skip)).StatusCode);

        using (var form = PartForm(second, part: 2, partCount: 2))
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PostAsync("/api/admin/backups/restore/parts", form)).StatusCode);

        // Re-sending the LAST part is refused. A client whose response was lost
        // after the server committed retries exactly this, and appending it twice
        // would corrupt the archive in a way that only surfaces at decrypt.
        using (var replay = PartForm(second, part: 2, partCount: 2))
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                (await client.PostAsync("/api/admin/backups/restore/parts", replay)).StatusCode);

        Assert.Equal(artifact.Length, BootstrapRestoreStaging.UploadedBytes());

        using var restore = RestoreFromParts("pw");
        var resp = await client.PostAsync("/api/admin/backups/restore", restore);

        // 202 means the staged archive DECRYPTED under the passphrase, which is
        // the whole claim: the two parts were concatenated in the right order
        // with nothing lost or doubled at the seam. Byte equality alone would
        // not prove it — the restore endpoint is what actually opens the file.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(artifact, await File.ReadAllBytesAsync(BootstrapRestoreStaging.ArchivePath));
        BootstrapRestoreStaging.Clear();
    }
}
