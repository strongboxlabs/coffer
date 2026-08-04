using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Per-ledger daily schedules (mig 136). One generic surface for every
/// <c>job_type</c>: <c>GET/PUT /api/ledgers/{id}/schedules/{jobType}</c>
/// (<c>quote-refresh</c> — ADR-0054 B; <c>snapshot</c> — ADR-0037).
/// </summary>
public static class SchedulesEndpoints
{
    public static IEndpointRouteBuilder MapSchedulesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ledgers/{ledgerId:guid}/schedules/{jobType}")
                          .RequireAuthorization()
                          .RequireLedgerAccess();
        group.MapGet("/", GetAsync);
        group.MapPut("/", PutAsync);
        return routes;
    }

    private static async Task<IResult> GetAsync(
        Guid ledgerId,
        string jobType,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SchedulesRepository schedules,
        CancellationToken cancellationToken)
    {
        var gate = await GuardAsync(ledgers, currentUser, ledgerId, jobType, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null) return gate;

        var dto = await schedules.GetAsync(ledgerId, jobType, cancellationToken).ConfigureAwait(false)
            ?? new ScheduleDto(Enabled: false, HourLocal: DefaultHour(jobType), MinuteLocal: 0);
        return Results.Ok(dto);
    }

    private static async Task<IResult> PutAsync(
        Guid ledgerId,
        string jobType,
        ScheduleDto body,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SchedulesRepository schedules,
        CancellationToken cancellationToken)
    {
        var gate = await GuardAsync(ledgers, currentUser, ledgerId, jobType, cancellationToken)
            .ConfigureAwait(false);
        if (gate is not null) return gate;
        if (body is null || body.HourLocal is < 0 or > 23 || body.MinuteLocal is < 0 or > 59)
            return BusinessError.Problem(BusinessError.Codes.ScheduleInvalid,
                "Hour must be 0–23 and minute 0–59.");

        var saved = await schedules.UpsertAsync(
            ledgerId, jobType, body.Enabled, (short)body.HourLocal, (short)body.MinuteLocal,
            body.Timezone, currentUser.UserId, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
        return Results.Ok(saved);
    }

    /// <summary>Ledger-visible + known-job_type gate; null when OK.</summary>
    private static async Task<IResult?> GuardAsync(
        LedgersRepository ledgers, ICurrentUserAccessor currentUser,
        Guid ledgerId, string jobType, CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");
        if (!JobTypes.All.Contains(jobType))
            return BusinessError.Problem(BusinessError.Codes.ScheduleJobTypeUnknown,
                $"'{jobType}' is not a known schedule job type.");
        return null;
    }

    // Sensible default time-of-day per job type (used only when no row exists).
    private static int DefaultHour(string jobType) =>
        jobType == JobTypes.Snapshot ? 3 : 19;
}
