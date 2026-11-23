using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Each recompute interceptor treats a modified header as touching every leg it has
/// when a property in its <c>RecomputeHeaderProperties</c> changed, so that set must be
/// exactly the header columns its recompute SQL reads that can change after insert.
/// Fail closed: a header column the SQL names is either in the set or fixed for the
/// life of the row, and nothing in the set goes unread.
/// </summary>
/// <remarks>
/// <para>The SQL is read as <see cref="LegRecomputePropertyCatalogTests"/> reads it: the
/// functions the projection's service calls, every function and view they reach, each
/// at its latest definition, scanning the bodies that name <c>txn_headers</c> or
/// <c>live_txn_headers</c>. <c>live_txn_headers</c> is the header table without its
/// templates, so only its filter is read.</para>
/// <para>Fixed for the life of the row means an init-only property: C# can set it only
/// when the row is created, and a new row's legs are added with it, so the leg
/// interceptors see it. A property mapped without a setter of its own counts as one
/// that can change.</para>
/// <para>The match is by name: a scanned body that names a column anywhere, as whatever
/// table's column, reads it. Not covered: SQL run through dynamic statements
/// (<c>EXECUTE</c>).</para>
/// </remarks>
public sealed class HeaderRecomputePropertyCatalogTests
{
    private static readonly IReadOnlySet<string> HeaderTables =
        new HashSet<string>(StringComparer.Ordinal) { "txn_headers", "live_txn_headers" };

    private static readonly IReadOnlySet<string> HeaderAliasViews =
        new HashSet<string>(StringComparer.Ordinal) { "live_txn_headers" };

    /// <summary>Each projection maintained on a header change: the service whose calls
    /// are its recompute, and the set its interceptor watches.</summary>
    private static readonly Dictionary<string, (string ServicePath, IReadOnlySet<string> Watched)> Projections =
        new(StringComparer.Ordinal)
        {
            ["balances and posting counts"] = (
                "src/Api/Db/Repositories/LegDerivedRecomputeService.cs",
                LegDerivedRecomputeInterceptor.RecomputeHeaderProperties),
            ["holdings, lots and realized gains"] = (
                "src/Api/Db/Repositories/HoldingsRecomputeService.cs",
                HoldingsRecomputeInterceptor.RecomputeHeaderProperties),
            ["trade prices"] = (
                "src/Api/Db/Repositories/TradePriceRecomputeService.cs",
                TradePriceFromLegInterceptor.RecomputeHeaderProperties),
        };

