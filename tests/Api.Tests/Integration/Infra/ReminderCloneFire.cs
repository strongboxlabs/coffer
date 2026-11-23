using Coffer.Api.Db.Repositories;
using Coffer.Domain.Reminders;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The verbatim-clone fire, called the way the auto-post job calls it.
/// </summary>
/// <remarks>
/// No route reaches it: a person posts an occurrence through the adjusted fire routes,
/// and only the auto-post job clones a template. Tests of what the clone writes call it
/// here, over a service context of its own as the job does, rather than through a run
/// of the job, so each pins the write and not the job's choice of what is due. The
/// context comes from the production-wired factory, as the job's does: a bare seeding
/// context saves without the recompute interceptors, and the occurrence would carry no
/// balances.
/// </remarks>
internal static class ReminderCloneFire
{
    public static async Task<RemindersRepository.FireResult> FireAsync(
        TestDatabase database, Guid ledgerId, Guid reminderId, DateOnly occurrenceDate)
    {
        await using var db = database.NewServiceFactory().Create();
        var repository = new RemindersRepository(
            db,
            new RecurrenceExpander(),
            new InvestmentTransactionsRepository(db, TimeProvider.System),
            new TransactionsRepository(db, TimeProvider.System),
            TimeProvider.System);
        return await repository.FireAsync(ledgerId, reminderId, occurrenceDate);
    }

    /// <summary>The clone fire, which must post: the committed header's id.</summary>
    public static async Task<Guid> PostedAsync(
        TestDatabase database, Guid ledgerId, Guid reminderId, DateOnly occurrenceDate)
    {
        var result = await FireAsync(database, ledgerId, reminderId, occurrenceDate);
        Assert.Equal(RemindersRepository.FireOutcome.Ok, result.Outcome);
        return result.HeaderId!.Value;
    }
}
