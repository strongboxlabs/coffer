using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Db;

/// <summary>
/// A write path's transaction has to open before its first read, and there has
/// to be exactly one of them.
/// </summary>
/// <remarks>
/// <para>This is asserted against the SOURCE because the defect it pins has no
/// single-threaded symptom. The bank PATCH opens its transaction as its first
/// statement and says why: the ledger-membership guard must run inside the
/// transaction that later writes, or there is a TOCTOU window between the
/// endpoint's cross-ledger check and the writes. The investment
/// <c>PatchAsync</c> must do the same: opening TWO transactions further down —
/// one inside the merge branch, one after validation — would leave the membership
/// guard, the merge branch's eligibility checks and the whole action × field
/// matrix validation running unprotected.</para>
///
/// <para>A boundary is <c>BeginTransactionAsync</c>, or <c>BeginLedgerWriteAsync</c>,
/// which opens the transaction with the ledger write lock as its first statement
/// (<c>LedgerWriteLock</c>, migration 275) and is how every write that can recompute
/// opens one. Either counts, and the two together must still count one.</para>
///
/// <para>Integration tests cannot see the difference, because a race needs two
/// writers and they run one. A test that can only be written as a
/// concurrency harness is a test that gets skipped, so the invariant is pinned
/// structurally instead: it is cheap, it is deterministic, and it fails on the
/// edit that would reintroduce the gap rather than on a Tuesday.</para>
///
/// <para>Deliberately narrow. It checks ordering and count in two named
/// methods, not a repo-wide rule — other write paths legitimately open a
/// transaction late, or conditionally (<c>CreateAsync</c> nests under a
/// caller's), and a broad rule here would be noise that gets suppressed.</para>
/// </remarks>
public sealed class WriteTransactionBoundaryTests
{
    private static readonly Regex Boundary =
        new(@"\b(BeginTransactionAsync|BeginLedgerWriteAsync)\(", RegexOptions.Compiled);

    [Theory]
    [InlineData("src/Api/Db/Repositories/TransactionsRepository.cs",
                "private async Task<PatchOutcome> PatchCoreAsync(")]
    [InlineData("src/Api/Db/Repositories/InvestmentTransactionsRepository.cs",
                "PatchInvestmentTransactionRequest request")]
    public void A_patch_opens_exactly_one_transaction_before_its_first_read(
        string relativePath, string signature)
    {
        var body = MethodBody(relativePath, signature);

        var opens = Boundary.Matches(body);
        Assert.True(opens.Count == 1,
            $"{relativePath}: expected exactly one transaction boundary (BeginTransactionAsync "
            + $"or BeginLedgerWriteAsync) in this PATCH, found {opens.Count}. A second boundary "
            + "means part of the method commits — or rolls back — independently of the rest.");

        var open = opens[0].Index;
        // The membership guard: the first thing the method reads from the DB.
        var firstRead = body.IndexOf("FirstOrDefaultAsync(", StringComparison.Ordinal);
        Assert.True(firstRead >= 0, $"{relativePath}: no read found to order against.");
        Assert.True(open < firstRead,
            $"{relativePath}: the transaction opens at offset {open}, AFTER the first "
            + $"read at {firstRead}. The ledger-membership guard has to run inside the "
            + "transaction that writes, or a concurrent commit can move the header out "
            + "of the ledger between the check and the write.");
    }

    /// <summary>
    /// The text of the method whose signature contains <paramref name="signature"/>,
    /// from there to the closing brace at the same indentation. Crude on purpose: a
    /// real parse would need Roslyn as a test dependency to answer a question about
    /// four characters of whitespace.
    /// </summary>
    private static string MethodBody(string relativePath, string signature)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"{relativePath}: no method's signature contains '{signature}'. If it was renamed, "
            + "point this test at the new name rather than deleting it.");
        Assert.True(source.IndexOf(signature, at + 1, StringComparison.Ordinal) < 0,
            $"{relativePath}: more than one method's signature contains '{signature}'; name one.");

        // "\n    }" — the closing brace of a method on a top-level type.
        var end = source.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.True(end > at, $"{relativePath}: could not find the method's end.");
        return source[at..end];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find the repository root walking up from {AppContext.BaseDirectory}.");
    }
}
