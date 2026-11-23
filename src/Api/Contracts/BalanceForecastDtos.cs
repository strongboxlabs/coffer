namespace Coffer.Api.Contracts;

/// <summary>
/// <c>GET /api/ledgers/{ledgerId}/accounts/balance-forecast?today&amp;days</c>: each
/// account's balance from the caller's today through <c>days</c> ahead, from the
/// register's committed rows and the reminders not yet posted. A projection, never a
/// fact: nothing here is counted anywhere else.
/// </summary>
/// <param name="Accounts">One row per account in scope, by name. The scope is every
/// account that is the source of an active reminder: not a category, not a holdings
/// sibling, inactive ones too.</param>
/// <param name="Combined">The accounts summed in the ledger's reporting currency, each
/// converted at the latest rate on every day, today and the forecast days alike
/// (ADR-0114 D7); null when there are none, or when a rate is missing, which
/// <paramref name="RateUnavailable"/> then names (ADR-0114 D9).</param>
/// <param name="RateUnavailable">Every missing rate the combined line needed.</param>
/// <param name="IncludesEstimates">True when an estimating reminder's amount was placed
/// on an account in scope on any day of the window: its estimate, or the amount entered
/// while it has no history to estimate from, as the Bills widget shows it. Which figures
/// it reached is each <see cref="BalanceForecastLowestDto.Estimated"/>.</param>
/// <param name="OverdueOnDay1">The un-posted occurrences dated before today that were
/// placed, all of them on day 1: the reminders the Bills widget calls overdue.</param>
/// <param name="Excluded">The reminders that would move an account in scope but could
/// not be placed, one row per reminder, with why.</param>
public sealed record BalanceForecastDto(
    IReadOnlyList<BalanceForecastAccountDto> Accounts,
    BalanceForecastCombinedDto? Combined,
    bool IncludesEstimates,
    int OverdueOnDay1,
    IReadOnlyList<BalanceForecastExclusionDto> Excluded,
    IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>One account's projection.</summary>
/// <param name="Today">The balance on day 0: the register's own current balance for
/// the caller's today, exactly (a brokerage's is its cash).</param>
/// <param name="Lowest">The lowest balance over <paramref name="Points"/>, day 0
/// included, the first date it is reached, and whether an estimate landed on this
/// account by then.</param>
/// <param name="Points">One point per day, day 0 (today) through the last day of the
/// window: the balance at the end of that day.</param>
public sealed record BalanceForecastAccountDto(
    Guid AccountId,
    string Name,
    string CurrencyCode,
    decimal Today,
    BalanceForecastLowestDto Lowest,
    IReadOnlyList<BalanceForecastPointDto> Points);

/// <summary>The accounts in scope summed, in the ledger's reporting currency; the same
/// shape as an account's projection, without an account.</summary>
public sealed record BalanceForecastCombinedDto(
    string CurrencyCode,
    decimal Today,
    BalanceForecastLowestDto Lowest,
    IReadOnlyList<BalanceForecastPointDto> Points);

/// <summary>The lowest projected balance and the first date it is reached.</summary>
/// <param name="Estimated">True when an estimating reminder's amount (its estimate, or
/// the amount entered before it has one), or its negation, landed on the figure's
/// account on or before <paramref name="Date"/>, so the figure rests on an estimate: for
/// an account's lowest, on that account; for the combined lowest, on any account in
/// scope. An estimate that lands after <paramref name="Date"/> does not make it one.</param>
public sealed record BalanceForecastLowestDto(decimal Amount, DateOnly Date, bool Estimated);

/// <summary>The projected balance at the end of <paramref name="Date"/>.</summary>
public sealed record BalanceForecastPointDto(DateOnly Date, decimal Balance);

/// <summary>A reminder left out of the projection.</summary>
/// <param name="Reason">
/// <c>no-lines</c>: its template has no lines, so it names no account to move.
/// <c>override-not-placeable</c>: its amount is computed (a loan's payment, an
/// estimate) and cannot be placed on the accounts in scope: a loan line whose split is
/// priced per payment lands in scope, the reminder has no source account, or an
/// estimate's template lacks the source line or the other line.
/// <c>cross-currency</c>: a managed loan payment across currencies, which can never post
/// (ADR-0114 D13).
/// <c>rate-unavailable</c>: an estimating reminder between currencies whose other side
/// needs the latest derived rate, and a stored leg of it is missing
/// (<paramref name="RateUnavailable"/>).
/// </param>
/// <param name="RateUnavailable">On a <c>rate-unavailable</c> row, each missing stored leg
/// in the direction it is stored (account currency into the reporting currency) and its
/// day (ADR-0114 D9, D13); absent from the JSON on every other row.</param>
public sealed record BalanceForecastExclusionDto(
    Guid ReminderId,
    string Reason,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<RateUnavailable>? RateUnavailable = null);
