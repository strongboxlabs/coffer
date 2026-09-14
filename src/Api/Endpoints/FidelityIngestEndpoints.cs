using Microsoft.AspNetCore.Routing;

using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger Fidelity brokerage CSV ingest (ADR-0031 Phase 6).
/// </summary>
/// <remarks>
/// <para>A thin wrapper over the provider-neutral <see cref="FileIngestEndpoints"/>, like
/// its OFX and QIF siblings: the route segment, provider key and error prefix are the
/// only Fidelity-specific values.</para>
///
/// <para>It gets its OWN route rather than riding the generic CSV one, even though both
/// take a <c>.csv</c>. The generic path requires a mapping document and takes one per
/// request; a Fidelity export needs none, because the format knowledge is in the shim.
/// Sharing a route would mean an endpoint that sometimes requires a mapping and
/// sometimes refuses one, decided by sniffing the body — a worse contract than two
/// routes that each mean one thing.</para>
/// </remarks>
public static class FidelityIngestEndpoints
{
    public static IEndpointRouteBuilder MapFidelityIngestEndpoints(this IEndpointRouteBuilder routes)
        => routes.MapFileIngestEndpoints(
            providerKey: FidelityActivityFileProvider.Key,
            routeSegment: "fidelity",
            errorPrefix: "fidelity");
}
