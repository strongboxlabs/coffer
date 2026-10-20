using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// Every <c>bytea</c> column in the schema is classified, and every one sealed under the
/// master key is handled by both rotation and post-restore reconciliation.
/// </summary>
/// <remarks>
/// The notification delivery configs were sealed under the master key and left out of
/// rotation when they were added (KekRotationService says what that cost), and the OAuth
/// server keys joined the same list in FU-069. Nothing failed until a rotation ran. This
/// fails the day a sealed column is added: an unclassified column fails, and so does a
/// master-sealed one that either service does not touch.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MasterKeySealedColumnsTests
{
    private readonly PostgresFixture _fixture;

    public MasterKeySealedColumnsTests(PostgresFixture fixture) => _fixture = fixture;

    private enum Kind
    {
        /// <summary>Sealed under the master KEK: rotation re-wraps it, reconciliation clears it.</summary>
        MasterKeySealed,
        /// <summary>Sealed under a ledger's LEK, which rotation does not change.</summary>
        LedgerKeySealed,
        /// <summary>A one-way hash of a secret; nothing opens it.</summary>
        Hash,
        /// <summary>Stored as it is: a public key, an id, or compressed ledger data.</summary>
        NotSealed,
    }

    /// <summary>
    /// <c>table.column</c> → what it is. For master-sealed columns, the DbSet and property
    /// both services must read, as <c>db.{DbSet}</c> and <c>.{Property}</c>.
    /// </summary>
    private static readonly Dictionary<string, (Kind Kind, string? DbSet, string? Property)> Catalog =
        new(StringComparer.Ordinal)
        {
            ["ledgers.wrapped_lek"] = (Kind.MasterKeySealed, "Ledgers", "WrappedLek"),
            ["global_scheduled_jobs.passphrase_ciphertext"] = (Kind.MasterKeySealed, "GlobalScheduledJobs", "PassphraseCiphertext"),
            ["drive_sync.oauth_ciphertext"] = (Kind.MasterKeySealed, "DriveSync", "OauthCiphertext"),
            ["notification_subscribers.config_ciphertext"] = (Kind.MasterKeySealed, "NotificationSubscribers", "ConfigCiphertext"),
            ["ledger_notification_subscribers.config_ciphertext"] = (Kind.MasterKeySealed, "LedgerNotificationSubscribers", "ConfigCiphertext"),
            ["oidc_server_keys.signing_key_ciphertext"] = (Kind.MasterKeySealed, "OidcServerKeys", "SigningKeyCiphertext"),
            ["oidc_server_keys.encryption_key_ciphertext"] = (Kind.MasterKeySealed, "OidcServerKeys", "EncryptionKeyCiphertext"),

            ["feed_connections.access_url_ciphertext"] = (Kind.LedgerKeySealed, null, null),

            ["auth_sessions.session_hash"] = (Kind.Hash, null, null),
            ["bootstrap_tokens.token_hash"] = (Kind.Hash, null, null),
            ["invites.token_hash"] = (Kind.Hash, null, null),
            ["mcp_access_tokens.token_hash"] = (Kind.Hash, null, null),

            ["webauthn_credentials.credential_id"] = (Kind.NotSealed, null, null),
            ["webauthn_credentials.public_key"] = (Kind.NotSealed, null, null),
            ["ledger_snapshots.content"] = (Kind.NotSealed, null, null),   // gzip JSON (mig 111)
        };

    [Fact]
    public async Task Every_bytea_column_is_classified_and_master_key_sealed_ones_are_rotated_and_reconciled()
    {
        await using var db = _fixture.NewDbContext();
        var columns = await db.Database.SqlQuery<string>($"""
            SELECT table_name || '.' || column_name AS "Value"
              FROM information_schema.columns
             WHERE table_schema = 'public' AND data_type = 'bytea'
            """).ToListAsync();

        // Fail closed: if the query stopped finding columns, every check below would pass.
        Assert.Contains("ledgers.wrapped_lek", columns);
        Assert.Contains("oidc_server_keys.signing_key_ciphertext", columns);

        var unclassified = columns.Except(Catalog.Keys).Order(StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0,
            "Classify these bytea columns in MasterKeySealedColumnsTests.Catalog: "
            + string.Join(", ", unclassified)
            + ". If one is sealed under the master key, KekRotationService must re-wrap it and "
            + "KekReconciliationService must clear it when it does not open.");
        var gone = Catalog.Keys.Except(columns).Order(StringComparer.Ordinal).ToList();
        Assert.True(gone.Count == 0, "The catalog lists columns the schema no longer has: " + string.Join(", ", gone));

        var rotation = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Crypto", "KekRotationService.cs"));
        var reconciliation = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Crypto", "KekReconciliationService.cs"));
        var missing = new List<string>();
        foreach (var (column, (kind, dbSet, property)) in Catalog)
        {
            if (kind != Kind.MasterKeySealed) continue;
            if (!rotation.Contains($"db.{dbSet}", StringComparison.Ordinal) || !rotation.Contains($".{property}", StringComparison.Ordinal))
                missing.Add($"{column} (rotation)");
            if (!reconciliation.Contains($"db.{dbSet}", StringComparison.Ordinal) || !reconciliation.Contains($".{property}", StringComparison.Ordinal))
                missing.Add($"{column} (reconciliation)");
        }
        Assert.True(missing.Count == 0,
            "Sealed under the master key but not handled: " + string.Join(", ", missing)
            + ". A rotation would strand it under the retired key, and a cross-install restore "
            + "would leave ciphertext this install cannot open.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
