using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The leg-derived recompute interceptor skips a modified leg whose changes all fall in
/// <see cref="LegDerivedRecomputeInterceptor.NonRecomputeLegProperties"/>, so that set must
/// hold only properties no recompute reads. Fail closed: every mapped
/// <see cref="TxnLegRow"/> property is classified, and no property in the skipped set has
/// its column named by the recompute SQL.
/// </summary>
/// <remarks>
/// <para>The recompute SQL is taken from the migrations, not from a list: the functions
/// <c>LegDerivedRecomputeService</c> calls (through their <c>AppDbContext</c> mappings),
/// every function and view they reach, each at its latest definition. Only a body that
/// names <c>txn_legs</c> can read a leg column, so only those are scanned.</para>
/// <para>The match is by name: a scanned body that names a column anywhere, as whatever
/// table's column, reads it. A property whose column collides with another table's column
/// is therefore classed as read.</para>
/// <para>Not covered: SQL run through dynamic statements (<c>EXECUTE</c>).</para>
/// </remarks>
public sealed partial class LegRecomputePropertyCatalogTests
{
    private const string ServicePath = "src/Api/Db/Repositories/LegDerivedRecomputeService.cs";

    [GeneratedRegex(@"\b_db\.(\w+)\s*\(")]
    private static partial Regex DbCall();

    [GeneratedRegex(@"\b(\w+)\s*\(")]
    private static partial Regex Call();

