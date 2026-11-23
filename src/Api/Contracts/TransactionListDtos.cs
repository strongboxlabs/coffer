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

/// <summary>
/// Cash-flow direction filter, by sign: above zero is an inflow, below it an outflow,
/// zero neither. A transaction line (<see cref="TransactionQuery"/>) is judged by its own
/// amount; a register entry (<c>GET /transactions/entries</c>) by the amount its row
/// shows on its account.
/// </summary>
public enum TransactionDirection { All, Inflow, Outflow }

/// <summary>
/// Filter/sort/page parameters for the transaction line-drill (ADR-0063 §D5 v2).
/// Reads the override-aware <c>resolved_transactions</c> view. No date window is
/// required. Filters compose:
/// <list type="bullet">
///   <item><see cref="AccountId"/> — lines on that account's register (any type;
///   a category id works too), shown as that register shows them. When omitted,
///   only real-account legs are returned (category legs excluded): a transaction
///   against categories appears once per line, from the money side (a split with
///   several category lines appears once for each); a transfer between two real
///   accounts appears once per account, both lines shown as the outflow leg's
///   register shows the transfer (for a zero amount, the originating side's
///   leg).</item>
///   <item><see cref="CategoryId"/> — lines whose counterparty is that category
///   ("what I spent on X"); composes with <see cref="AccountId"/>. Without an
///   account, each line is shown as that category's register shows it.</item>
///   <item><see cref="Payee"/> exact / <see cref="Text"/> substring (payee, the memo
///   the line shows, and the transaction memo on a split's own lines).</item>
///   <item><see cref="MinAmount"/>/<see cref="MaxAmount"/> bound the <b>absolute</b>
///   amount; <see cref="Direction"/> filters by sign.</item>
/// </list>
/// </summary>
public sealed class TransactionQuery
{
    public required Guid LedgerId { get; init; }
    public Guid? AccountId { get; init; }
    public Guid? CategoryId { get; init; }
    /// <summary>
    /// Restrict to lines facing ANY of these categories. Null or empty = no
    /// restriction; combined with <see cref="CategoryId"/> both must hold.
    /// </summary>
    /// <remarks>
    /// Exists for the budget screen, where expanding a row shows the whole
    /// SUBTREE's transactions so the list adds up to the row's number — the row
    /// is a rollup, so direct postings alone would not. The caller passes one
    /// category and the server expands it; the alternative was one request per
    /// descendant, which for a deep branch is a request storm for a click.
    /// </remarks>
    public IReadOnlyCollection<Guid>? CategoryIds { get; init; }
    /// <summary>Restrict to lines this tag is in play for (ADR-0077): what the line's register shows, and on a split's own lines the transaction's tags. Null = any.</summary>
    public Guid? TagId { get; init; }
    /// <summary>
    /// Present every line from its own leg, as its own account's register does, with no
    /// account chosen. By default a line between two real accounts is read from the posting's
    /// outflow leg, so both lines of a transfer show that side's notes. A list that is meant to
    /// agree with every register it links to (a tag's lines, ADR-0111 D6) sets this, so a tag
    /// held on one side of a transfer only is found on that side's line.
    /// </summary>
    public bool PresentFromOwnLeg { get; init; }
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
/// <param name="Memo">The memo the register of the leg in scope shows (see <see cref="ITransactionLineNotes"/>).</param>
/// <param name="TransactionMemo">The transaction's memo, on split-side lines only.</param>
/// <param name="Tags">The tags in play for the line, each once.</param>
/// <param name="LegId">The line's own leg, on <paramref name="AccountId"/>: unique per
/// line, where <paramref name="HeaderId"/> with <paramref name="AccountId"/> is not (a
/// split's lines share both), so it is what names a line.</param>
/// <param name="CurrencyCode">The ISO 4217 currency <paramref name="Amount"/> is in: that
/// of <paramref name="AccountId"/>, the line's own account, unless that is a category. A
/// category holds no money of its own, so a category's line (listed when a category is the
/// account in scope) is in the currency of <paramref name="CounterpartyAccountId"/>, the
/// account on the posting's other side; only a line between two categories takes the
/// category's own code. Nothing converts it: lines in different currencies sit in one
/// listing and do not sum.</param>
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
    string Status,
    string? TransactionMemo,
    IReadOnlyList<string> Tags,
    Guid LegId,
    string CurrencyCode) : ITransactionLineNotes;

/// <summary>A page of transaction lines + paging cursor (offset-based;
/// <paramref name="HasMore"/> reflects a peek one past <c>Limit</c>).</summary>
public sealed record TransactionLinesResult(
    IReadOnlyList<TransactionLine> Lines,
    int Limit,
    int Offset,
    bool HasMore);
