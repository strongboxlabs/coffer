using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// Pinning the contract of the master-KEK loader (ADR-0026, sourced per ADR-0092 D1,
/// narrowed by ADR-0094): the key file is the only source, and every departure from the
/// expected shape fails fast.
/// </summary>
/// <remarks>
/// The <c>COFFER_MASTER_KEK_BASE64</c> env var and its write-through migration are gone
/// (ADR-0094), so the tests that pinned env-vs-file precedence went with them. The id
/// fallback is still passed in rather than read from the environment, so nothing here
/// touches process-global state — that raced the integration harness, which runs in
/// parallel collections.
/// </remarks>
public sealed class MasterKeyLoaderTests : IDisposable
{
    private static readonly byte[] Key32 = BuildKey();
    private static byte[] BuildKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    private static string Key32Base64 => Convert.ToBase64String(Key32);

    private readonly string _dir = Directory.CreateTempSubdirectory("coffer-kek-tests").FullName;

    private MasterKeyStore NewStore() => new(Path.Combine(_dir, $"{Guid.NewGuid():N}.key"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // --- file source (the steady state) ------------------------------------

    [Fact]
    public void Resolves_a_valid_key_from_the_file()
    {
        var store = NewStore();
        store.Write(Key32Base64);

        var result = MasterKeyLoader.Resolve(store, fallbackKeyId: "v1");

        Assert.NotNull(result.Key);
        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal("v1", result.Key.Id);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
    }

    [Fact]
    public void Prefers_the_files_own_id_over_the_fallback()
    {
        // Rotation and minting write key and id together, so the pairing on disk is
        // authoritative and the fallback is not consulted.
        var store = NewStore();
        store.Write(Key32Base64, "v7");

        var result = MasterKeyLoader.Resolve(store, fallbackKeyId: "v1");

        Assert.Equal("v7", result.Key!.Id);
    }

    [Fact]
    public void Tolerates_a_trailing_newline_in_an_operator_placed_file()
    {
        // An injected secret (Docker/k8s/Key Vault) or a hand-written file routinely
        // ends with a newline. Failing only because of that would be a miserable
        // diagnosis.
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, Key32Base64 + "\n");

        var result = MasterKeyLoader.Resolve(store, fallbackKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
    }

    [Fact]
    public void Reports_no_key_when_the_file_holds_none()
    {
        // Not an exception: ADR-0092 D3 lets a virgin install proceed and mint one in
        // the setup ceremony. Whether "none" is fatal is the caller's gate — it refuses
        // if the database already holds wrapped material.
        var result = MasterKeyLoader.Resolve(NewStore(), fallbackKeyId: "v1");

        Assert.Null(result.Key);
        Assert.Equal(MasterKeyLoader.KeySource.None, result.Source);
    }

    [Fact]
    public void Throws_when_the_file_is_not_valid_base64()
    {
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, "not!valid!base64!at!all!");

        Assert.Throws<FormatException>(
            () => MasterKeyLoader.Resolve(store, fallbackKeyId: "v1"));
    }

    [Fact]
    public void Throws_when_the_files_decoded_length_is_wrong()
    {
        // 16 bytes — AES-128, not AES-256.
        var store = NewStore();
        store.Write(Convert.ToBase64String(new byte[16]));

        var ex = Assert.Throws<ArgumentException>(
            () => MasterKeyLoader.Resolve(store, fallbackKeyId: "v1"));
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void Rejects_a_blank_fallback_id()
        => Assert.Throws<ArgumentException>(
            () => MasterKeyLoader.Resolve(NewStore(), fallbackKeyId: "  "));

    [Fact]
    public void Uses_the_fallback_id_for_a_hand_written_file_with_no_id_line()
    {
        // ADR-0094 makes this the documented recovery route for an install whose key
        // only ever lived in .env: write the key to the file by hand. Such a file has no
        // `id=` line, so the fallback has to keep working — it is the difference between
        // recovering and being told to abandon the sealed secrets.
        var store = NewStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.Path)!);
        File.WriteAllText(store.Path, Key32Base64 + Environment.NewLine);

        var result = MasterKeyLoader.Resolve(store, fallbackKeyId: "v3");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal("v3", result.Key.Id);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
    }
}
