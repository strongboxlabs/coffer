using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tool over the reminders agenda (ADR-0047). Read-only; RLS scopes every
/// read to the bearer's user (an out-of-grant <c>ledgerId</c> yields empty rows).
/// </summary>
[McpServerToolType]
public static class RemindersTools
{
    [McpServerTool(Name = "list_upcoming_reminders"), Description(
        "Upcoming scheduled transactions — bills and recurring reminders (ADR-0047) — " +
        "for a ledger over the next N days: date, kind, payee, memo, amount (negative = " +
        "outflow, positive = inflow), and reminderId. 'kind' is 'reminder' (due, not " +
        "yet posted — what's coming up), 'scheduled' (already posted for that date), or " +
        "'skipped' (a skipped slot); filter to 'reminder' for the true agenda of what's " +
        "still owed. Ordered by date. Amounts in the ledger's currency (USD). Use " +
        "list_ledgers first to resolve ledgerId.")]
    public static async Task<IReadOnlyList<UpcomingOccurrence>> ListUpcomingReminders(
        RemindersRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Window size in days ahead of today (default 30, clamped to 1..732).")]
        int daysAhead = 30,
        CancellationToken cancellationToken = default)
    {
        // Server-side "today" (UTC). A day-boundary fuzz vs the user's local date is
        // immaterial for a "next N days" agenda; the window is clamped like the
        // reminders/upcoming endpoint (ADR-0047).
        var from = DateOnly.FromDateTime(DateTime.UtcNow);
        var to = from.AddDays(Math.Clamp(daysAhead, 1, 732));
        return await repository.GetUpcomingAsync(ledgerId, from, to, cancellationToken)
            .ConfigureAwait(false);
    }
}
