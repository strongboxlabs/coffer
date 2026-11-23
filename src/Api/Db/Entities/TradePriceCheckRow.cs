namespace Coffer.Api.Db.Entities;

/// <summary>
/// Keyless query type for the <c>fn_trade_price_check(p_ledger_id)</c> TVF in
/// migration 232 — one row per (security, day) that migration 177's rank rule
/// implies a trade-derived <c>security_prices</c> entry for.
///
/// <para>Rows the check found CONSISTENT come back too, with a null
/// <see cref="Reason"/>. The caller needs the examined count as well as the
/// disagreements, and deriving the former separately would mean a second copy
/// of the eligibility rule.</para>
///
/// <para>The rule lives in the function rather than here on purpose. Restating
/// it in LINQ would be a second copy of the writer's logic, free to drift from
/// the thing it is checking — which is the failure this projection's check
/// exists to catch in the first place.</para>
/// </summary>
internal sealed class TradePriceCheckRow
{
    public Guid SecurityId { get; init; }
    public DateOnly PriceDate { get; init; }

    /// <summary>What the last trade of the day implies, rounded to the
    /// destination column's 4dp.</summary>
    public decimal ExpectedPrice { get; init; }

    /// <summary>The stored price, or null when no row exists at all.</summary>
    public decimal? StoredPrice { get; init; }

    /// <summary>The stored row's source, or null when none exists.</summary>
    public string? StoredSource { get; init; }

    /// <summary>
    /// <c>missing</c> (<see cref="Contracts.ConsistencyReasons.NotStored"/>: a trade
    /// seeded nothing), <c>value</c> (<see cref="Contracts.ConsistencyReasons.ValueDiffers"/>:
    /// a rank-comparable row holds a stale price, or a price in another currency than the
    /// brokerage that made the trade), or NULL — consistent. NULL also covers a
    /// <c>fetch</c>/<c>manual</c> row that outranks the trade: those days are
    /// checked and found legitimate even when the two prices disagree, because a
    /// missing trade price is CORRECT on a day a close or a gap-fill owns. A stored
    /// trade price the rule derives nothing for is not returned since migration 240;
    /// the unbacked-price advisory lists it (ADR-0084 D6).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The account the determining trade sits on (migration 233) — the
    /// holdings sibling, since that is where a security leg lives. Set on every row
    /// since migration 240: each is a day a trade decides.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>The header of the trade that decided the day (migration 233), so a
    /// reader can go look at it before deciding whether to repair. Set on every row
    /// since migration 240, as <see cref="AccountId"/> is.</summary>
    public Guid? HeaderId { get; init; }

    /// <summary>The currency of the brokerage that made the determining trade, which the
    /// day's price is in (migration 274's <c>fn_trade_currency</c>).</summary>
    public string ExpectedCurrency { get; init; } = string.Empty;

    /// <summary>The stored row's currency, or null when none exists. A
    /// <c>value</c> reason covers a stored import, simplefin or trade row in another
    /// currency as it does one at another price (migration 274).</summary>
    public string? StoredCurrency { get; init; }

    public bool Drifted => Reason is not null;
}
