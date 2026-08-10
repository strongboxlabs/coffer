using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// Pinning the contract of the master-KEK loader (ADR-0026, sourced per ADR-0092 D1):
/// fail-fast on every departure from the expected shape, and <b>the key file wins</b>
/// over the deprecated environment variable.
/// </summary>
/// <remarks>
/// These drive the <c>Resolve(store, envValue, envId)</c> overload rather than setting
/// the real environment variable. Mutating process-global env here raced the
/// integration harness — <c>ApiFactory</c> clears the same variable, and xUnit runs
/// separate collections in parallel — so a unit test could flip it out from under a
/// host build, or vice versa. Passing the value in removes the shared state entirely.
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

        var result = MasterKeyLoader.Resolve(store, envKeyValue: null, envKeyId: "v1");

        Assert.NotNull(result.Key);
        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal("v1", result.Key.Id);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
        Assert.False(result.EnvironmentIgnored);
    }

    [Fact]
    public void Prefers_the_files_own_id_over_the_environments()
    {
        // Rotation mints key and id together, so the pairing on disk is authoritative.
        var store = NewStore();
        store.Write(Key32Base64, "v7");

        var result = MasterKeyLoader.Resolve(store, envKeyValue: null, envKeyId: "v1");

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

        var result = MasterKeyLoader.Resolve(store, envKeyValue: null, envKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
    }

    [Fact]
    public void Reports_no_key_when_neither_source_is_configured()
    {
        // Not an exception: ADR-0092 D3 lets a virgin install proceed and mint one in
        // the setup ceremony. Whether "none" is fatal is the caller's gate.
        var result = MasterKeyLoader.Resolve(NewStore(), envKeyValue: null, envKeyId: "v1");

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
            () => MasterKeyLoader.Resolve(store, envKeyValue: null, envKeyId: "v1"));
    }

    [Fact]
    public void Throws_when_the_files_decoded_length_is_wrong()
    {
        // 16 bytes — AES-128, not AES-256.
        var store = NewStore();
        store.Write(Convert.ToBase64String(new byte[16]));

        var ex = Assert.Throws<ArgumentException>(
            () => MasterKeyLoader.Resolve(store, envKeyValue: null, envKeyId: "v1"));
        Assert.Contains("32 bytes", ex.Message);
    }

    // --- the file wins (ADR-0092 D1) ---------------------------------------

    [Fact]
    public void The_file_wins_over_the_environment_and_the_mismatch_is_reported()
    {
        // THE regression this ordering exists to prevent. With env-first, a UI rotation
        // re-wrapped the database under a new key, wrote it to the file — and the next
        // boot silently overwrote that with the stale env value, leaving the process
        // holding the OLD key over NEW wraps. Same failure on restore-adopt, where
        // reconciliation then cleared the secrets the adopted key was supplied to save.
        var envKey = new byte[32];
        envKey[0] = 0xAB;
        var store = NewStore();
        store.Write(Key32Base64, "v2");

        var result = MasterKeyLoader.Resolve(
            store, envKeyValue: Convert.ToBase64String(envKey), envKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);          // the file's key, not the env's
        Assert.Equal("v2", result.Key.Id);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
        // The caller warns: an operator who set that variable believes it does something.
        Assert.True(result.EnvironmentIgnored);
        // And the file is left exactly as it was — no write-through clobber.
        Assert.Equal(Key32Base64, store.ReadRaw());
    }

    [Fact]
    public void A_matching_environment_value_is_not_reported_as_ignored()
    {
        // The ordinary post-migration state: .env still holds the key that was written
        // through to the file. Nothing is wrong, so nothing should be said.
        var store = NewStore();
        store.Write(Key32Base64);

        var result = MasterKeyLoader.Resolve(store, envKeyValue: Key32Base64, envKeyId: "v1");

        Assert.False(result.EnvironmentIgnored);
    }

    [Fact]
    public void A_malformed_environment_value_is_ignored_when_the_file_has_a_key()
    {
        // File-first means a typo in .env can't take an install down.
        var store = NewStore();
        store.Write(Key32Base64);

        var result = MasterKeyLoader.Resolve(
            store, envKeyValue: "not!valid!base64!", envKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.True(result.EnvironmentIgnored);
    }

    // --- env-var transition (ADR-0092 D6) ----------------------------------

    [Fact]
    public void Migrates_the_deprecated_env_var_when_the_file_is_empty()
    {
        var store = NewStore();

        var result = MasterKeyLoader.Resolve(store, envKeyValue: Key32Base64, envKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal(MasterKeyLoader.KeySource.MigratedFromEnvironment, result.Source);
        Assert.False(result.EnvironmentIgnored);
        // Written through with its id, so the NEXT boot resolves from the file even if
        // the operator removes the variable — which is the whole point of D6.
        var (key, id) = store.Read();
        Assert.Equal(Key32Base64, key);
        Assert.Equal("v1", id);
    }

    [Fact]
    public void A_malformed_env_var_throws_when_there_is_no_file_to_fall_back_on()
    {
        // The dangerous alternative: treating a typo'd value as "no key", which D3
        // could then resolve by minting a fresh one over live wrapped material.
        Assert.Throws<FormatException>(() => MasterKeyLoader.Resolve(
            NewStore(), envKeyValue: "not!valid!base64!at!all!", envKeyId: "v1"));
    }

    [Fact]
    public void A_malformed_env_var_is_not_written_to_the_store()
    {
        // Validate-before-persist: a bad value must never become the install's
        // permanent on-disk state.
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => MasterKeyLoader.Resolve(
            store, envKeyValue: Convert.ToBase64String(new byte[16]), envKeyId: "v1"));
        Assert.False(store.Exists());
    }

    [Fact]
    public void Whitespace_only_env_var_is_treated_as_unset()
    {
        var store = NewStore();
        store.Write(Key32Base64);

        var result = MasterKeyLoader.Resolve(store, envKeyValue: "   ", envKeyId: "v1");

        Assert.Equal(Key32, result.Key!.KeyBytes);
        Assert.Equal(MasterKeyLoader.KeySource.File, result.Source);
        Assert.False(result.EnvironmentIgnored);
    }

    [Fact]
    public void Rejects_a_blank_env_id()
        => Assert.Throws<ArgumentException>(
            () => MasterKeyLoader.Resolve(NewStore(), envKeyValue: null, envKeyId: "  "));
}
