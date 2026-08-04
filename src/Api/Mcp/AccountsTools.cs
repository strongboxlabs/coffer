using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Mcp;

/// <summary>
/// MCP tools over the account catalog + net worth (ADR-0063 §D5 v2). Read-only;
/// balances are Overview-consistent (investment accounts include holdings market
/// value), so a sum of accounts and the dedicated net_worth agree and both match
/// the app. RLS scopes every read to the bearer's user.
/// </summary>
[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool(Name = "list_accounts"), Description(
        "List a ledger's accounts (and optionally categories): id, name, type, " +
        "categoryKind, parentId (the category tree — real accounts are flat), " +
        "currency, active flag, class ('asset' | 'liability' | 'none'), taxStatus " +
        "('taxable' | 'tax_deferred' | 'tax_free' | 'other'; null = unknown), and balance. " +
        "Balances are Overview-consistent — investment accounts include holdings " +
        "market value (cash + positions), not just cash. Categories have a null " +
        "balance (they carry flows; use transaction_summary). Excludes internal " +
        "holdings-sibling accounts. Use this to resolve account ids for the other " +
        "tools and to total assets/liabilities. USD.")]
    public static async Task<IReadOnlyList<AccountInfo>> ListAccounts(
        AccountsReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Include budget categories too. Default false (real accounts only).")]
        bool includeCategories = false,
        [Description("Include archived/inactive accounts. Default false.")]
        bool includeInactive = false,
        [Description("Restrict to one account type (e.g. 'bank', 'investment', 'credit_card'). Omit for all.")]
        string? type = null,
        CancellationToken cancellationToken = default) =>
        await repository.ListAccountsAsync(ledgerId, includeCategories, includeInactive, type, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "net_worth"), Description(
        "Current net worth for a ledger: total assets, total liabilities (stored " +
        "negative, so net worth = assets + liabilities), investments value, and a " +
        "per-account breakdown. Investment accounts include holdings market value. " +
        "This is the authoritative figure — it matches the app's Overview screen — so " +
        "prefer it over summing list_accounts yourself. As of now. USD.")]
    public static async Task<McpNetWorth> NetWorth(
        AccountsReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        await repository.NetWorthAsync(ledgerId, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "net_worth_history"), Description(
        "Net worth OVER TIME: a series of points, each the ledger's net worth as of the " +
        "end of a period (interval 'month' | 'quarter' | 'year') within [fromUtc, toUtc], " +
        "the final point clamped to toUtc. Each point = cash balances as of that date + " +
        "split-adjusted holdings market value, Overview-consistent (investment accounts " +
        "include positions; a security is priced at the last market close on/before the " +
        "date, else its last trade price). unpricedSecurityCount flags points where a held " +
        "security had NO price at all at that date (valued at 0, so net worth is " +
        "understated by those). Prefer a wider interval over long ranges. USD.")]
    public static async Task<NetWorthHistory> NetWorthHistory(
        AccountsReportingRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Window start, UTC ISO-8601 (e.g. 2020-01-01T00:00:00Z).")] DateTime fromUtc,
        [Description("Window end, UTC ISO-8601.")] DateTime toUtc,
        [Description("Interval between points: 'month' (default), 'quarter', or 'year'.")]
        string interval = "month",
        CancellationToken cancellationToken = default) =>
        await repository.NetWorthHistoryAsync(
            ledgerId, fromUtc, toUtc,
            McpArgs.ParseEnum<ReportTimeBucket>(interval, "interval"),
            cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "account_portfolio"), Description(
        "Portfolio for one investment (brokerage) account: cash balance, each " +
        "position (security, quantity, cost basis, latest price, market value, " +
        "unrealized gain), and a summary (portfolio value, cost, unrealized gain %, " +
        "cash, total = cash + positions). This is the 'investment balance by account' " +
        "view. Resolve accountId via list_accounts (type 'investment'). USD.")]
    public static async Task<HoldingsViewDto> AccountPortfolio(
        HoldingsRepository repository,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Investment account id (GUID) from list_accounts.")] Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var result = await repository
            .GetByBrokerageAsync(ledgerId, accountId, cancellationToken)
            .ConfigureAwait(false);
        return result.Kind switch
        {
            HoldingsRepository.ResultKind.Ok => result.View!,
            HoldingsRepository.ResultKind.AccountNotInLedger =>
                throw new ArgumentException("No such account in this ledger. Use list_accounts to find one."),
            HoldingsRepository.ResultKind.NotAnInvestmentAccount =>
                throw new ArgumentException("That account is not an investment account. Filter list_accounts by type 'investment'."),
            HoldingsRepository.ResultKind.NoHoldingsSibling =>
                throw new ArgumentException("That investment account has no holdings recorded."),
            _ => throw new InvalidOperationException("Unexpected portfolio result."),
        };
    }
}
