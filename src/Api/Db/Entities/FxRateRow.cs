namespace Coffer.Api.Db.Entities;

/// <summary>
/// One exchange rate for one UTC day (<c>fx_rates</c>, migration 280, ADR-0114 D4, D6):
/// one unit of <see cref="FromCurrency"/> is worth <see cref="Rate"/> units of
/// <see cref="ToCurrency"/>.
/// </summary>
/// <remarks>
/// <para>One rate per (ledger, pair, day), whatever its source: the unique key refuses a
/// second one, so the first stored value wins (ADR-0114 D6) and a hand entry can be made
/// only for a day with no stored rate (ADR-0114 D10). The request role may insert and
/// read a rate, never update or delete one.</para>
/// <para>A day the feed skips converts at the last rate on or before it. That read is a
/// backward scan of the unique key <c>(ledger_id, from_currency, to_currency, rate_date)</c>.</para>
/// <para>Attribution: <see cref="EnteredByUserId"/> may be set only on a
/// <see cref="FxRateSource.Manual"/> rate, and every manual rate is written naming its
/// author. The column is cleared by the database when that user is deleted, and the rate
/// stays manual (migration 280's header says why the CHECK is one-way).</para>
/// </remarks>
internal sealed class FxRateRow
{
    public Guid Id { get; init; }

    public Guid LedgerId { get; init; }

    /// <summary>The currency converted from: the account's, for a fetched rate.</summary>
    public required string FromCurrency { get; init; }

    /// <summary>The currency converted into. Never <see cref="FromCurrency"/>.</summary>
    public required string ToCurrency { get; init; }

    /// <summary>The UTC day the rate is for (ADR-0070).</summary>
    public DateOnly RateDate { get; init; }

    /// <summary><c>NUMERIC(28,12)</c>, positive. Rounded once, at write, to the column's
    /// scale.</summary>
    public decimal Rate { get; init; }

    /// <summary>One of <see cref="FxRateSource"/>.</summary>
    public required string Source { get; init; }

    /// <summary>The source Frankfurter names for a fetched rate; null on a manual
    /// one.</summary>
    public string? Upstream { get; init; }

    /// <summary>Who typed a manual rate; null on a fetched one, and on a manual one whose
    /// author has been deleted.</summary>
    public Guid? EnteredByUserId { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>The values of <c>ck_fx_rates_source</c> (migration 280).</summary>
internal static class FxRateSource
{
    /// <summary>Fetched from the configured Frankfurter feed (ADR-0114 D4).</summary>
    public const string Frankfurter = "frankfurter";

    /// <summary>Typed on the rates view for a day with no stored rate
    /// (ADR-0114 D10).</summary>
    public const string Manual = "manual";
}
