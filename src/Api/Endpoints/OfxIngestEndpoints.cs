using Microsoft.AspNetCore.Routing;

using Coffer.Api.Ingest.Ofx;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger OFX/QFX file-upload ingest (ADR-0031 Phase 4). A thin
/// wrapper over the provider-neutral <see cref="FileIngestEndpoints"/>
/// — the route segment (<c>ofx</c>), provider key
/// (<see cref="OfxFileProvider.Key"/>), and error-code prefix
/// (<c>ofx</c>) are the only OFX-specific values.
/// </summary>
public static class OfxIngestEndpoints
{
    public static IEndpointRouteBuilder MapOfxIngestEndpoints(this IEndpointRouteBuilder routes)
        => routes.MapFileIngestEndpoints(
            providerKey: OfxFileProvider.Key,
            routeSegment: "ofx",
            errorPrefix: "ofx");
}
