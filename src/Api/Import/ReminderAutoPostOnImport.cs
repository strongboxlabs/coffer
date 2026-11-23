using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using Coffer.Api.Db;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Reminders;
using Coffer.Api.Scheduling;

namespace Coffer.Api.Import;

/// <summary>
/// A Moneydance import that brings a reminder set to auto-commit turns the new ledger's
/// <c>reminder-auto-post</c> job on, as saving such a reminder in the app does.
/// </summary>
/// <remarks>
/// <para>
/// Moneydance's <c>acdays</c> is the same request as the app's "Auto-post / N days
/// before" tick: the importer stores it as <c>AutoCommitDaysBefore</c>, and the reminders
/// page shows the reminder as auto-posting. Ticking it in the app IS the opt-in
/// (ADR-0097), so an import that left the job unconfigured would show a reminder as
/// posting itself while nothing ever posted it, the silent failure that ADR exists to
/// remove.
/// </para>
/// <para>
/// Run inside the import's own transaction, last, after the reminders are written: the
/// ledger and its schedule commit or roll back together, so an imported ledger never has
/// an auto-commit reminder without the job that acts on it. Create-if-absent through
/// <see cref="SchedulesRepository.EnsureCreatedEnabledAsync"/>; a brand-new ledger has no
/// row yet, so in practice this creates one.
/// </para>
/// <para>
/// A ledger imported before this ran is found by the consistency check's
/// <c>reminder_auto_post</c> projection, which reads the same rule
/// (<see cref="AutoPostRequests"/>), and its Repair turns the job on the same way.
/// </para>
/// </remarks>
internal static class ReminderAutoPostOnImport
{
    /// <summary>
    /// Create the ledger's enabled auto-post schedule, configured by
    /// <paramref name="ownerUserId"/>, when any of its reminders asks to be auto-posted.
    /// </summary>
    internal static async Task EnsureOnAsync(
        DbTransaction transaction, Guid ledgerId, Guid ownerUserId, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection
            ?? throw new InvalidOperationException("The import's transaction has no connection.");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);

        // The consistency check's reading of the same rule (AutoPostRequests), so a ledger
        // this leaves without the job is one the check reports.
        var asksForAutoPost = await AutoPostRequests.In(db, ledgerId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!asksForAutoPost) return;

        await new SchedulesRepository(db).EnsureCreatedEnabledAsync(
            ledgerId, JobTypes.ReminderAutoPost, ScheduleDefaults.ReminderAutoPostHour,
            ownerUserId, nowUtc, cancellationToken).ConfigureAwait(false);
    }
}
