using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Fido2NetLib;

using Coffer.Api.Auth.Webauthn;
using Coffer.Api.Errors;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Starts a fresh passkey tap for one sensitive action (2026-10-12 audit, M2). The
/// client runs the returned ceremony and sends the result in the
/// <see cref="StepUpGuard.HeaderName"/> header of the request it is approving.
/// </summary>
public static class StepUpEndpoints
{
    public static IEndpointRouteBuilder MapStepUpEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/auth/step-up/begin", BeginAsync).RequireAuthorization();
        return routes;
    }

    private static async Task<IResult> BeginAsync(
        StepUpBeginRequest request,
        FreshAssertionGate gate,
        CancellationToken cancellationToken)
    {
        if (request?.Action is not { } action || !StepUpActions.All.Contains(action))
            return BusinessError.Problem(BusinessError.Codes.StepUpUnknownAction,
                "action must be one of: " + string.Join(", ", StepUpActions.All.Order(StringComparer.Ordinal)) + ".");

        var begun = await gate.BeginAsync(StepUpActions.FlowFor(action), cancellationToken)
                              .ConfigureAwait(false);
        return begun.Failure ?? Results.Ok(new StepUpBeginResponse(begun.ChallengeId, begun.Options!));
    }
}

public sealed record StepUpBeginRequest(string? Action);

public sealed record StepUpBeginResponse(Guid ChallengeId, AssertionOptions AssertionOptions);
