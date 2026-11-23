namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// The type-<c>s</c> security accounts: one per (investment account, security)
/// pair, each hanging under its investment account and carrying Moneydance's
/// <c>sec_type</c>, <c>sec_subtype</c> and <c>sec_exchange</c>, which is where
/// Moneydance writes them and where the importer reads them.
/// </summary>
/// <remarks>
/// A holding's role is its account's role and the security's key, joined by a
/// slash; its id is hashed from that role like every account's, so adding a pair
/// moves no other id. A holding opens and closes with its account.
/// </remarks>
public static class DemoHoldings
{
    /// <summary>Every (account role, security key) pair, in export order.</summary>
    public static IReadOnlyList<(string AccountRole, string SecurityKey)> Pairs { get; } =
    [
        (DemoAccountRoles.Brokerage, DemoSecurities.Vti),
        (DemoAccountRoles.Brokerage, DemoSecurities.Vxus),
        (DemoAccountRoles.Brokerage, DemoSecurities.Bnd),
        (DemoAccountRoles.Brokerage, DemoSecurities.Vnq),
        (DemoAccountRoles.Brokerage, DemoSecurities.MoneyMarket),
        (DemoAccountRoles.Brokerage, DemoSecurities.Nvda),
        (DemoAccountRoles.Brokerage, DemoSecurities.Msft),
        (DemoAccountRoles.RolloverIra, DemoSecurities.Vti),
        (DemoAccountRoles.RolloverIra, DemoSecurities.Vxus),
        (DemoAccountRoles.RolloverIra, DemoSecurities.Bnd),
        (DemoAccountRoles.FamilyHsa, DemoSecurities.Vti),
        (DemoAccountRoles.OldPlanA, DemoSecurities.TargetDate),
        (DemoAccountRoles.NewPlanA, DemoSecurities.TargetDate),
        (DemoAccountRoles.PlanB, DemoSecurities.TargetDate),
        (DemoAccountRoles.PlanB, DemoSecurities.StableValue),
    ];

    /// <summary>The holding account's role for an account and a security.</summary>
    public static string Role(string accountRole, string securityKey) => accountRole + "/" + securityKey;

    /// <summary>The holding account of <paramref name="securityKey"/> under the
    /// account with <paramref name="accountRole"/>.</summary>
    public static DemoAccount Holding(DemoBook book, string accountRole, string securityKey)
    {
        ArgumentNullException.ThrowIfNull(book);
        return book.Account(Role(accountRole, securityKey));
    }

    /// <summary>The securities held under the account with
    /// <paramref name="accountRole"/>, in export order.</summary>
    public static IReadOnlyList<string> SecuritiesOf(string accountRole) =>
        Pairs.Where(p => p.AccountRole == accountRole).Select(p => p.SecurityKey).ToList();

    /// <summary>Add every holding account to the book, after the real accounts.</summary>
    public static void AddTo(DemoBook book, DemoMarket market)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(market);

        foreach (var (accountRole, securityKey) in Pairs)
        {
            var parent = book.Account(accountRole);
            var security = DemoSecurities.Get(securityKey);
            var role = Role(accountRole, securityKey);
            var holding = new DemoAccount
            {
                Role = role,
                Id = book.Ids.Id("acct", role, 0),
                Name = security.Name,
                MdType = "s",
                DateCreated = parent.DateCreated,
                ActiveFrom = parent.ActiveFrom,
                ActiveThrough = parent.ActiveThrough,
                IsInactive = parent.IsInactive,
                OpeningBalance = 0L,
                ParentId = parent.Id,
                CurrencyId = market.CurrencyId(securityKey),
            };
            holding.ExtraFields.Add(new("sec_type", security.SecType));
            if (security.SecSubtype is { } subtype) holding.ExtraFields.Add(new("sec_subtype", subtype));
            if (security.Exchange is { } exchange) holding.ExtraFields.Add(new("sec_exchange", exchange));
            book.AddAccount(holding);
        }
    }
}
