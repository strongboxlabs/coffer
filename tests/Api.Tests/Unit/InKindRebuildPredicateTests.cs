using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The pre-upgrade check and migration 263 agree on which in-kind transfers are rebuilt
/// as zero-gain sales (ADR-0108 D18): both read the shape through the same predicate text.
/// </summary>
/// <remarks>
/// The check counts the transfers with no source brokerage that the rebuild cannot take,
/// and predicts the gain of the ones it takes. A predicate that drifts in either copy would
/// make it count against a different set from the one the upgrade rebuilds. The two are
/// compared from the first leg join to the brokerage count, white space collapsed.
/// </remarks>
public sealed class InKindRebuildPredicateTests
{
    private const string From = "JOIN txn_legs hl ON hl.header_id = u.header_id AND hl.quantity < 0";
    private const string To = "WHERE y.holdings_account_id = hl.account_id AND y.ledger_id = u.ledger_id) = 1";

    [Fact]
    public void The_check_reads_the_rebuild_shape_with_the_migrations_predicate()
    {
        var repo = RepoRoot();
        var migration = Predicate(File.ReadAllText(Path.Combine(
            repo, "db", "migrations", "263_originating_account_tag_name_key_and_saved_report_repoint.sql")));
        var check = Predicate(File.ReadAllText(Path.Combine(
            repo, "scripts", "maintainer", "release-checks", "0128-precheck.sql")));

        Assert.Contains("tl.amount = -hl.amount", migration, StringComparison.Ordinal);
        Assert.Equal(migration, check);
    }

    /// <summary>The one predicate in <paramref name="sql"/>, white space collapsed.</summary>
    private static string Predicate(string sql)
    {
        var start = sql.IndexOf(From, StringComparison.Ordinal);
        Assert.True(start >= 0, "the rebuild predicate's first leg join is missing");
        Assert.Equal(-1, sql.IndexOf(From, start + From.Length, StringComparison.Ordinal));
        var end = sql.IndexOf(To, start, StringComparison.Ordinal);
        Assert.True(end > start, "the rebuild predicate's brokerage count is missing");
        return Regex.Replace(sql[start..(end + To.Length)], @"\s+", " ");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