    public static TheoryData<string> ProjectionNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Projections.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ProjectionNames))]
    public void A_header_change_recomputes_exactly_what_the_recompute_reads(string projection)
    {
        var (servicePath, watched) = Projections[projection];
        var root = StoredNotesWriteCatalogTests.RepoRoot();
        var migrations = LegRecomputePropertyCatalogTests.Migrations(root);
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only").Options);
        var headers = context.Model.FindEntityType(typeof(TxnHeaderRow))!;
        var table = StoreObjectIdentifier.Table(headers.GetTableName()!, headers.GetSchema());
        var columns = headers.GetProperties()
            .ToDictionary(p => p.Name, p => p.GetColumnName(table)!.ToLowerInvariant(), StringComparer.Ordinal);
        Assert.True(columns.Count > 20, $"found only {columns.Count} mapped header properties; the model is not being read");
        var fixedForLife = headers.GetProperties().Where(p => IsInitOnly(p.PropertyInfo))
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        // Fail closed both ways: a reflection test that found nothing fixed would demand
        // every column, and one that found everything fixed would demand none.
        Assert.Contains(nameof(TxnHeaderRow.Id), fixedForLife);
        Assert.DoesNotContain(nameof(TxnHeaderRow.PostedAt), fixedForLife);

        var roots = LegRecomputePropertyCatalogTests.Roots(root, context.Model, servicePath);
        Assert.True(roots.Count >= 1, $"{projection}: no recompute function found; the scan is not matching the service's calls");
        var (scanned, read) = LegRecomputePropertyCatalogTests.ReadIdentifiers(migrations, roots, HeaderTables, HeaderAliasViews);
        Assert.True(scanned.Count >= 1, $"{projection}: scanned only [{string.Join(", ", scanned)}]; no recompute body names a header table");

        var changeable = columns
            .Where(c => read.Contains(c.Value) && !fixedForLife.Contains(c.Key))
            .Select(c => c.Key)
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        violations.AddRange(changeable.Where(p => !watched.Contains(p))
            .Select(p => $"{p} ({columns[p]}): the {projection} SQL ([{string.Join(", ", scanned)}]) names it and it can "
                         + "change after insert, but its interceptor does not watch it; add it to RecomputeHeaderProperties"));
        violations.AddRange(watched.Where(p => !columns.ContainsKey(p))
            .Select(p => $"{p}: watched but not a mapped header property; remove it"));
        violations.AddRange(watched.Where(p => columns.ContainsKey(p) && !changeable.Contains(p))
            .Select(p => $"{p} ({columns[p]}): watched, but the {projection} SQL does not read it or it is fixed for the life of "
                         + "the row; remove it"));
        Assert.True(violations.Count == 0, string.Join("\n", violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void An_alias_view_is_read_for_its_filter_and_not_its_columns()
    {
        // The root reads a header column through the alias; the alias lists another and
        // filters on a third. The listed column stays out, the filter's comes in.
        var migrations = new List<(string Name, string Text)>
        {
            ("001.sql", """
                CREATE FUNCTION fn_root(p UUID) RETURNS void LANGUAGE sql AS $$ SELECT h.read_col FROM live_txn_headers h; $$;
                CREATE VIEW live_txn_headers AS SELECT listed_col, read_col FROM txn_headers WHERE NOT filter_col;
                """),
        };

        var (scanned, read) = LegRecomputePropertyCatalogTests.ReadIdentifiers(
            migrations, ["fn_root"], HeaderTables, HeaderAliasViews);

        Assert.Equal(["function fn_root", "view live_txn_headers (filter)"], scanned.Order(StringComparer.Ordinal));
        Assert.Contains("read_col", read);
        Assert.Contains("filter_col", read);
        Assert.DoesNotContain("listed_col", read);
    }

    /// <summary>
    /// A header removed through the change tracker takes the rows merged into it with it
    /// into the tracker: every <c>TxnHeaders.Remove</c> and <c>RemoveRange</c> in
    /// <c>src/Api</c> is preceded in its method by <c>MergeLosers.TrackAsync</c>.
    /// </summary>
    /// <remarks>
    /// The database un-merges a deleted winner's losers (<c>ON DELETE SET NULL</c>), past
    /// the tracker; tracked first, EF un-merges them too, as a change to
    /// <c>is_merged_into</c> the header rule above counts. A removal without it leaves
    /// each loser's accounts and positions as they were while it was merged. Not covered:
    /// a removal through <c>Set&lt;TxnHeaderRow&gt;()</c> or an entry's state, which no
    /// writer uses; a set-based delete reads the losers itself.
    /// </remarks>
    [Fact]
    public void Every_tracked_header_removal_tracks_the_rows_merged_into_it_first()
    {
        var root = StoredNotesWriteCatalogTests.RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 300, $"scanned only {files.Count} files; the guard is not looking at the API");

        var removal = new Regex(@"\b_?db\.TxnHeaders\.Remove(?:Range)?\(");
        var methodHeader = new Regex(@"(?:public|private|internal|protected)[^;{}=]*?\s(\w+)\s*(?:<[^>]*>)?\s*\(");
        var removals = 0;
        var violations = new List<string>();
        foreach (var file in files)
        {
            var code = StoredNotesWriteCatalogTests.CodeOnly(File.ReadAllText(file));
            foreach (Match m in removal.Matches(code))
            {
                removals++;
                var header = methodHeader.Matches(code[..m.Index]).LastOrDefault();
                var before = code[(header?.Index ?? 0)..m.Index];
                if (!before.Contains("MergeLosers.TrackAsync(", StringComparison.Ordinal))
                    violations.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{header?.Groups[1].Value ?? "(none)"} "
                                   + "removes a header without MergeLosers.TrackAsync before it");
            }
        }
        Assert.True(removals >= 3, $"found only {removals} tracked header removals; the scan is not matching them");
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static bool IsInitOnly(PropertyInfo? property) =>
        property?.SetMethod is { } set
        && set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
}
