namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless result type for the <c>reminder_estimate_samples</c> function (migration 220):
/// one row per estimating series, carrying the SIZE and the SUM of its sample window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never an average.</b> The division is the only lossy step in the whole feature, so
/// it happens in C# beside a single rounding call at the source account's currency's
/// places, half away from zero (ADR-0114 D15). Averaging in SQL and rounding again on
/// write is precisely the double-rounding defect migration 209 existed to remove.
/// </para>
/// <para>
/// <see cref="SampleCount"/> is also the number a surface shows the user — "average of
/// the last 2 of 3 requested" — so it is part of the answer, not an implementation
/// detail of it.
/// </para>
/// </remarks>
internal sealed class ReminderEstimateSampleRow
{
    public Guid RecurringTransactionId { get; init; }

    /// <summary>How many occurrences the window actually found: at most the requested N.</summary>
    public int SampleCount { get; init; }

    /// <summary>Sum of those occurrences' source-side nets.</summary>
    public decimal SampleSum { get; init; }
}
