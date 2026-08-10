using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db.Entities;

namespace Coffer.Api.Db.Services;

/// <summary>
/// Persists in-flight WebAuthn ceremony state across the begin/complete
/// HTTP pair. Each row holds the JSON-serialised <c>CredentialCreateOptions</c>
/// or <c>AssertionOptions</c> the browser was given at <c>/begin</c>;
/// the <c>/complete</c> handler looks the row up by id and feeds it back
/// to Fido2.AspNet for verification.
/// </summary>
/// <remarks>
/// <para>Connects via the service-role factory: ceremony rows are
/// minted at <c>/begin</c> BEFORE the user is authenticated (for login)
/// or even exists (for bootstrap setup), so RLS on coffer_app would
/// deny both the INSERT and the read at <c>/complete</c>.</para>
///
/// <para><see cref="ConsumeAsync"/>'s atomic "flip <c>consumed_at</c>
/// and return the row" semantics use a transactional WHERE-conditioned
/// <c>ExecuteUpdate</c> followed by a SELECT in the same transaction.
/// Postgres serialises the row-level lock acquired by the UPDATE, so
/// two concurrent <c>/complete</c> calls against the same challenge
/// can never both succeed (the loser sees zero affected rows and
/// returns null without ever issuing the SELECT).</para>
/// </remarks>
public sealed class ChallengeStore
{
    /// <summary>Flow identifier for the bootstrap setup ceremony.</summary>
    public const string SetupFlow = "setup";

    /// <summary>Flow identifier for the login assertion ceremony.</summary>
    public const string LoginFlow = "login";

    /// <summary>
    /// Flow identifier for adding a passkey to an already-authenticated
    /// user (ADR-0013 follow-through — distinct from <see cref="SetupFlow"/>,
    /// which also creates the user).
    /// </summary>
    public const string RegisterFlow = "register";

    /// <summary>
    /// Flow identifier for redeeming an invite (ADR-0083 slice B) — like
    /// <see cref="SetupFlow"/> it creates the user + credential, but gated by an
    /// invite token and pre-scoped to the invite's ledger/role instead of a picker.
    /// </summary>
    public const string InviteFlow = "invite";

    /// <summary>
    /// Flow identifier for re-authenticating before the master KEK is revealed
    /// (ADR-0092 D2). Deliberately its own flow so a challenge minted for login
    /// can never be redeemed here — the session cookie already proves "an admin";
    /// this assertion proves "the human is present right now".
    /// </summary>
    public const string MasterKeyRevealFlow = "masterkey-reveal";

    /// <summary>
    /// Flow identifier for re-authenticating before the stored backup passphrase is
    /// revealed (ADR-0092 D7). Its own flow, not shared with
    /// <see cref="MasterKeyRevealFlow"/>: cross-redemption between two admin step-ups
    /// gains an attacker nothing, but keeping "a challenge is good for exactly the
    /// ceremony it was minted for" as a flat invariant is cheaper to reason about
    /// than arguing the exception each time a surface is added.
    /// </summary>
    public const string BackupPassphraseRevealFlow = "backup-passphrase-reveal";

    private readonly ServiceDbContextFactory _serviceFactory;

    public ChallengeStore(ServiceDbContextFactory serviceFactory)
    {
        _serviceFactory = serviceFactory;
    }

    /// <summary>
    /// Persist a fresh challenge row and return its id. The caller hands
    /// the id back to the client so the matching <c>/complete</c> request
    /// can echo it. <paramref name="ttl"/> bounds the row's life — 60s by
    /// default elsewhere, but the caller decides per-flow.
    /// </summary>
    public async Task<Guid> SaveAsync(
        string flow,
        Guid? userId,
        string optionsJson,
        string? metadataJson,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Challenge TTL must be positive.");

        var row = new WebAuthnPendingChallengeRow
        {
            Id = Guid.NewGuid(),
            Flow = flow,
            UserId = userId,
            OptionsJson = optionsJson,
            MetadataJson = metadataJson,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
        };
        await using var db = _serviceFactory.Create();
        db.PendingChallenges.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>
    /// Verify-and-consume a challenge by id. Returns the row when the id
    /// matches an unexpired, unconsumed row whose flow matches; null
    /// otherwise. Race-safe under concurrent callers because the UPDATE
    /// acquires a row lock — see class-level remarks.
    /// </summary>
    public async Task<WebAuthnPendingChallengeRow?> ConsumeAsync(
        Guid id,
        string expectedFlow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFlow);

        await using var db = _serviceFactory.Create();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
                                                         .ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var affected = await db.PendingChallenges
            .Where(c => c.Id == id
                     && c.Flow == expectedFlow
                     && c.ConsumedAt == null
                     && c.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.ConsumedAt, _ => (DateTime?)now),
                cancellationToken)
            .ConfigureAwait(false);

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Read the post-update row inside the still-open transaction.
        // Postgres' READ COMMITTED isolation lets us see our own
        // earlier UPDATE; the lock the UPDATE acquired blocks any
        // other transaction trying to consume the same id until we
        // COMMIT.
        var row = await db.PendingChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Delete every expired or consumed row. Run periodically (PR 3.5+
    /// adds an IHostedService sweep); the indexes keep the lookup cheap
    /// in the meantime.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = _serviceFactory.Create();
        return await db.PendingChallenges
            .Where(c => c.ExpiresAt < now || c.ConsumedAt != null)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
