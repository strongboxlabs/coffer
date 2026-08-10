namespace Coffer.Api.Crypto;

/// <summary>
/// Loads the deployment-level master KEK at API startup. Single entry point for
/// the secret-loading contract per ADR-0014 §Layer 4 + ADR-0026, as amended by
/// ADR-0092 D1 (the key lives in a file, not the environment).
/// </summary>
/// <remarks>
/// <para>Fails loudly on any departure from the contract — no key found,
/// malformed base64, wrong key length. The API refuses to start rather than fall
/// through to a default; an unconfigured deployment that accidentally booted
/// would wrap every new LEK under a key the operator doesn't have, and
/// decryption would fail at the first secret-read.</para>
///
/// <para>ADR-0092 D3 narrows *when* that refusal applies — a genuinely virgin
/// install may proceed without a key and generate one in the setup ceremony,
/// because there is nothing yet to strand. That gate needs a database probe, so
/// it lives at the call site in Program.cs, not here. This class only answers
/// "what key, if any, is configured."</para>
/// </remarks>
public static class MasterKeyLoader
{
    /// <summary>
    /// Environment variable carrying the base64-encoded 32-byte master KEK.
    /// <b>Deprecated</b> by ADR-0092 D1 in favour of <see cref="MasterKeyStore"/>.
    /// Still honoured for one release and migrated to the file on first boot
    /// (ADR-0092 D6) so existing deployments upgrade without an <c>.env</c> edit;
    /// removed a release later.
    /// </summary>
    public const string EnvVarName = "COFFER_MASTER_KEK_BASE64";

    /// <summary>
    /// Optional env var naming the current KEK's id (stamped into
    /// <c>ledgers.lek_kek_id</c> on new wraps). Defaults to <c>"v1"</c>. A deprecated
    /// fallback only: rotation mints key and id together into the key file, whose
    /// <c>id=</c> line wins (ADR-0092 D4).
    /// </summary>
    public const string IdEnvVarName = "COFFER_MASTER_KEK_ID";

    // COFFER_MASTER_KEK_NEW_BASE64 / _NEW_ID are gone with the `rotate-kek` CLI
    // (ADR-0092 D4). Rotation generates the new key server-side and swaps the key
    // file itself, so there is nothing for an operator to pre-stage in the
    // environment.

    /// <summary>Outcome of <see cref="Resolve"/> — which source supplied the key,
    /// so the caller can log a deprecation, run the D3 gate, or hand off to the
    /// setup ceremony.</summary>
    public enum KeySource
    {
        /// <summary>No key configured anywhere. Legal only on a virgin install
        /// (ADR-0092 D3); the caller decides.</summary>
        None,
        /// <summary>Read from <see cref="MasterKeyStore"/> — the steady state.</summary>
        File,
        /// <summary>Read from the deprecated env var and written through to the
        /// file (ADR-0092 D6). The caller logs the deprecation.</summary>
        MigratedFromEnvironment,
    }

    /// <summary>
    /// Outcome of <see cref="Resolve(MasterKeyStore)"/>.
    /// </summary>
    /// <param name="Key">The resolved key, or null when nothing is configured.</param>
    /// <param name="Source">Where it came from.</param>
    /// <param name="EnvironmentIgnored">
    /// True when <see cref="EnvVarName"/> is set to something OTHER than what the key
    /// file holds, and was therefore ignored. The caller must warn: an operator who
    /// edits <c>.env</c> expecting an effect needs to hear that the file now wins.
    /// </param>
    public sealed record Resolution(MasterKey? Key, KeySource Source, bool EnvironmentIgnored);

    /// <summary>
    /// Resolve the master KEK per ADR-0092 D1. <b>The key file wins.</b> The
    /// deprecated env var is honoured only when the file holds no key, and is then
    /// written through to it.
    /// </summary>
    /// <remarks>
    /// <para>The file has to win, and this was originally the other way round. With
    /// env-first, a UI rotation (D4) re-wrapped the database under a new key, wrote
    /// that key to the file — and the next boot silently overwrote it with the stale
    /// env value, leaving the process holding the OLD key over NEW wraps. The
    /// restore-adopt path (D4) failed the same way, and worse: boot B would clobber
    /// the adopted source key, then D5 reconciliation would clear the very secrets
    /// the operator supplied that key to preserve. Both are silent, and both land on
    /// the transition path D6 promises will work — so file-first is not a preference,
    /// it's a correctness requirement once anything can write the file.</para>
    ///
    /// <para>The upgrade path still works: an operator arriving with a key in
    /// <c>.env</c> and no file gets it migrated on first boot, no edit and no
    /// downtime. From then on the file is authoritative — including after a
    /// rotation, which is the whole point.</para>
    ///
    /// <para>A malformed value from EITHER source throws rather than falling through
    /// to the next one: a typo'd key must be a loud failure, never a silent downgrade
    /// to "no key configured", which D3 could then resolve by minting a fresh one
    /// over live wrapped material.</para>
    /// </remarks>
    /// <exception cref="FormatException">Configured value is not valid base64.</exception>
    /// <exception cref="ArgumentException">Decoded key is not exactly 32 bytes.</exception>
    public static Resolution Resolve(MasterKeyStore store)
        => Resolve(store, Environment.GetEnvironmentVariable(EnvVarName), ResolveId());

