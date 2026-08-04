namespace Coffer.Api.Crypto;

/// <summary>
/// Loads the deployment-level master KEK from the environment at
/// API startup. Single entry point for the secret-loading contract
/// per ADR-0014 §Layer 4 + ADR-0026.
/// </summary>
/// <remarks>
/// Fails loudly on any departure from the contract — missing env
/// var, malformed base64, wrong key length. The API refuses to
/// start rather than fall through to a default; an unconfigured
/// production deployment that accidentally booted would be a
/// catastrophic crypto break (every newly-wrapped LEK would be
/// under a key the operator doesn't have, and decryption would
/// fail at the first secret-read).
/// </remarks>
public static class MasterKeyLoader
{
    /// <summary>
    /// Environment variable carrying the base64-encoded 32-byte
    /// master KEK. Set per-deployment; never committed.
    /// </summary>
    public const string EnvVarName = "COFFER_MASTER_KEK_BASE64";

    /// <summary>Optional env var naming the current KEK's id (stamped into
    /// <c>ledgers.lek_kek_id</c> on new wraps). Defaults to <c>"v1"</c>;
    /// <c>rotate-kek</c> bumps it (see <see cref="KekRotationService"/>).</summary>
    public const string IdEnvVarName = "COFFER_MASTER_KEK_ID";

    /// <summary>Env vars for the NEW key during <c>rotate-kek</c>.</summary>
    public const string NewEnvVarName = "COFFER_MASTER_KEK_NEW_BASE64";
    public const string NewIdEnvVarName = "COFFER_MASTER_KEK_NEW_ID";

    /// <summary>
    /// Read <see cref="EnvVarName"/> (+ optional <see cref="IdEnvVarName"/>),
    /// validate, and construct the in-memory <see cref="MasterKey"/>. The id
    /// defaults to <c>"v1"</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Env var unset or empty.</exception>
    /// <exception cref="FormatException">Env var value is not valid base64.</exception>
    /// <exception cref="ArgumentException">Decoded key is not exactly 32 bytes.</exception>
    public static MasterKey LoadFromEnvironmentOrThrow()
    {
        var id = Environment.GetEnvironmentVariable(IdEnvVarName);
        return LoadFromValueOrThrow(
            Environment.GetEnvironmentVariable(EnvVarName),
            string.IsNullOrWhiteSpace(id) ? "v1" : id.Trim(),
            EnvVarName);
    }

    /// <summary>
    /// Load the NEW master key for rotation from <see cref="NewEnvVarName"/>
    /// (+ optional <see cref="NewIdEnvVarName"/>, default <c>"v2"</c>). Throws
    /// the same way as the primary loader.
    /// </summary>
    public static MasterKey LoadNewFromEnvironmentOrThrow()
    {
        var id = Environment.GetEnvironmentVariable(NewIdEnvVarName);
        return LoadFromValueOrThrow(
            Environment.GetEnvironmentVariable(NewEnvVarName),
            string.IsNullOrWhiteSpace(id) ? "v2" : id.Trim(),
            NewEnvVarName);
    }

    /// <summary>Validate a base64 KEK value + build the <see cref="MasterKey"/>.
    /// Shared by the primary + rotation loaders so the contract (set, base64,
    /// 32 bytes) is enforced identically.</summary>
    public static MasterKey LoadFromValueOrThrow(string? raw, string id, string envVarName)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"{envVarName} is not set. Generate one with " +
                $"`openssl rand -base64 32` and add it to your .env " +
                $"or systemd unit. See docs/decisions/0014-encryption-at-rest.md.");

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(raw.Trim());
        }
        catch (FormatException ex)
        {
            throw new FormatException(
                $"{envVarName} is not valid base64. Decode failed: {ex.Message}",
                ex);
        }

        // MasterKey's constructor validates length; let the
        // ArgumentException surface with its precise message.
        return new MasterKey(keyBytes, id);
    }
}
