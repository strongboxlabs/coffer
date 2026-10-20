using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Coffer.Api.Auth;

/// <summary>Access level a ledger-scoped endpoint requires (ADR-0020 role matrix).</summary>
public enum LedgerAccessLevel { Read, Write, Owner }

/// <summary>Per-endpoint override of a group's method-based default level.</summary>
public sealed record LedgerAccessOverride(LedgerAccessLevel Level);

/// <summary>
/// Group filters that enforce the per-ledger role (ADR-0083 D2, the API-layer
/// primary check; role-aware RLS in migration 174 is the DB backstop). The
/// <c>{ledgerId}</c> route value is resolved through <see cref="LedgerAuthorizer"/>;
/// a denied call short-circuits with the 422 business error, so a viewer gets a
/// clean rejection instead of the silent 0-row no-op RLS produces on a blocked
/// UPDATE/DELETE.
/// </summary>
public static class LedgerAccessExtensions
{
    /// <summary>
    /// Gate every endpoint in the group by HTTP method — GET/HEAD/OPTIONS => read
    /// (any grant), any mutation => write (owner/editor) — unless the endpoint carries
    /// an <see cref="AsLedgerRead{T}"/> / <see cref="AsLedgerOwner{T}"/> override.
    /// </summary>
    public static RouteGroupBuilder RequireLedgerAccess(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter((ctx, next) => GateAsync(ctx, next, forceLevel: null));
        return group;
    }

    /// <summary>
    /// Gate every endpoint at READ level (any grant), regardless of method — for
    /// user-OWNED per-ledger surfaces (dashboard preferences, account groups) that a
    /// viewer may still manage for their own view. These tables are deliberately not
    /// role-gated by migration 174.
    /// </summary>
    public static RouteGroupBuilder RequireLedgerMembership(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter((ctx, next) => GateAsync(ctx, next, forceLevel: LedgerAccessLevel.Read));
        return group;
    }

    /// <summary>Method-based gate for a single standalone ledger endpoint that isn't in a group.</summary>
    public static RouteHandlerBuilder RequireLedgerAccess(this RouteHandlerBuilder endpoint)
    {
        endpoint.AddEndpointFilter((ctx, next) => GateAsync(ctx, next, forceLevel: null));
        return endpoint;
    }

    /// <summary>Mark a single endpoint as read-level (any grant) — e.g. a compute/preview POST.</summary>
    public static TBuilder AsLedgerRead<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new LedgerAccessOverride(LedgerAccessLevel.Read));
        return builder;
    }

    /// <summary>Mark a single endpoint as owner-only — e.g. a destructive action.</summary>
    public static TBuilder AsLedgerOwner<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new LedgerAccessOverride(LedgerAccessLevel.Owner));
        return builder;
    }

    private static async ValueTask<object?> GateAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next, LedgerAccessLevel? forceLevel)
    {
        var http = ctx.HttpContext;
        var method = http.Request.Method;
        var over = http.GetEndpoint()?.Metadata.GetMetadata<LedgerAccessOverride>();

        var level = forceLevel
            ?? over?.Level
            ?? (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
                    ? LedgerAccessLevel.Read
                    : LedgerAccessLevel.Write);

        if (!http.Request.RouteValues.TryGetValue("ledgerId", out var raw)
            || !Guid.TryParse(raw?.ToString(), out var ledgerId))
            return await next(ctx);   // group without a {ledgerId} route value — nothing to gate here.

        var authz = http.RequestServices.GetRequiredService<LedgerAuthorizer>();
        var ct = http.RequestAborted;
        IResult? denied = level switch
        {
            LedgerAccessLevel.Owner => await authz.RequireOwnerAsync(ledgerId, ct).ConfigureAwait(false),
            LedgerAccessLevel.Read  => await authz.RequireReadAsync(ledgerId, ct).ConfigureAwait(false),
            _                       => await authz.RequireWriteAsync(ledgerId, ct).ConfigureAwait(false),
        };
        return denied ?? await next(ctx);
    }
}
