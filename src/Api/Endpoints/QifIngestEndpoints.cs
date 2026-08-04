using Microsoft.AspNetCore.Routing;

using Coffer.Api.Ingest.Qif;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger QIF file-upload ingest (ADR-0042). A thin wrapper over
/// the provider-neutral <see cref="FileIngestEndpoints"/> — the route
/// segment (<c>qif</c>), provider key
/// (<see cref="QifFileProvider.Key"/>), and error-code prefix
/// (<c>qif</c>) are the only QIF-specific values. QIF is
/// single-account-implicit: the preview surfaces one sentinel account
/// and the user binds it to a target Coffer account on import.
/// </summary>
public static class QifIngestEndpoints
{
    public static IEndpointRouteBuilder MapQifIngestEndpoints(this IEndpointRouteBuilder routes)
        => routes.MapFileIngestEndpoints(
            providerKey: QifFileProvider.Key,
            routeSegment: "qif",
            errorPrefix: "qif");
}
