using Microsoft.EntityFrameworkCore;

using Npgsql;

using Coffer.Api.Db;

namespace Coffer.Api.Crypto;

/// <summary>
/// Answers one startup question (ADR-0092 D3): does this database already hold
/// material wrapped under a master KEK? A "no" makes a key-less boot legal —
/// there is nothing to strand, so a fresh key can be minted. A "yes" makes it an
/// operator error, because minting a fresh key would orphan the wrapped set.
/// </summary>
/// <remarks>
/// <para><b>Why the queries look defensive.</b> This runs before migrations and
/// before the host is built, so it cannot assume the schema exists and cannot
/// resolve a <see cref="AppDbContext"/> from DI. The usual escape hatch for a
/// schema question — a Postgres function bound via <c>HasDbFunction</c> — is
/// unavailable by definition: a function created by a migration cannot exist
/// before migrations. So each check is an ordinary LINQ query wrapped in a
/// <c>42P01 undefined_table</c> catch, keeping the data-access layer 100% EF (see
/// <c>feedback_no_raw_sql_in_api</c> in project memory) at the cost of using an
/// expected exception as a signal.</para>
///
/// <para><b>Fails closed.</b> Any outcome other than a clean "no wrapped
/// material" — including an unreachable database — reports true. The asymmetry is
/// deliberate: a false "virgin" mints a key over live wrapped material and
/// orphans it, while a false "not virgin" only refuses to boot, which is
/// recoverable by supplying the key or passing the adopt flag.</para>
/// </remarks>
public static class WrappedMaterialProbe
{
    /// <summary>Postgres <c>undefined_table</c> — the table this check asks about
    /// hasn't been created yet, i.e. migrations haven't run.</summary>
    private const string UndefinedTable = "42P01";

    /// <summary>
    /// Every place a KEK-wrapped value can live: the same columns
    /// <c>KekRotationService</c> re-wraps and <c>KekReconciliationService</c> clears.
    /// A wrapped column missing here is one a key-less boot would orphan silently, so
    /// <c>MasterKeySealedColumnsTests</c> fails when the schema holds a master-sealed
    /// column that this list, rotation or reconciliation does not read.
    /// </summary>
    private static readonly Func<AppDbContext, bool>[] Checks =
    [
        // Each check is the rule rotation and reconciliation select by, so the three agree on
        // what is sealed. A ledger key is present or null. The passphrase and the Drive token
        // count only when non-empty: reconciliation clears them to null, and an empty value
        // is nothing either service opens, so a key minted over it orphans nothing.
        db => db.Ledgers.Any(l => l.WrappedLek != null),
        db => db.GlobalScheduledJobs.Any(j => j.PassphraseCiphertext != null && j.PassphraseCiphertext.Length > 0),
        db => db.DriveSync.Any(d => d.OauthCiphertext != null && d.OauthCiphertext.Length > 0),

        // Delivery targets' sealed settings (URL and token), both scopes. Length > 0
        // rather than a null check
        // because config_ciphertext is BYTEA NOT NULL: reconciliation empties it to
        // retire a blob this install cannot open, and an empty array is "no wrapped
        // material here", not "wrapped material of length zero".
        db => db.NotificationSubscribers.Any(t => t.ConfigCiphertext.Length > 0),
        db => db.LedgerNotificationSubscribers.Any(t => t.ConfigCiphertext.Length > 0),

        // The OAuth server's signing and encryption keys (mig 253). Every boot seals a
        // pair once migrations have run if none is stored, so any install that has
        // booted once holds wrapped material and a key-less boot refuses: minting a
        // fresh key would leave MCP sign-in unable to open its own keys. Both columns are
        // BYTEA NOT NULL and reconciliation deletes the row rather than emptying it, so
        // Length > 0 is the same "no wrapped material" rule as the delivery targets above.
        db => db.OidcServerKeys.Any(k =>
            k.SigningKeyCiphertext.Length > 0 || k.EncryptionKeyCiphertext.Length > 0),

        // The private-fork release token (mig 269, ADR-0110). BYTEA NOT NULL, and no row means
        // no token, so any row with Length > 0 is wrapped material a fresh key would orphan.
        db => db.UpgradeRepoToken.Any(t => t.TokenCiphertext.Length > 0),
    ];

    /// <summary>
    /// True when the database holds at least one KEK-wrapped value, or when that
    /// could not be established. <paramref name="onProbeFailure"/> receives the
    /// exception when the probe couldn't run at all, so the caller can say why it
    /// is refusing rather than reporting a bare "wrapped material present".
    /// </summary>
    public static bool Exists(
        string serviceConnectionString,
        Action<Exception>? onProbeFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceConnectionString);

        // Constructed directly rather than resolved: DI doesn't exist yet. The
        // service role has BYPASSRLS, so none of the app.user_id session plumbing
        // AppUserDbConnectionInterceptor provides is needed here.
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(serviceConnectionString)
                .Options);

        foreach (var check in Checks)
        {
            try
            {
                if (check(db)) return true;
            }
            catch (Exception ex) when (IsUndefinedTable(ex))
            {
                // Table absent → migrations haven't created it. Not evidence of
                // wrapped material, and not a probe failure either: this is the
                // expected shape of a fresh install's first boot.
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException
                                         or TimeoutException)
            {
                // Couldn't reach or query the database at all. Can't prove virgin
                // → report not-virgin. See the fail-closed note above; the two
                // error directions are not equally costly.
                onProbeFailure?.Invoke(ex);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="ex"/> is (or wraps) Postgres
    /// <c>42P01 undefined_table</c>. EF surfaces provider errors directly for some
    /// failures and wrapped in an <see cref="InvalidOperationException"/> for
    /// others, so the inner chain is walked rather than assuming either shape.
    /// </summary>
    private static bool IsUndefinedTable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is PostgresException { SqlState: UndefinedTable }) return true;
        return false;
    }
}
