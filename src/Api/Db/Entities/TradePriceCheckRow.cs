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
    /// <c>missing</c> (a trade seeded nothing), <c>value</c> (a rank-comparable
    /// row holds a stale price), <c>orphaned</c> (a stored trade price the rule
    /// derives nothing for — reported, never repaired automatically, because
    /// ADR-0084 D4 keeps some of them deliberately), or NULL — consistent. NULL also covers a
    /// <c>fetch</c>/<c>manual</c> row that outranks the trade: those days are
    /// checked and found legitimate even when the two prices disagree, because a
    /// missing trade price is CORRECT on a day a close or a gap-fill owns.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The account the determining trade sits on (migration 233) — the
    /// holdings sibling, since that is where a security leg lives. NULL on an
    /// orphan: no trade decided it, so naming an account would be a fiction.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>The header of the trade that decided the day (migration 233), so a
    /// reader can go look at it before deciding whether to repair. NULL on an
    /// orphan, which is what tells the UI to link to the security's price list
    /// instead of to a register.</summary>
    public Guid? HeaderId { get; init; }

    public bool Drifted => Reason is not null;
}
