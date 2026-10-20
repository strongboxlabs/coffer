using Coffer.Api.Backup;

namespace Coffer.Api.Endpoints;

/// <summary>
/// <c>GET /api/restore-outcome/{attemptId}</c> — what the boot that applied a
/// staged whole-database restore actually did (ADR-0061, amended 2026-10-09).
/// </summary>
/// <remarks>
/// <para>The browser that staged a restore used to wait for <c>/healthz</c> and
/// then say "Restore complete". <c>/healthz</c> answers whether the restore worked
/// or not, so a failed restore — logged, database untouched — was reported as a
/// success. The boot now records its outcome; this reads it back.</para>
/// <para>Anonymous, because after an applied restore the caller's session belongs
/// to a database that no longer exists (and the setup restore has no session at
/// all). The attempt id is a random GUID handed only to the caller who staged the
/// restore, and only the latest outcome is kept, so this answers nothing to anyone
/// who does not already hold the id of the most recent attempt.</para>
/// </remarks>
public static class RestoreOutcomeEndpoints
{
    public static IEndpointRouteBuilder MapRestoreOutcomeEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/restore-outcome/{attemptId:guid}", (Guid attemptId) =>
            {
                var outcome = BootstrapRestoreStaging.ReadOutcome(attemptId);
                return outcome is null
                    ? Results.NotFound()
                    : Results.Ok(new RestoreOutcomeDto(
                        outcome.Outcome, outcome.FinishedAtUtc, outcome.PreviousDatabase, outcome.Error));
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
        return routes;
    }

    public sealed record RestoreOutcomeDto(
        string Outcome, DateTime FinishedAtUtc, string? PreviousDatabase, string? Error);
}
