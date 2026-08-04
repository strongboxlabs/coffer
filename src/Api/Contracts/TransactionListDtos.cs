namespace Coffer.Api.Contracts;

/// <summary>How <see cref="TransactionQuery"/> orders results.</summary>
public enum TransactionSort
{
    /// <summary>By posted date (then header seq) — the register order.</summary>
    Date,
    /// <summary>By signed amount.</summary>
    Amount,
    /// <summary>By absolute amount (largest movements regardless of sign).</summary>
    AbsAmount,
}

/// <summary>Cash-flow direction filter, by the sign of the leg's amount.</summary>
public enum TransactionDirection { All, Inflow, Outflow }

/// <summary>
/// Filter/sort/page parameters for the transaction line-drill (ADR-0063 §D5 v2).
/// Reads the override-aware <c>resolved_transactions</c> view. No date window is
/// required. Filters compose:
/// <list type="bullet">
///   <item><see cref="AccountId"/> — lines on that account's register (any type;
///   a category id works too). When omitted, only real-account legs are returned
///   (category legs excluded) so each transaction appears once from the money
///   side.</item>
///   <item><see cref="CategoryId"/> — lines whose counterparty is that category
///   ("what I spent on X"); composes with <see cref="AccountId"/>.</item>
///   <item><see cref="Payee"/> exact / <see cref="Text"/> substring (payee+memo).</item>
///   <item><see cref="MinAmount"/>/<see cref="MaxAmount"/> bound the <b>absolute</b>
///   amount; <see cref="Direction"/> filters by sign.</item>
/// </list>
/// </summary>
public sealed class TransactionQuery
{
    public required Guid LedgerId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? CategoryId { get; init; }
    /// <summary>Restrict to transactions carrying this tag (ADR-0077). Null = any.</summary>
    public Guid? TagId { get; init; }
    public string? Payee { get; init; }
    public string? Text { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public TransactionDirection Direction { get; init; } = TransactionDirection.All;
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public TransactionSort Sort { get; init; } = TransactionSort.Date;
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 200;
    public int Offset { get; init; }
}

/// <summary>One transaction line (a leg) from the override-aware view.</summary>
/// <param name="AccountName">The leg's own account.</param>
/// <param name="CounterpartyAccountName">The other side of the posting (the
/// category for a cash leg, the account for a category leg). Full path.</param>
public sealed record TransactionLine(
    Guid HeaderId,
    DateTime PostedAt,
    string? Payee,
    Guid AccountId,
    string AccountName,
    Guid? CounterpartyAccountId,
    string? CounterpartyAccountName,
    decimal Amount,
    string? Memo,
    string Status);

/// <summary>A page of transaction lines + paging cursor (offset-based;
/// <paramref name="HasMore"/> reflects a peek one past <c>Limit</c>).</summary>
public sealed record TransactionLinesResult(
    IReadOnlyList<TransactionLine> Lines,
    int Limit,
    int Offset,
    bool HasMore);
