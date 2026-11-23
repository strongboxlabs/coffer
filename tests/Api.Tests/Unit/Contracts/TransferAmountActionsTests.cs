using System.Text.RegularExpressions;

using Coffer.Domain.Investment;

namespace Coffer.Api.Tests.Unit.Contracts;

/// <summary>
/// The actions that carry <c>transferAmount</c> across currencies (ADR-0114 D13), as the
/// SPA's <c>TRANSFER_AMOUNT_ACTIONS</c> lists them, are the wire values of the catalog's
/// transfer-shape actions, so an editor that gates the second amount on that list asks
/// for it on a plain transfer too. A name that is not on the wire, such as the register's
/// display word for a transfer, fails here.
/// </summary>
public sealed class TransferAmountActionsTests
{
    private static readonly string[] TransferShapeActions =
        [LedgerActions.Transfer, LedgerActions.BuyXfr, LedgerActions.SellXfr, LedgerActions.DivXfr];

    [Fact]
    public void The_web_list_is_exactly_the_transfer_shape_actions_by_their_wire_values()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Web", "src", "lib", "types", "investment.ts"));

        var listed = ArrayMembers(source, "TRANSFER_AMOUNT_ACTIONS");
        var wire = UnionMembers(source, "LedgerInvestmentAction");

        Assert.Equal(TransferShapeActions.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.All(listed, action => Assert.Contains(action, wire));
    }

    private static List<string> ArrayMembers(string source, string name)
    {
        var match = Regex.Match(source, $@"export const {Regex.Escape(name)}\b[^=]*=\s*\[([^\]]*)\]");
        Assert.True(match.Success, $"Could not find `export const {name} = [...]` in investment.ts.");
        return [.. Regex.Matches(match.Groups[1].Value, @"'([^']*)'").Select(m => m.Groups[1].Value)];
    }

    private static List<string> UnionMembers(string source, string name)
    {
        var match = Regex.Match(source, $@"export type {Regex.Escape(name)}\s*=([^;]+);");
        Assert.True(match.Success, $"Could not find `export type {name} = ...;` in investment.ts.");
        var members = Regex.Matches(match.Groups[1].Value, @"'([^']*)'").Select(m => m.Groups[1].Value).ToList();
        // Anchor: the union was read, not an empty set every list is a subset of.
        Assert.Contains(LedgerActions.TransferShares, members);
        return members;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
