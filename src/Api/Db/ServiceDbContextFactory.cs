using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;

namespace Coffer.Api.Db;

/// <summary>
/// Creates short-lived <see cref="AppDbContext"/> instances bound to
/// the <c>coffer_service</c> connection string (BYPASSRLS). Used by
/// code paths that cross the authentication boundary or that
/// legitimately need to write across users / ledgers:
/// <list type="bullet">
///   <item><description>The cookie auth handler — looks up sessions by
///   their SHA-256 hash before any user has been resolved.</description></item>
///   <item><description>WebAuthn /begin endpoints — look up the user
///   by username (login) or mint a pending challenge under a NULL
///   user-id (bootstrap setup).</description></item>
///   <item><description>WebAuthn /complete endpoints — verify the
///   assertion, then INSERT auth_sessions / UPDATE webauthn_credentials.
///   These rows are auth-subsystem state, not user content.</description></item>
///   <item><description><c>POST /api/ledgers</c> — inserts a ledger
///   row + an owner grant in one transaction; the grant doesn't exist
///   yet when the ledger insert's <c>WITH CHECK</c> would evaluate
///   (RLS would deny). The endpoint authenticates the user first,
///   then escalates this single write through the service factory.</description></item>
/// </list>
/// Registered as singleton — the <see cref="DbContextOptions{T}"/> is
/// built once and reused. Each <see cref="Create"/> call returns a
/// fresh context the caller is responsible for disposing.
/// </summary>
/// <remarks>
/// Using <see cref="AppDbContext"/> (the same type as the runtime
/// context) means the EF model is shared — entities, FKs, view
/// mappings, all identical. The only difference is the underlying
/// connection. No <see cref="AppUserDbConnectionInterceptor"/> is
/// attached, so connections don't carry an <c>app.user_id</c> GUC;
/// the BYPASSRLS attribute on coffer_service skips RLS regardless.
/// </remarks>
public sealed class ServiceDbContextFactory
{
    private readonly DbContextOptions<AppDbContext> _options;

    public ServiceDbContextFactory(IOptions<ApiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.Value.ServiceConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Api:ServiceConnectionString is required from PR 3.8 onward — set it in configuration.");
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    /// <summary>
    /// Construct a fresh <see cref="AppDbContext"/> over the
    /// service-role connection string. Caller owns disposal (typically
    /// via <c>await using</c>).
    /// </summary>
    public AppDbContext Create() => new AppDbContext(_options);
}
