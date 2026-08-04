using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Services;

/// <summary>
/// Mints, persists, and consumes the one-shot bootstrap token described
/// in ADR-0013. On first start (no <c>webauthn_credentials</c> rows),
/// the API generates 32 random bytes, stores their SHA-256 in
/// <c>bootstrap_tokens</c> with an expiry, and writes the plaintext to
/// the operator's log so a human can pick it up. Calling
/// <see cref="ConsumeAsync"/> with the plaintext flips
/// <c>consumed_at</c> exactly once; subsequent calls fail.
/// </summary>
/// <remarks>
/// Connects via the service-role factory: the bootstrap flow runs
/// before any user is authenticated (or even exists), and
/// <c>bootstrap_tokens</c> is REVOKE'd from coffer_app in migration 017
/// regardless.
/// </remarks>
public sealed class BootstrapTokenService
{
    private readonly ServiceDbContextFactory _serviceFactory;
    private readonly ILogger<BootstrapTokenService> _logger;
    private readonly TimeSpan _tokenLifetime;
    private readonly string _webOrigin;

    public BootstrapTokenService(
        ServiceDbContextFactory serviceFactory,
        IOptions<ApiOptions> options,
        ILogger<BootstrapTokenService> logger)
    {
        _serviceFactory = serviceFactory;
        _logger = logger;
        _tokenLifetime = TimeSpan.FromHours(Math.Max(1, options.Value.Bootstrap.TokenLifetimeHours));
        // The browser-facing origin (same-origin single-container, ADR-0059).
        // First configured Fido2 origin is the canonical web URL.
        _webOrigin = (options.Value.Fido2.Origins.Count > 0
            ? options.Value.Fido2.Origins[0]
            : "http://localhost:8080").TrimEnd('/');
    }

    /// <summary>
    /// Stable path for the first-run setup-URL artifact (the bootstrap-token
    /// CLI + first start write it; <see cref="ConsumeAsync"/> deletes it on
    /// successful setup). Lives under <c>data/</c> beside the binary — the
    /// Docker image mounts a volume there (ADR-0059).
    /// </summary>
    private static string ArtifactPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "bootstrap.url");

    private string BuildSetupUrl(string plaintext) => $"{_webOrigin}/setup/{plaintext}";

    /// <summary>
    /// Mint a token if and only if no credentials and no unconsumed,
    /// unexpired token already exist. Idempotent: calling repeatedly while
    /// a valid token sits in the DB is a no-op so a container restart
    /// between "operator copied the token" and "operator pasted it"
    /// doesn't reissue.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a fresh token was issued (and logged); <c>false</c>
    /// when no token was needed or a valid one already exists.
    /// </returns>
    public async Task<bool> EnsureBootstrapTokenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        if (await db.WebAuthnCredentials.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Bootstrap token not needed — credentials already exist.");
            return false;
        }

        var now = DateTime.UtcNow;
        if (await db.BootstrapTokens
                .AnyAsync(t => t.ConsumedAt == null && t.ExpiresAt > now, cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Bootstrap token already issued — see prior log line for the plaintext, or wait for it to expire to mint a new one.");
            return false;
        }

        var (plaintext, hash) = GenerateToken();
        var expiresAt = now.Add(_tokenLifetime);

        db.BootstrapTokens.Add(new BootstrapTokenRow
        {
            TokenHash = hash,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var setupUrl = BuildSetupUrl(plaintext);
        _logger.LogWarning(
            "First-run bootstrap. Open this one-shot setup URL "
            + "(valid for {Hours}h, never logged again): {Url}",
            _tokenLifetime.TotalHours, setupUrl);
        WriteArtifact(setupUrl);

        return true;
    }

    /// <summary>
    /// CLI entry point (<c>ledger-api bootstrap-token</c>). Returns a usable
    /// first-run setup URL, or <c>null</c> when setup is already complete (a
    /// credential exists). The plaintext of a prior token is unrecoverable
    /// (only its hash is stored), so this reissues: any prior unconsumed token
    /// is revoked and a fresh one minted, leaving exactly the printed URL valid.
    /// Also (re)writes the <c>data/bootstrap.url</c> artifact.
    /// </summary>
    public async Task<string?> ReissueSetupUrlAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _serviceFactory.Create();
        if (await db.WebAuthnCredentials.AnyAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var now = DateTime.UtcNow;
        // Revoke prior unconsumed tokens so only the URL we print works.
        await db.BootstrapTokens
            .Where(t => t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ConsumedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);

        var (plaintext, hash) = GenerateToken();
        db.BootstrapTokens.Add(new BootstrapTokenRow
        {
            TokenHash = hash,
            ExpiresAt = now.Add(_tokenLifetime),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var setupUrl = BuildSetupUrl(plaintext);
        WriteArtifact(setupUrl);
        return setupUrl;
    }

    // Best-effort artifact IO — never fail bootstrap because the data dir
    // isn't writable; just log.
    private void WriteArtifact(string url)
    {
        try
        {
            var path = ArtifactPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, url + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the bootstrap-url artifact.");
        }
    }

    private void DeleteArtifact()
    {
        try
        {
            var path = ArtifactPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the bootstrap-url artifact.");
        }
    }

    /// <summary>
    /// Verify the supplied plaintext against the stored hash and flip
    /// <c>consumed_at</c>. Returns <c>true</c> on success; <c>false</c>
    /// if the token doesn't exist, has expired, or has already been
    /// consumed.
    /// </summary>
    public async Task<bool> ConsumeAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return false;

        byte[] hash;
        try { hash = HashToken(plaintext); }
        catch (FormatException) { return false; }

        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        var affected = await db.BootstrapTokens
            .Where(t => t.TokenHash == hash && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ConsumedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);
        var consumed = affected > 0;
        // Setup done — the artifact advertises a now-spent URL; remove it.
        if (consumed) DeleteArtifact();
        return consumed;
    }

    /// <summary>
    /// Generate a 32-byte random token, return its base64url-encoded
    /// plaintext (URL-safe so it can ride in a path segment) and its
    /// SHA-256 hash for storage.
    /// </summary>
    internal static (string Plaintext, byte[] Hash) GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var plaintext = Base64UrlEncode(bytes);
        var hash = SHA256.HashData(bytes);
        return (plaintext, hash);
    }

    /// <summary>
    /// Hash a presented plaintext token the same way <see cref="GenerateToken"/>
    /// does for storage so verification is a byte-array equality check.
    /// </summary>
    internal static byte[] HashToken(string plaintext)
    {
        var bytes = Base64UrlDecode(plaintext);
        return SHA256.HashData(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
               .Replace('+', '-')
               .Replace('/', '_')
               .TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
