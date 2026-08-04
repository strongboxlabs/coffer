using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

using Coffer.Api.Auth;

namespace Coffer.Api.Db;

/// <summary>
/// EF Core <see cref="DbConnectionInterceptor"/> that runs
/// <c>SET app.user_id = '&lt;uuid&gt;'</c> on every connection the
/// runtime <see cref="AppDbContext"/> opens, reading the user-id from
/// the request-scoped <see cref="ICurrentUserAccessor"/>. The RLS
/// policies in migration 017 reference <c>current_setting('app.user_id', true)</c>;
/// without this interceptor, the GUC is unset, the policies' subqueries
/// return zero rows, and coffer_app sees nothing — fail-closed.
/// </summary>
/// <remarks>
/// <para>Session-scoped <c>SET</c> (not <c>SET LOCAL</c>) — the value
/// lives until the connection returns to the pool. Npgsql's
/// connection-return path issues <c>DISCARD ALL</c> by default (the
/// <c>No Reset On Close</c> connection-string option is false out of
/// the box), so the next request that picks up the same physical
/// connection sees a clean session and re-applies its own SET. Verified
/// against Npgsql 10's documented pooling behaviour.</para>
///
/// <para>Pre-auth code paths (cookie validation, /login/begin) connect
/// via the service-role factory and never touch this interceptor. If a
/// connection is checked out under <see cref="AppDbContext"/> while the
/// request is still pre-auth (no user resolved yet), the interceptor
/// skips the SET; RLS policies then deny everything to coffer_app —
/// which is the right behaviour, because pre-auth code should be
/// using the service role, not the app role.</para>
/// </remarks>
public sealed class AppUserDbConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ICurrentUserAccessor _currentUser;

    public AppUserDbConnectionInterceptor(ICurrentUserAccessor currentUser)
    {
        _currentUser = currentUser;
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            // No user resolved yet → leave app.user_id unset. RLS
            // policies will deny coffer_app reads on every scoped
            // table, which is the correct fail-closed posture.
            return;
        }

        // APPROVED-RAW-SQL-EXCEPTION (2026-05-11): the API
        // data-access layer is otherwise 100% LINQ + EF, with complex
        // queries bound to Postgres functions via HasDbFunction (see
        // feedback_no_raw_sql_in_api in project memory). The one
        // sanctioned exception is this interceptor: setting a
        // per-session Postgres GUC has no LINQ analogue (it's not a
        // query, it's session plumbing the RLS model depends on),
        // and pushing the SET into every repository query would
        // defeat RLS-as-default-deny.
        //
        // Greppable token "APPROVED-RAW-SQL-EXCEPTION" lets a future
        // audit (`rg APPROVED-RAW-SQL-EXCEPTION src/Api`) distinguish
        // pre-approved exceptions from new violations.
        //
        // Parameterised via Npgsql's positional placeholder so the
        // user-id can't smuggle SQL. The cast inside the policies'
        // current_setting() handles the text→uuid conversion.
        await using var cmd = (NpgsqlCommand)connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.user_id', $1, false)";
        cmd.Parameters.AddWithValue(_currentUser.UserId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