    [GeneratedRegex(@"\b[a-z_][a-z0-9_]*\b", RegexOptions.IgnoreCase)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:public\.)?(\w+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateFunction();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?(\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateView();

    [Fact]
    public void Every_leg_property_is_classified_and_no_skipped_one_is_read_by_a_recompute()
    {
        var root = StoredNotesWriteCatalogTests.RepoRoot();
        var migrations = Migrations(root);
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only").Options);
        var legs = context.Model.FindEntityType(typeof(TxnLegRow))!;
        var table = StoreObjectIdentifier.Table(legs.GetTableName()!, legs.GetSchema());
        var columns = legs.GetProperties()
            .ToDictionary(p => p.Name, p => p.GetColumnName(table)!.ToLowerInvariant(), StringComparer.Ordinal);
        Assert.True(columns.Count > 20, $"found only {columns.Count} mapped leg properties; the model is not being read");

        var roots = Roots(root, context.Model, ServicePath);
        // Fail closed: a scan that matched no call would read no SQL and pass.
        Assert.True(roots.Count >= 2, $"found only [{string.Join(", ", roots)}] as recompute functions; the scan is not matching the service's calls");

        var (scanned, read) = ReadIdentifiers(migrations, roots, LegTables, NoAliasViews);
        Assert.True(scanned.Count >= 2, $"scanned only [{string.Join(", ", scanned)}]; no recompute body names txn_legs");

        var skipped = LegDerivedRecomputeInterceptor.NonRecomputeLegProperties;
        var counted = LegDerivedRecomputeInterceptor.RecomputeLegProperties;
        var violations = new List<string>();
        violations.AddRange(columns.Keys.Where(p => !skipped.Contains(p) && !counted.Contains(p))
            .Select(p => $"{p} ({columns[p]}): a mapped leg property in neither set; classify it"));
        violations.AddRange(skipped.Intersect(counted)
            .Select(p => $"{p}: in both sets; it belongs in one"));
        violations.AddRange(skipped.Concat(counted).Where(p => !columns.ContainsKey(p)).Distinct()
            .Select(p => $"{p}: classified but not a mapped leg property; remove the entry"));
        violations.AddRange(skipped.Where(p => columns.TryGetValue(p, out var c) && read.Contains(c))
            .Select(p => $"{p} ({columns[p]}): skipped by the recompute interceptor, but the recompute SQL "
                         + $"([{string.Join(", ", scanned)}]) names its column; move it to the recomputed set"));
        violations.AddRange(counted.Where(p => columns.TryGetValue(p, out var c) && !read.Contains(c))
            .Select(p => $"{p} ({columns[p]}): classed as read, but no recompute names its column; move it to the skipped set"));
        Assert.True(violations.Count == 0, string.Join("\n", violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_scan_follows_calls_and_views_and_reads_only_bodies_that_name_txn_legs()
    {
        // Synthetic migrations: the root reaches a helper through a call and a view through
        // its FROM; the helper reads a leg column, the view names txn_legs and another, and
        // a later script redefines the helper, whose latest body is the one read. The
        // unreached function's column and the root's own (it names no txn_legs) stay out.
        var migrations = new List<(string Name, string Text)>
        {
            ("001.sql", """
                CREATE FUNCTION fn_root(p UUID) RETURNS void LANGUAGE sql AS $$ SELECT fn_helper(p), x.y FROM v_legs x WHERE x.not_a_leg_col = 1; $$;
                CREATE FUNCTION fn_helper(p UUID) RETURNS void LANGUAGE sql AS $$ SELECT l.stale_col FROM txn_legs l; $$;
                CREATE FUNCTION fn_unreached() RETURNS void LANGUAGE sql AS $$ SELECT l.unreached_col FROM txn_legs l; $$;
                CREATE VIEW v_legs AS SELECT l.view_col AS y FROM txn_legs l;
                """),
            ("002.sql", """
                CREATE OR REPLACE FUNCTION public.fn_helper(p UUID) RETURNS void LANGUAGE sql AS $$
                    -- a comment naming comment_col
                    SELECT l.helper_col FROM txn_legs l;
                $$;
                """),
        };

        var (scanned, read) = ReadIdentifiers(migrations, ["fn_root"], LegTables, NoAliasViews);

        Assert.Equal(["function fn_helper", "view v_legs"], scanned.Order(StringComparer.Ordinal));
        Assert.Contains("helper_col", read);
        Assert.Contains("view_col", read);
        Assert.DoesNotContain("stale_col", read);
        Assert.DoesNotContain("unreached_col", read);
        Assert.DoesNotContain("not_a_leg_col", read);
        Assert.DoesNotContain("comment_col", read);
    }

    private static readonly IReadOnlySet<string> LegTables = new HashSet<string>(StringComparer.Ordinal) { "txn_legs" };
    private static readonly IReadOnlySet<string> NoAliasViews = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The SQL names of the mapped functions the service at
    /// <paramref name="servicePath"/> calls.</summary>
    internal static List<string> Roots(string root, IModel model, string servicePath)
    {
        var service = File.ReadAllText(Path.Combine(root, servicePath));
        var called = DbCall().Matches(service).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return model.GetDbFunctions()
            .Where(f => f.MethodInfo is not null && called.Contains(f.MethodInfo.Name))
            .Select(f => f.Name.ToLowerInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Every function and view reachable from <paramref name="roots"/> at its latest
    /// definition, and the identifiers named by the ones whose body names one of
    /// <paramref name="tables"/>.
    /// </summary>
    /// <remarks>
    /// An alias view (<paramref name="aliasViews"/>) is a pass-through of its table: a
    /// body naming it names the table, so it belongs in <paramref name="tables"/>
    /// too. Its own select list lists every column and reads none, so only its
    /// <c>WHERE</c> clause is read, as a filter every reader of it applies.
    /// </remarks>
    internal static (List<string> Scanned, HashSet<string> Read) ReadIdentifiers(
        List<(string Name, string Text)> migrations, IReadOnlyCollection<string> roots,
        IReadOnlySet<string> tables, IReadOnlySet<string> aliasViews)
    {
        // Names from the code only: a comment that says "CREATE VIEW is ..." defines nothing.
        var code = migrations.Select(m => WithoutComments(m.Text)).ToList();
        var functions = code.SelectMany(t => CreateFunction().Matches(t))
            .Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var views = code.SelectMany(t => CreateView().Matches(t))
            .Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var scanned = new List<string>();
        var read = new HashSet<string>(StringComparer.Ordinal);
        var reached = new HashSet<string>(roots.Select(r => $"function {r}"), StringComparer.Ordinal);
        var pending = new Queue<string>(reached);
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            var name = next[(next.IndexOf(' ') + 1)..];
            var body = next.StartsWith("function ", StringComparison.Ordinal)
                ? StoredNotesWriteCatalogTests.LatestDefinition(migrations, name)
                : LatestView(migrations, name);
            Assert.True(body is not null, $"{next}: no definition found in db/migrations");

            if (next.StartsWith("view ", StringComparison.Ordinal) && aliasViews.Contains(name))
            {
                var filter = Regex.Match(body!, @"\bWHERE\b([\s\S]*)$", RegexOptions.IgnoreCase);
                scanned.Add(next + " (filter)");
                if (filter.Success)
                    read.UnionWith(Identifier().Matches(filter.Groups[1].Value).Select(m => m.Value.ToLowerInvariant()));
                continue;
            }

            var identifiers = Identifier().Matches(body!).Select(m => m.Value.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
            if (identifiers.Overlaps(tables))
            {
                scanned.Add(next);
                read.UnionWith(identifiers);
            }
            foreach (Match call in Call().Matches(body!))
            {
                var callee = call.Groups[1].Value.ToLowerInvariant();
                if (functions.Contains(callee) && reached.Add($"function {callee}")) pending.Enqueue($"function {callee}");
            }
            foreach (var view in identifiers.Where(views.Contains))
                if (reached.Add($"view {view}")) pending.Enqueue($"view {view}");
        }
        return (scanned, read);
    }

    /// <summary>The latest definition of the view <paramref name="name"/>, to its statement end, comments removed.</summary>
    private static string? LatestView(List<(string Name, string Text)> migrations, string name)
    {
        var create = new Regex($@"CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?{name}\b", RegexOptions.IgnoreCase);
        for (var i = migrations.Count - 1; i >= 0; i--)
        {
            var text = WithoutComments(migrations[i].Text);
            var m = create.Matches(text).LastOrDefault();
            if (m is null) continue;
            var end = text.IndexOf(';', m.Index);
            return end < 0 ? text[m.Index..] : text[m.Index..end];
        }
        return null;
    }

    private static string WithoutComments(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"/\*[\s\S]*?\*/", " "), @"--[^\n]*", " ");

    internal static List<(string Name, string Text)> Migrations(string root) =>
        Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToList();
}