    /// <summary>
    /// Testable core of <see cref="Resolve(MasterKeyStore)"/>: the environment is
    /// passed in rather than read, so tests don't have to mutate process-global state
    /// (which races the integration harness, since xUnit runs separate collections in
    /// parallel).
    /// </summary>
    public static Resolution Resolve(MasterKeyStore store, string? envKeyValue, string envKeyId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(envKeyId);

        var (fromFile, fileId) = store.Read();
        if (!string.IsNullOrWhiteSpace(fromFile))
        {
            // The file's own id wins over the environment's. Rotation mints key and
            // id together (ADR-0092 D4), so the pairing on disk is authoritative;
            // COFFER_MASTER_KEK_ID is a deprecated fallback for a file that predates
            // the id line.
            var key = LoadFromValueOrThrow(fromFile, fileId ?? envKeyId, store.Path);
            var envDiffers = !string.IsNullOrWhiteSpace(envKeyValue)
                && envKeyValue.Trim() != fromFile.Trim();
            return new(key, KeySource.File, envDiffers);
        }

        if (!string.IsNullOrWhiteSpace(envKeyValue))
        {
            // Validate BEFORE persisting, so a malformed env var never lands in the
            // store and becomes the install's permanent bad state.
            var key = LoadFromValueOrThrow(envKeyValue, envKeyId, EnvVarName);
            store.Write(envKeyValue.Trim(), envKeyId);
            return new(key, KeySource.MigratedFromEnvironment, false);
        }

        return new(null, KeySource.None, false);
    }

    /// <summary>
    /// Bump the trailing number of a KEK id: <c>v1</c> → <c>v2</c>, <c>2</c> →
    /// <c>3</c>, <c>2026-08</c> → <c>2026-09</c>. An id with no trailing digits gets
    /// <c>-2</c> appended, so a custom label still produces a distinct successor
    /// rather than colliding with itself.
    /// </summary>
    /// <remarks>
    /// <para>Zero padding is preserved, so a date-shaped id stays date-shaped instead
    /// of degrading to <c>2026-9</c>. Widening still works — <c>v9</c> → <c>v10</c> —
    /// because padding only ever left-pads to the original width.</para>
    ///
    /// <para>Lives here rather than in the rotation endpoint because the boot path
    /// needs it too: <c>--adopt-new-kek</c> bumps the id so a freshly minted key isn't
    /// labelled identically to the orphaned rows it just abandoned.</para>
    /// </remarks>
    public static string NextKekId(string currentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentId);
        var id = currentId.Trim();

        var digitStart = id.Length;
        while (digitStart > 0 && char.IsAsciiDigit(id[digitStart - 1])) digitStart--;

        if (digitStart == id.Length) return $"{id}-2";

        var digits = id[digitStart..];
        // long, not int: a pathological id like "v99999999999…" must not turn a
        // rotation into an overflow exception.
        return long.TryParse(digits, out var n) && n < long.MaxValue
            ? $"{id[..digitStart]}{(n + 1).ToString().PadLeft(digits.Length, '0')}"
            : $"{id}-2";
    }

    /// <summary>The configured KEK id, defaulting to <c>"v1"</c>.</summary>
    public static string ResolveId()
    {
        var id = Environment.GetEnvironmentVariable(IdEnvVarName);
        return string.IsNullOrWhiteSpace(id) ? "v1" : id.Trim();
    }

    /// <summary>Validate a base64 KEK value + build the <see cref="MasterKey"/>.
    /// One contract (set, base64, 32 bytes) enforced identically for every source —
    /// the key file, the deprecated env var, and a rotation's freshly-minted key.
    /// <paramref name="envVarName"/> is really "where this came from", used only to
    /// name the source in the exception.</summary>
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
