using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Crypto;

/// <summary>
/// Every <c>bytea</c> column in the schema is classified, and every one sealed under the
/// master key is handled by rotation, by post-restore reconciliation, and by the startup
/// probe that decides whether a key-less boot is legal (ADR-0092 D3).
/// </summary>
/// <remarks>
/// <para><b>The issue.</b> Three pieces of code each keep their own list of where
/// master-sealed material lives, and a column missing from one of them fails only when
/// that code runs: rotation strands it under the retired key, reconciliation leaves
/// ciphertext this install cannot open, and the probe reads an install as virgin, so a
/// key-less boot mints a fresh key and orphans it.</para>
/// <para><b>The handling.</b> The set of columns comes from the schema, so a new
/// <c>bytea</c> column fails here until it is classified, and every master-sealed one must
/// be read by all three. All three therefore cover the same columns. The match is on the
/// source with comments removed, so a comment naming a column does not count as reading
/// it.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MasterKeySealedColumnsTests : DatabaseTest
{
    public MasterKeySealedColumnsTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

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
    /// rotation, reconciliation and the probe must read, as <c>db.{DbSet}</c> and
    /// <c>.{Property}</c>; for ledger-key-sealed ones, what reconciliation's ledger re-key
    /// must clear.
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
            ["upgrade_repo_token.token_ciphertext"] = (Kind.MasterKeySealed, "UpgradeRepoToken", "TokenCiphertext"),

            ["feed_connections.access_url_ciphertext"] = (Kind.LedgerKeySealed, "FeedConnections", "AccessUrlCiphertext"),

            ["auth_sessions.session_hash"] = (Kind.Hash, null, null),
            ["bootstrap_tokens.token_hash"] = (Kind.Hash, null, null),
            ["invites.token_hash"] = (Kind.Hash, null, null),
            ["mcp_access_tokens.token_hash"] = (Kind.Hash, null, null),

            ["webauthn_credentials.credential_id"] = (Kind.NotSealed, null, null),
            ["webauthn_credentials.public_key"] = (Kind.NotSealed, null, null),
            ["ledger_snapshots.content"] = (Kind.NotSealed, null, null),   // gzip JSON (mig 111)
        };

    [Fact]
    public async Task Every_bytea_column_is_classified_and_master_key_sealed_ones_are_rotated_reconciled_and_probed()
    {
        await using var db = Db.NewDbContext();
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

        var consumers = new (string Name, string Code)[]
        {
            ("rotation", CodeOf("KekRotationService.cs")),
            ("reconciliation", CodeOf("KekReconciliationService.cs")),
            ("probe", CodeOf("WrappedMaterialProbe.cs")),
        };
        var missing = new List<string>();
        foreach (var (column, (kind, dbSet, property)) in Catalog)
        {
            if (kind != Kind.MasterKeySealed) continue;
            foreach (var (name, code) in consumers)
                if (!code.Contains($"db.{dbSet}", StringComparison.Ordinal) || !code.Contains($".{property}", StringComparison.Ordinal))
                    missing.Add($"{column} ({name})");
        }
        Assert.True(missing.Count == 0,
            "Sealed under the master key but not handled: " + string.Join(", ", missing)
            + ". Missed by rotation, it is stranded under the retired key; by reconciliation, a "
            + "cross-install restore leaves ciphertext this install cannot open; by "
            + "WrappedMaterialProbe, a key-less boot reads the install as virgin and mints a key "
            + "that orphans it.");

        // A ledger key reconciliation cannot open is replaced, and everything sealed under the
        // old one goes with it; what the re-key does not clear is left as ciphertext no key
        // opens. Rotation leaves these alone (the ledger key itself does not change), and the
        // probe needs nothing more: they exist only beside a wrapped ledger key it counts.
        var rekey = MethodBody(CodeOf("KekReconciliationService.cs"), "ReconcileLedgersAsync");
        var notCleared = Catalog
            .Where(c => c.Value.Kind == Kind.LedgerKeySealed)
            .Where(c => !rekey.Contains($"db.{c.Value.DbSet}", StringComparison.Ordinal)
                     || !rekey.Contains($".{c.Value.Property}", StringComparison.Ordinal))
            .Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(notCleared.Count == 0,
            "Sealed under a ledger key but not cleared by KekReconciliationService.ReconcileLedgersAsync: "
            + string.Join(", ", notCleared) + ". A restore across keys or an --adopt-new-kek run "
            + "replaces that ledger key and strands it.");
    }

    /// <summary>The text of method <paramref name="name"/> in <paramref name="code"/>, up to
    /// the next member declared at the same indentation. Fails when it is not found, so a
    /// rename cannot turn the check that uses it into an empty one.</summary>
    private static string MethodBody(string code, string name)
    {
        var start = Regex.Match(code, $@"\n    (private|public|internal|protected) [^\n]*\b{name}\(");
        Assert.True(start.Success, $"No method {name} in the reconciliation source.");
        var rest = code[(start.Index + start.Length)..];
        var next = Regex.Match(rest, @"\n    (private|public|internal|protected) ");
        return next.Success ? code.Substring(start.Index, start.Length + next.Index) : code[start.Index..];
    }

    [Fact]
    public void Comments_do_not_count_as_code()
    {
        // The guard above passes for a consumer whose only mention of a column is a comment
        // unless comments are removed first; this holds the removal to that.
        var code = WithoutComments("""
            /// <c>db.Hidden</c>
            var a = db.Shown; // db.Trailing
            /* db.Block
               db.BlockLine */ var b = 1;
            """);

        Assert.Contains("db.Shown", code, StringComparison.Ordinal);
        Assert.Contains("var b = 1;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Hidden", code, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Trailing", code, StringComparison.Ordinal);
        Assert.DoesNotContain("db.Block", code, StringComparison.Ordinal);
    }

    /// <summary>The source of <c>src/Api/Crypto/{file}</c> with its comments removed.</summary>
    private static string CodeOf(string file) =>
        WithoutComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Api", "Crypto", file)));

    /// <summary>
    /// <paramref name="source"/> without <c>//</c> and <c>/* */</c> comments. A <c>//</c>
    /// inside a string literal also cuts the rest of its line, which can only remove a
    /// match, so the guard fails rather than passes on it.
    /// </summary>
    private static string WithoutComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline), "//[^\n]*", " ");

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
