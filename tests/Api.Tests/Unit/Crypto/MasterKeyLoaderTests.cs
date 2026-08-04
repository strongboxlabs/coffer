using Coffer.Api.Crypto;

namespace Coffer.Api.Tests.Unit.Crypto;

/// <summary>
/// Pinning the contract of the env-var loader (ADR-0026): fail-fast
/// on every departure from the expected shape. The API refuses to
/// start rather than fall through to a default — a misconfigured
/// production boot would be a catastrophic crypto break.
/// </summary>
public sealed class MasterKeyLoaderTests
{
    private static IDisposable WithEnvVar(string? value)
    {
        var original = Environment.GetEnvironmentVariable(MasterKeyLoader.EnvVarName);
        Environment.SetEnvironmentVariable(MasterKeyLoader.EnvVarName, value);
        return new Restore(() =>
            Environment.SetEnvironmentVariable(MasterKeyLoader.EnvVarName, original));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;
        public Restore(Action action) { _action = action; }
        public void Dispose() => _action();
    }

    [Fact]
    public void Loads_a_valid_32_byte_base64_KEK()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        using var _ = WithEnvVar(Convert.ToBase64String(key));

        var loaded = MasterKeyLoader.LoadFromEnvironmentOrThrow();

        Assert.Equal(key, loaded.KeyBytes);
        Assert.Equal("v1", loaded.Id);
    }

    [Fact]
    public void Throws_when_env_var_is_unset()
    {
        using var _ = WithEnvVar(null);
        var ex = Assert.Throws<InvalidOperationException>(
            MasterKeyLoader.LoadFromEnvironmentOrThrow);
        Assert.Contains(MasterKeyLoader.EnvVarName, ex.Message);
    }

    [Fact]
    public void Throws_when_env_var_is_empty_or_whitespace()
    {
        using var _ = WithEnvVar("   ");
        Assert.Throws<InvalidOperationException>(
            MasterKeyLoader.LoadFromEnvironmentOrThrow);
    }

    [Fact]
    public void Throws_when_value_is_not_valid_base64()
    {
        using var _ = WithEnvVar("not!valid!base64!at!all!");
        Assert.Throws<FormatException>(MasterKeyLoader.LoadFromEnvironmentOrThrow);
    }

    [Fact]
    public void Throws_when_decoded_length_is_wrong()
    {
        // 16 bytes — half the expected length. AES-128, not AES-256.
        using var _ = WithEnvVar(Convert.ToBase64String(new byte[16]));
        var ex = Assert.Throws<ArgumentException>(
            MasterKeyLoader.LoadFromEnvironmentOrThrow);
        Assert.Contains("32 bytes", ex.Message);
    }
}
