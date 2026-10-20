namespace Coffer.Api.Db.Entities;

/// <summary>
/// Persistable shape of a row in <c>auth_sessions</c>. The cookie value
/// itself never lives in the DB — only its SHA-256, so a DB read can't
/// forge sessions (per ADR-0013).
/// </summary>
public sealed class SessionRow
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public byte[] SessionHash { get; init; } = Array.Empty<byte>();
    public string? UserAgent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? RevokedAt { get; init; }

    /// <summary>How the session began (mig 248), one of <see cref="SessionAuthMethods"/>.</summary>
    public string AuthMethod { get; init; } = SessionAuthMethods.Passkey;
}

/// <summary>
/// The ways a session can begin (<c>auth_sessions.auth_method</c>, mig 248). Only
/// <see cref="RecoveryCode"/> changes anything today: such a session may add one
/// passkey within <c>StepUpGuard.RecoveryPasskeyWindow</c> without a step-up tap,
/// because it may have no usable passkey to tap with.
/// </summary>
public static class SessionAuthMethods
{
    public const string Passkey = "passkey";
    public const string RecoveryCode = "recovery-code";
    public const string Setup = "setup";
    public const string Invite = "invite";
    public const string Dev = "dev";
}
