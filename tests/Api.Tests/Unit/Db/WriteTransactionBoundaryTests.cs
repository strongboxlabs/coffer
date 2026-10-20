using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit.Db;

/// <summary>
/// A write path's transaction has to open before its first read, and there has
/// to be exactly one of them.
/// </summary>
/// <remarks>
/// <para>This is asserted against the SOURCE because the defect it pins has no
/// single-threaded symptom. The bank <c>PatchAsync</c> opens its transaction as
/// its first statement and says why: the ledger-membership guard must run inside
/// the transaction that later writes, or there is a TOCTOU window between the
/// endpoint's cross-ledger check and the writes. The investment
/// <c>PatchAsync</c> used to open TWO transactions further down — one inside the
/// merge branch, one after validation — leaving the membership guard, the merge
/// branch's eligibility checks and the whole action × field matrix validation
/// running unprotected.</para>
///
/// <para>Every integration test passed before and after the fix, because a race
/// needs two writers and these run one. A test that can only be written as a
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
    [Theory]
    [InlineData("src/Api/Db/Repositories/TransactionsRepository.cs",
                "PatchTransactionRequest request")]
    [InlineData("src/Api/Db/Repositories/InvestmentTransactionsRepository.cs",
                "PatchInvestmentTransactionRequest request")]
    public void A_patch_opens_exactly_one_transaction_before_its_first_read(
        string relativePath, string requestParameter)
    {
        var body = MethodBody(relativePath, requestParameter);

        var opens = Regex.Matches(body, @"BeginTransactionAsync\(").Count;
        Assert.True(opens == 1,
            $"{relativePath}: expected exactly one BeginTransactionAsync in this "
            + $"PATCH, found {opens}. A second boundary means part of the method "
            + "commits — or rolls back — independently of the rest.");

        var open = body.IndexOf("BeginTransactionAsync(", StringComparison.Ordinal);
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
    /// The text of the method whose signature contains
    /// <paramref name="requestParameter"/>, from its signature to the closing
    /// brace at the same indentation. Crude on purpose: a real parse would need
    /// Roslyn as a test dependency to answer a question about four characters of
    /// whitespace.
    /// </summary>
    private static string MethodBody(string relativePath, string requestParameter)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        var at = source.IndexOf(requestParameter, StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"{relativePath}: no method takes '{requestParameter}'. If it was renamed, "
            + "point this test at the new name rather than deleting it.");

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
