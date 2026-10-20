using System.Threading.RateLimiting;

using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;

namespace Coffer.Api.Auth;

/// <summary>
/// Recovery-code attempts per username per hour, wherever they come from (FU-088).
/// Singleton.
/// </summary>
/// <remarks>
/// The route's rate limit is per client address, which bounds one client and, before the
/// proxy's forwarded address was believed, was one bucket for everyone. Neither bounds a
/// guess spread over many addresses against one account, and every attempt there costs up
/// to ten Argon2id verifies (one per unused code). This is taken before the user is looked
/// up, for every username asked about, so its answer says nothing about whether the account
/// exists. The cost: someone can spend an account's attempts and hold its recovery-code
/// sign-in at 429 for the hour; passkey sign-in is not affected.
/// </remarks>
public sealed class RecoveryAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public RecoveryAttemptLimiter(IOptions<ApiOptions> options)
    {
        var permits = Math.Max(1, options.Value.Auth.RecoveryAttemptsPerAccountPerHour);
        _limiter = PartitionedRateLimiter.Create<string, string>(username =>
            RateLimitPartition.GetFixedWindowLimiter(username, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
            }));
    }

    /// <summary>True when an attempt for <paramref name="username"/> may proceed.</summary>
    public bool TryAcquire(string username)
    {
        using var lease = _limiter.AttemptAcquire(username.Trim().ToUpperInvariant());
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
