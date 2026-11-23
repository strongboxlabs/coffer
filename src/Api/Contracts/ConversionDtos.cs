namespace Coffer.Api.Contracts;

/// <summary>
/// Why a converted figure is null: it needed the rate from <see cref="FromCurrency"/> into
/// <see cref="ToCurrency"/> for <see cref="Date"/>, and that rate is missing (ADR-0114 D9).
/// </summary>
/// <remarks>
/// <para>A rate is missing when the pair has no rate on or before the day, or when the day
/// is after the pair's coverage: the later of the last day its fetches covered and its
/// latest hand-entered day. No staleness threshold applies, and no account is left out of
/// a total: the figure is null, never a partial sum.</para>
/// <para>Serialized as <c>{"reason":"rate_unavailable","fromCurrency":"EUR",
/// "toCurrency":"USD","date":"2026-01-30"}</c> on every surface: the screens' APIs, REST
/// and MCP.</para>
/// </remarks>
public sealed record RateUnavailable(string FromCurrency, string ToCurrency, DateOnly Date)
{
    /// <summary>The reason code a null converted figure carries.</summary>
    public const string Code = "rate_unavailable";

    /// <summary>Always <see cref="Code"/>, so a reader that keys on a reason string reads
    /// this one the way it reads every other.</summary>
    public string Reason => Code;
}

/// <summary>
/// The rate between two account currencies for a day, derived through the ledger's
/// reporting currency (ADR-0114 D13, D14): what the editors prefill a transfer's second
/// amount from and what their rate line says.
/// <c>GET /api/ledgers/{ledgerId}/fx-rates/{fromCurrency}/{toCurrency}/derived?date=yyyy-MM-dd&amp;amount=</c>,
/// open to any member; it writes nothing, and a transfer never writes <c>fx_rates</c>.
/// </summary>
/// <remarks>
/// <para>For account currencies X (<see cref="FromCurrency"/>) and Y
/// (<see cref="ToCurrency"/>) and the reporting currency R: when R is Y it is the stored
/// X→R rate, when R is X the inverse of the stored Y→R rate, otherwise the cross, X→R over
/// Y→R. No pair between two account currencies is read or stored. Each stored leg is read
/// by the one rate rule (<c>RateDay.Of</c>): a day before the server's UTC today reads the
/// pair's rate on or before it, today or a later day the latest, and a pair whose coverage
/// has lapsed has none (ADR-0114 D9).</para>
/// <para>The rate and the converted figure are computed at full precision, and only
/// <see cref="Converted"/> is rounded, once, at <see cref="ToCurrency"/>'s places, half away
/// from zero (ADR-0114 D15). The client shows <see cref="Converted"/> rather than
/// multiplying, so no figure is rounded twice.</para>
/// </remarks>
/// <param name="FromCurrency">X, the currency the amount is in.</param>
/// <param name="ToCurrency">Y, the currency the second amount is in.</param>
/// <param name="Date">The day asked for.</param>
/// <param name="ReportingCurrency">R, the currency the rate is derived through.</param>
/// <param name="Rate">One unit of <paramref name="FromCurrency"/> in
/// <paramref name="ToCurrency"/>; null when a stored leg is missing.</param>
/// <param name="RateDate">The day the rate is good for: the older of its stored legs'
/// days, which the rate line names. Null exactly when <paramref name="Rate"/> is.</param>
/// <param name="Legs">The stored legs read, in the direction they are stored (account
/// currency into R), each with its own day and rate: one when R is X or Y, two otherwise.
/// Empty when <paramref name="Rate"/> is null.</param>
/// <param name="Converted">The request's <c>amount</c> at <paramref name="Rate"/>, rounded
/// once at <paramref name="ToCurrency"/>'s places, half away from zero, with the amount's
/// sign. Null when no amount was sent or <paramref name="Rate"/> is null.</param>
/// <param name="RateUnavailable">Each missing stored leg, in the direction it is stored
/// (account currency into R), and the day; empty when the rate is there.</param>
public sealed record DerivedRateDto(
    string FromCurrency,
    string ToCurrency,
    DateOnly Date,
    string ReportingCurrency,
    decimal? Rate,
    DateOnly? RateDate,
    IReadOnlyList<DerivedRateLegDto> Legs,
    decimal? Converted,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>One stored rate a <see cref="DerivedRateDto"/> was derived from: one unit of
/// <paramref name="FromCurrency"/> in <paramref name="ToCurrency"/>, the reporting
/// currency, as stored for <paramref name="Date"/>, the day the one rate rule read.</summary>
public sealed record DerivedRateLegDto(string FromCurrency, string ToCurrency, DateOnly Date, decimal Rate);
