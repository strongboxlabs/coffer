using Coffer.Importer.Moneydance.Pipeline;

namespace Coffer.Importer.Moneydance.Tests.Pipeline;

/// <summary>
/// The sentence the import wizard shows for each failed check. The person importing
/// does not know the importer's terms, so the sentence uses none of them, and its
/// counts agree in number with their nouns.
/// </summary>
public sealed class CheckSummariesTests
{
    [Fact]
    public void Each_failed_check_reads_in_plain_words_singular_and_plural()
    {
        Assert.Equal(
            "1 account name is used by more than one account: Name A. Rename the accounts that share "
            + "a name so you can tell them apart.",
            CheckSummaries.DuplicateAccountNames(["Name A"], more: false));
        Assert.Equal(
            "2 account names are used by more than one account: Name A, Name B. Rename the accounts that "
            + "share a name so you can tell them apart.",
            CheckSummaries.DuplicateAccountNames(["Name A", "Name B"], more: false));
        Assert.Equal(
            "At least 2 account names are used by more than one account: Name A, Name B. Rename the "
            + "accounts that share a name so you can tell them apart.",
            CheckSummaries.DuplicateAccountNames(["Name A", "Name B"], more: true));

        Assert.Equal(
            "Some investment transactions were stored with an action Coffer does not use: act_a. They may "
            + "not show correctly in their register.",
            CheckSummaries.UnusableActions(["act_a"], more: false));
        Assert.Equal(
            "Some investment transactions were stored with actions Coffer does not use: act_a, act_b, and "
            + "others. They may not show correctly in their register.",
            CheckSummaries.UnusableActions(["act_a", "act_b"], more: true));

        Assert.Equal(
            "1 transaction has lines that do not add up to zero, so the balances of the accounts it "
            + "touches may be off.",
            CheckSummaries.Unbalanced(1));
        Assert.Equal(
            "1,200 transactions have lines that do not add up to zero, so the balances of the accounts "
            + "they touch may be off.",
            CheckSummaries.Unbalanced(1200));

        Assert.Equal("1 transaction was imported with no lines, so it has no amount.",
            CheckSummaries.WithoutLines(1));
        Assert.Equal("3 transactions were imported with no lines, so they have no amount.",
            CheckSummaries.WithoutLines(3));

        Assert.Equal(
            "1 transaction is not owned by an account that can hold it, so it may show in the wrong register.",
            CheckSummaries.WithoutOwner(1));
        Assert.Equal(
            "3 transactions are not owned by an account that can hold them, so they may show in the wrong "
            + "register.",
            CheckSummaries.WithoutOwner(3));

        Assert.Equal("1 tax lot points at a transaction line that does not exist, so its cost basis may be wrong.",
            CheckSummaries.LotsWithoutLine(1));
        Assert.Equal("3 tax lots point at a transaction line that does not exist, so their cost basis may be wrong.",
            CheckSummaries.LotsWithoutLine(3));

        Assert.Equal("1 account of the 3 in the export is not in the ledger.", CheckSummaries.AccountCount(3, 2));
        Assert.Equal("2 accounts of the 3 in the export are not in the ledger.", CheckSummaries.AccountCount(3, 1));
        Assert.Equal("The ledger has 4 accounts from the export, which has 3.", CheckSummaries.AccountCount(3, 4));

        Assert.Equal("1 transaction was not imported.", CheckSummaries.NotImported(1));
        Assert.Equal("3 transactions were not imported.", CheckSummaries.NotImported(3));
    }

    /// <summary>The importer's own terms, which mean nothing to the person importing.</summary>
    private static readonly string[] ImporterTerms =
        ["header", "leg", "MD ", "external_id", "junction", "migration", "invariant", "(s)", "×", "DB "];

    [Fact]
    public void No_sentence_uses_the_importers_terms()
    {
        string[] names = ["Name A", "Name B"];
        var sentences = new[] { 1, 2 }.SelectMany(n => new[]
        {
            CheckSummaries.DuplicateAccountNames(names[..n], more: false),
            CheckSummaries.DuplicateAccountNames(names[..n], more: true),
            CheckSummaries.UnusableActions(names[..n], more: false),
            CheckSummaries.UnusableActions(names[..n], more: true),
            CheckSummaries.Unbalanced(n),
            CheckSummaries.WithoutLines(n),
            CheckSummaries.WithoutOwner(n),
            CheckSummaries.LotsWithoutLine(n),
            CheckSummaries.AccountCount(3, n),
            CheckSummaries.AccountCount(n, 3),
            CheckSummaries.NotImported(n),
        });

        foreach (var sentence in sentences)
            foreach (var term in ImporterTerms)
                Assert.DoesNotContain(term, sentence, StringComparison.OrdinalIgnoreCase);
    }
}
