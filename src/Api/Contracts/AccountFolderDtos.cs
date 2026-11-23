namespace Coffer.Api.Contracts;

/// <summary>
/// One sidebar folder (mig 258, FU-006), as
/// <c>GET /api/ledgers/{ledgerId}/account-folders</c> lists it. The list is flat and
/// ordered by name; the SPA builds the tree from <see cref="ParentId"/>.
/// </summary>
/// <param name="Id">Folder id.</param>
/// <param name="AccountType">The one account type the folder holds; it sits in that
/// type's section of the sidebar.</param>
/// <param name="ParentId">The folder this one sits in; null at the top level.</param>
/// <param name="Name">Display name, unique among its siblings ignoring case.</param>
/// <param name="AccountIds">The accounts directly in this folder (not those in its
/// sub-folders). Inactive accounts keep their place and are listed too.</param>
public sealed record AccountFolderDto(
    Guid Id,
    string AccountType,
    Guid? ParentId,
    string Name,
    IReadOnlyList<Guid> AccountIds);

/// <summary>
/// Body of <c>POST /api/ledgers/{ledgerId}/account-folders</c>. A non-null
/// <see cref="ParentId"/> creates the folder inside that one, which must be of the
/// same <see cref="AccountType"/>.
/// </summary>
public sealed class CreateAccountFolderRequest
{
    public string AccountType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Guid? ParentId { get; init; }
}

/// <summary>Body of <c>PATCH /api/ledgers/{ledgerId}/account-folders/{folderId}</c>:
/// rename. Moving is its own route (<see cref="MoveAccountFolderRequest"/>).</summary>
public sealed class RenameAccountFolderRequest
{
    public string? Name { get; init; }
}

/// <summary>Body of <c>PUT /api/ledgers/{ledgerId}/account-folders/{folderId}/parent</c>:
/// move a folder into another of its type, or to the top level with null.</summary>
public sealed class MoveAccountFolderRequest
{
    public Guid? ParentId { get; init; }
}

/// <summary>Body of <c>PUT /api/ledgers/{ledgerId}/accounts/{accountId}/folder</c>:
/// put the account in a folder of its type, or take it out of any with null.</summary>
public sealed class SetAccountFolderRequest
{
    public Guid? FolderId { get; init; }
}

/// <summary>
/// One real account's current balance, as
/// <c>GET /api/ledgers/{ledgerId}/accounts/balances</c> lists it for the sidebar
/// (FU-052). The same figure the dashboard shows: cash, plus holdings market value
/// for an investment account; liabilities are negative. Inactive accounts are
/// included, so one response serves both states of the sidebar's "show inactive".
/// </summary>
public sealed record AccountBalanceDto(Guid AccountId, decimal Balance, string CurrencyCode);

/// <summary>
/// The sidebar rail's totals, as <c>GET /api/ledgers/{ledgerId}/accounts/subtotals</c>
/// returns them (ADR-0106 D6): each account type's section heading, and each folder.
/// Every total combines accounts, so it is in the ledger's reporting currency,
/// <see cref="CurrencyCode"/>, each account's current balance converted at the latest
/// rate, summed, rounded once (ADR-0114 D7); a total that needs a missing rate is null
/// and names each pair and day it needed (ADR-0114 D9).
/// </summary>
/// <param name="CurrencyCode">The ledger's reporting currency.</param>
/// <param name="Types">One per account type the ledger has an account of: the overview's
/// type subtotals.</param>
/// <param name="Folders">One per folder, empty ones included.</param>
public sealed record AccountSubtotalsDto(
    string CurrencyCode,
    IReadOnlyList<AccountTypeSubtotalDto> Types,
    IReadOnlyList<AccountFolderSubtotalDto> Folders);

/// <summary>One account type's total over every account of the type, inactive ones
/// included (ADR-0085).</summary>
public sealed record AccountTypeSubtotalDto(
    string AccountType, decimal? Amount, IReadOnlyList<RateUnavailable> RateUnavailable);

/// <summary>One folder's total over every account beneath it at any depth, inactive ones
/// included (ADR-0085).</summary>
/// <param name="AccountIds">The accounts the total is over, by id, so the rail can tell
/// a total it holds from a folder whose accounts have moved since.</param>
/// <param name="Amount">Zero for a folder with no accounts beneath it.</param>
public sealed record AccountFolderSubtotalDto(
    Guid FolderId, IReadOnlyList<Guid> AccountIds, decimal? Amount, IReadOnlyList<RateUnavailable> RateUnavailable);
