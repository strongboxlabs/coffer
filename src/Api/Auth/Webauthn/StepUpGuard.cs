using System.Security.Claims;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Fido2NetLib;

using Coffer.Api.Db.Entities;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;

namespace Coffer.Api.Auth.Webauthn;

/// <summary>
/// The actions that need a fresh passkey tap (2026-10-12 audit, M2), each with its own
/// challenge flow so a tap minted for one can't approve another (mig 248).
/// </summary>
public static class StepUpActions
{
    public const string PasskeyAdd = "passkey-add";
    public const string PasskeyDelete = "passkey-delete";
    public const string RecoveryCodes = "recovery-codes";
    public const string McpToken = "mcp-token";
    public const string GrantAdmin = "grant-admin";
    public const string Restore = "restore";
    public const string BackupPassphrase = "backup-passphrase";
    public const string BackupDelete = "backup-delete";
    public const string McpWrites = "mcp-writes";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PasskeyAdd, PasskeyDelete, RecoveryCodes, McpToken, GrantAdmin,
        Restore, BackupPassphrase, BackupDelete, McpWrites,
    };

    /// <summary>The challenge flow a tap for <paramref name="action"/> is minted under.</summary>
    public static string FlowFor(string action) => "step-up:" + action;
}

/// <summary>
/// Requires a fresh passkey tap for one action before the endpoint does anything.
/// </summary>
/// <remarks>
/// <para>The client starts a tap with <c>POST /api/auth/step-up/begin</c>, runs the
/// WebAuthn ceremony, and sends the result in the <see cref="HeaderName"/> header of
/// the request it guards. A header rather than a body field because several guarded
/// requests are multipart (restore) or have no body at all (regenerate recovery codes).
/// The tap is verified by <see cref="FreshAssertionGate"/>: single-use, scoped to this
/// action, the caller's own passkey, enrolled before the session began, with user
/// verification.</para>
///
/// <para>Two principals are let through without a tap. Dev auth, the development and
/// test scheme, registered only when the environment is Development AND Api:DevAuth is
/// set (ADR-0013's dual gate): its principal is the system user, which has no
/// passkey. And a session that began with a recovery code, for
/// <see cref="StepUpActions.PasskeyAdd"/> only, within
/// <see cref="RecoveryPasskeyWindow"/> of signing in, and only until it has added one:
/// such a session may have no usable passkey at all, and without this it could never
/// get one.</para>
/// </remarks>
public sealed class StepUpGuard
{
    public const string HeaderName = "X-Coffer-Step-Up";

    /// <summary>How long after a recovery-code sign-in the session may add a passkey untapped.</summary>
    public static readonly TimeSpan RecoveryPasskeyWindow = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions ProofJson = new(JsonSerializerDefaults.Web);

    private readonly FreshAssertionGate _gate;
    private readonly IHttpContextAccessor _http;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly CredentialsRepository _credentials;
    private readonly TimeProvider _clock;

    public StepUpGuard(
        FreshAssertionGate gate,
        IHttpContextAccessor http,
        ICurrentUserAccessor currentUser,
        CredentialsRepository credentials,
        TimeProvider clock)
    {
        _gate = gate;
        _http = http;
        _currentUser = currentUser;
        _credentials = credentials;
        _clock = clock;
    }

    /// <summary>The verdict: a failure to return, or go ahead (noting the recovery allowance).</summary>
    public readonly record struct Outcome(IResult? Failure, bool ViaRecoveryAllowance);

    /// <summary>The proof a client sends: the step-up challenge it began and the assertion.</summary>
    public sealed record Proof(Guid ChallengeId, AuthenticatorAssertionRawResponse? AssertionResponse);

    /// <summary>Require a tap for <paramref name="action"/> on the current request.</summary>
    public async Task<Outcome> RequireAsync(string action, CancellationToken cancellationToken)
    {
        if (!StepUpActions.All.Contains(action))
            throw new ArgumentOutOfRangeException(nameof(action), action, "Not a step-up action.");

        var context = _http.HttpContext
            ?? throw new InvalidOperationException("Step-up needs an HTTP request.");

        if (IsDevAuth(context.User))
            return new(null, false);

        if (action == StepUpActions.PasskeyAdd
            && await RecoveryAllowsPasskeyAddAsync(cancellationToken).ConfigureAwait(false))
            return new(null, true);

        var proof = ReadProof(context.Request);
        if (proof is null)
            return new(BusinessError.Problem(BusinessError.Codes.StepUpRequired,
                "Confirm with your passkey to do this."), false);

        var verified = await _gate
            .VerifyAsync(StepUpActions.FlowFor(action), proof.ChallengeId, proof.AssertionResponse, cancellationToken)
            .ConfigureAwait(false);
        return new(verified.Failure, false);
    }

    /// <summary>
    /// True when this session began with a recovery code less than
    /// <see cref="RecoveryPasskeyWindow"/> ago and has not added a passkey since.
    /// </summary>
    public async Task<bool> RecoveryAllowsPasskeyAddAsync(CancellationToken cancellationToken)
    {
        var principal = _http.HttpContext?.User;
        if (principal is null
            || SessionClaims.AuthMethodOf(principal) != SessionAuthMethods.RecoveryCode
            || SessionClaims.CreatedAtOf(principal) is not { } sessionStart
            || _clock.GetUtcNow().UtcDateTime - sessionStart > RecoveryPasskeyWindow)
            return false;

        var credentials = await _credentials.GetByUserAsync(_currentUser.UserId, cancellationToken)
                                            .ConfigureAwait(false);
        return !credentials.Any(c => c.CreatedAt.ToUniversalTime() > sessionStart);
    }

    private static bool IsDevAuth(ClaimsPrincipal principal) =>
        principal.Identities.Any(i => i.IsAuthenticated && i.AuthenticationType == AuthSchemes.DevAuth);

    /// <summary>The header's base64url JSON, or null when absent or unreadable.</summary>
    internal static Proof? ReadProof(HttpRequest request)
    {
        var raw = request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var s = raw.Trim().Replace('-', '+').Replace('_', '/');
            s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return JsonSerializer.Deserialize<Proof>(json, ProofJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
