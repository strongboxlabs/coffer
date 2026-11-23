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
/// (<c>quote-refresh</c> — ADR-0054 B; <c>snapshot</c> — ADR-0037), and
/// <c>GET /api/ledgers/{id}/schedules</c> for all of them at once.
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

        // Every job type at once, for a reader that judges the ledger's jobs together.
        routes.MapGroup("/api/ledgers/{ledgerId:guid}/schedules")
              .RequireAuthorization()
              .RequireLedgerAccess()
              .MapGet("/", ListAsync);
        return routes;
    }

    /// <summary>
    /// <c>GET /api/ledgers/{ledgerId}/schedules</c> — every known job type's schedule,
    /// keyed by job type, each defaulted exactly as the single-job GET defaults it.
    /// </summary>
    private static async Task<IResult> ListAsync(
        Guid ledgerId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SchedulesRepository schedules,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");

        var stored = await schedules.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false);
        var all = JobTypes.All
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                jobType => jobType,
                jobType => stored.GetValueOrDefault(jobType) ?? Unconfigured(jobType),
                StringComparer.Ordinal);
        return Results.Ok(all);
    }

    // What a job type with no row reads as: off, at its default time.
    private static ScheduleDto Unconfigured(string jobType) =>
        new(Enabled: false, HourLocal: DefaultHour(jobType), MinuteLocal: 0);

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
            ?? Unconfigured(jobType);
        return Results.Ok(dto);
    }

    private static async Task<IResult> PutAsync(
        Guid ledgerId,
        string jobType,
        ScheduleDto body,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        SchedulesRepository schedules,
        TimeProvider clock,
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
            body.Timezone, currentUser.UserId, clock.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
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
    private static int DefaultHour(string jobType) => jobType switch
    {
        JobTypes.Snapshot => 3,
        // Early morning: overnight postings have settled, and it is well clear of the
        // 19:00 quote refresh so a ledger running both does not fire two outbound bursts
        // at once.
        JobTypes.FeedSync => 6,
        // Between the 03:00 snapshot and the 06:00 feed sync: the snapshot is a stable
        // pre-dawn picture taken before the day's reminders land, and the reminders exist
        // before bank data arrives or anyone looks at a balance. Without an arm here it
        // would silently default to 19:00 and post the day's rent in the evening. Shared
        // with the paths that create the row, so this reads as what they create.
        JobTypes.ReminderAutoPost => ScheduleDefaults.ReminderAutoPostHour,
        _ => 19,
    };
}
