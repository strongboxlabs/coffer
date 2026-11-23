using System.Text.Json;
using System.Text.RegularExpressions;

using Npgsql;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The plan of <c>account_balances_as_of_now</c> as the app reads it, and what in it
/// would let an account's lookup cost more than the account's own running balances.
/// </summary>
/// <remarks>
/// The statement is the one <c>AccountBalancesRepository</c> sends, planned on a
/// <c>coffer_app</c> connection with the ledger's user set, so row-level security, the
/// role and the planner's view of the parameters are the app's.
/// </remarks>
public static partial class CurrentBalancePlan
{
    /// <summary>The read, with the ledger and the boundary as parameters.</summary>
    public const string Sql =
        "SELECT b.account_id, b.balance FROM account_balances_as_of_now(@ledgerId, @scheduledFrom) AS b";

    /// <summary>The account's running balances: what the count and the fallback read.</summary>
    public const string AccountIndex = "idx_thab_account_ledger";

    /// <summary>The register's index: what the walk reads, never more of it than the account has rows.</summary>
    public const string RegisterIndex = "idx_txn_headers_ledger_posted_seq";

    /// <summary>The running balances' primary key: how the walk probes each header's running balance.</summary>
    public const string BalanceKey = "txn_header_account_balances_pkey";

    /// <summary>
    /// How the fallback may reach a header: a probe by the running balance's header id,
    /// through the primary key or the <c>(id, ledger_id)</c> key the composite foreign keys
    /// reference (migration 049). Both lead with the id, so a probe of either reads one
    /// header.
    /// </summary>
    public static readonly IReadOnlySet<string> HeaderKeys =
        new HashSet<string>(StringComparer.Ordinal) { "txn_headers_pkey", "uq_txn_headers_id_ledger" };

    private const string Balances = "txn_header_account_balances";
    private const string Headers = "txn_headers";
    private const string Accounts = "accounts";

    /// <summary>A coffer_app connection acting as <paramref name="userId"/>, open.</summary>
    public static async Task<NpgsqlConnection> OpenAsUserAsync(string appConnectionString, Guid userId)
    {
        var connection = new NpgsqlConnection(appConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, false)", connection);
        set.Parameters.AddWithValue("user", userId.ToString());
        await set.ExecuteNonQueryAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>The read as a command on <paramref name="connection"/>, prefixed by <paramref name="prefix"/>.</summary>
    public static NpgsqlCommand Command(NpgsqlConnection connection, Guid ledgerId, DateTime scheduledFrom, string prefix = "")
    {
        var command = new NpgsqlCommand(prefix + Sql, connection);
        command.Parameters.AddWithValue("ledgerId", ledgerId);
        command.Parameters.AddWithValue("scheduledFrom", scheduledFrom);
        return command;
    }

    /// <summary>The plan, without running the read.</summary>
    public static async Task<string> ExplainJsonAsync(NpgsqlConnection connection, Guid ledgerId, DateTime scheduledFrom)
    {
        await using var command = Command(connection, ledgerId, scheduledFrom, "EXPLAIN (FORMAT JSON) ");
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    /// <summary>
    /// What in <paramref name="planJson"/> reads the running balances or the headers other
    /// than the three bounded steps of migration 279; empty when every read is one of them.
    /// </summary>
    /// <remarks>
    /// <para>The only reads admitted:</para>
    /// <list type="bullet">
    /// <item>the count: <c>txn_header_account_balances</c> through <see cref="AccountIndex"/>
    /// on the account, under an aggregate;</item>
    /// <item>the walk: <c>txn_headers</c> through <see cref="RegisterIndex"/> on the
    /// account's ledger, directly under a Limit (the account's count), each header's running
    /// balance on the account probed through <see cref="BalanceKey"/>;</item>
    /// <item>the fallback: <c>txn_header_account_balances</c> through
    /// <see cref="AccountIndex"/> on the account, under a Limit, each row's header probed
    /// through one of <see cref="HeaderKeys"/> by the row's header id.</item>
    /// </list>
    /// <para>Fails closed: any other read of either table (a walk with no Limit of its own,
    /// a sequential or bitmap scan of the headers, a key scan without its condition) is
    /// reported, and so is each of the reads above when the plan has none of it, so a plan
    /// that changes shape fails rather than passing unread.</para>
    /// <para>A condition is matched by what its alias reads, not by the alias: the planner
    /// renames a relation it reads twice (<c>thab_1</c>, <c>h_1</c>), so every alias is
    /// resolved through the scans of the plan itself. Judged on node types, relation and
    /// index names and conditions only, never on a cost or a time.</para>
    /// </remarks>
    public static IReadOnlyList<string> BoundedLookupViolations(string planJson)
    {
        using var plan = JsonDocument.Parse(planJson);
        var root = plan.RootElement[0].GetProperty("Plan");

        var relationOf = new Dictionary<string, string>(StringComparer.Ordinal);
        Collect(root);

        var violations = new List<string>();
        int counts = 0, walks = 0, walkProbes = 0, fallbackReads = 0, headerProbes = 0;
        Walk(root, []);

        if (counts == 0)
            violations.Add($"no count of the account's running balances through {AccountIndex}");
        if (walks == 0)
            violations.Add($"no walk of {RegisterIndex} bounded by a Limit of its own");
        if (walkProbes == 0)
            violations.Add($"no probe of a walked header's running balance through {BalanceKey}");
        if (fallbackReads == 0)
            violations.Add($"no fallback read of the account's running balances through {AccountIndex}");
        if (headerProbes == 0)
            violations.Add($"no probe of txn_headers through {string.Join(" or ", HeaderKeys.Order(StringComparer.Ordinal))} by a running balance's header id");
        return violations;

        // Every alias the plan scans, to the relation it reads. A subquery's alias resolves to
        // the one relation its scans read, so a condition naming the subquery (w.id) is read
        // as a condition on what the subquery returns.
        void Collect(JsonElement node)
        {
            var relation = Text(node, "Relation Name");
            var alias = Text(node, "Alias");
            if (relation.Length > 0 && alias.Length > 0) relationOf[alias] = relation;
            if (Text(node, "Node Type") == "Subquery Scan" && alias.Length > 0
                && RelationsBelow(node).Distinct(StringComparer.Ordinal).ToList() is [var only])
                relationOf.TryAdd(alias, only);
            foreach (var child in Children(node)) Collect(child);
        }

        // The relations a node's own scans read: a SubPlan or InitPlan below it (row-level
        // security's grant lookups) is another query, not what the node returns.
        static IEnumerable<string> RelationsBelow(JsonElement node)
        {
            foreach (var child in Children(node))
            {
                if (Text(child, "Parent Relationship") is "SubPlan" or "InitPlan") continue;
                var relation = Text(child, "Relation Name");
                if (relation.Length > 0) yield return relation;
                else foreach (var below in RelationsBelow(child)) yield return below;
            }
        }

        void Walk(JsonElement node, List<JsonElement> ancestors)
        {
            var type = Text(node, "Node Type");
            var relation = Text(node, "Relation Name");
            var index = Text(node, "Index Name");
            var cond = Text(node, "Index Cond");
            var described = Describe(node);

            if (relation == Headers)
            {
                if (type == "Index Scan" && index == RegisterIndex && OnAccountsLedger(cond))
                {
                    if (ancestors.Count > 0 && Text(ancestors[^1], "Node Type") == "Limit") walks++;
                    else violations.Add($"{described}: a walk of the register's index with no Limit of its own, which reads every header of the ledger for an account with no row");
                }
                else if (type == "Index Scan" && HeaderKeys.Contains(index) && ByBalancesHeaderId(cond))
                {
                    headerProbes++;
                }
                else
                {
                    violations.Add($"{described}: a read of the headers that is neither the bounded walk nor a probe by a running balance's header id");
                }
            }
            else if (relation == Balances)
            {
                if (type is "Index Scan" or "Index Only Scan" && index == AccountIndex && OnAccount(cond))
                    ClassifyAccountRead(described, ancestors);
                else if (type == "Bitmap Heap Scan" && OwnInputs(node).ToList() is { Count: > 0 } bitmaps
                         && bitmaps.All(b => Text(b, "Node Type") == "Bitmap Index Scan"
                                             && Text(b, "Index Name") == AccountIndex
                                             && OnAccount(Text(b, "Index Cond"))))
                    ClassifyAccountRead(described, ancestors);
                else if (type == "Index Scan" && index == BalanceKey && ByWalkedHeader(cond) && OnAccount(cond))
                    walkProbes++;
                else
                    violations.Add($"{described}: a read of the running balances that is neither the count, the walk's probe nor the fallback");
            }

            ancestors.Add(node);
            foreach (var child in Children(node)) Walk(child, ancestors);
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        // The account's running balances, read for the count (under an aggregate) or for the
        // fallback (under its Limit): told apart by the nearest of the two above the read.
        void ClassifyAccountRead(string described, List<JsonElement> ancestors)
        {
            for (var i = ancestors.Count - 1; i >= 0; i--)
            {
                var above = Text(ancestors[i], "Node Type");
                if (above == "Aggregate") { counts++; return; }
                if (above == "Limit") { fallbackReads++; return; }
            }
            violations.Add($"{described}: the account's running balances read with neither an aggregate nor a Limit above");
        }

        bool OnAccount(string cond) => Matches(AccountCondition(), cond, 1, Accounts);

        bool OnAccountsLedger(string cond) =>
            Matches(LedgerCondition(), cond, 1, Accounts) && cond.Contains("posted_at <", StringComparison.Ordinal);

        bool ByBalancesHeaderId(string cond) => Matches(HeaderIdCondition(), cond, 1, Balances);

        bool ByWalkedHeader(string cond) => Matches(WalkedHeaderCondition(), cond, 1, Headers);

        bool Matches(Regex pattern, string cond, int group, string relation) =>
            pattern.Matches(cond).Any(m => relationOf.TryGetValue(m.Groups[group].Value, out var r) && r == relation);
    }

    private static string Describe(JsonElement node)
    {
        var type = Text(node, "Node Type");
        var relation = Text(node, "Relation Name");
        var index = Text(node, "Index Name");
        var cond = Text(node, "Index Cond");
        return $"{type} on {relation}"
               + (index.Length > 0 ? $" using {index}" : "")
               + (cond.Length > 0 ? $" ({cond})" : "");
    }

    private static IEnumerable<JsonElement> Children(JsonElement node) =>
        node.TryGetProperty("Plans", out var children) ? children.EnumerateArray() : [];

    /// <summary>A node's inputs, without the SubPlans and InitPlans it evaluates (row-level
    /// security's grant lookups), which are other queries.</summary>
    private static IEnumerable<JsonElement> OwnInputs(JsonElement node) =>
        Children(node).Where(c => Text(c, "Parent Relationship") is not ("SubPlan" or "InitPlan"));

    private static string Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary><c>account_id = x.id</c>, x to be resolved to <c>accounts</c>.</summary>
    [GeneratedRegex(@"\baccount_id = (\w+)\.id\b")]
    private static partial Regex AccountCondition();

    /// <summary><c>ledger_id = x.ledger_id</c>, x to be resolved to <c>accounts</c>.</summary>
    [GeneratedRegex(@"\bledger_id = (\w+)\.ledger_id\b")]
    private static partial Regex LedgerCondition();

    /// <summary><c>(id = x.header_id</c>, x to be resolved to the running balances.</summary>
    [GeneratedRegex(@"\(id = (\w+)\.header_id\b")]
    private static partial Regex HeaderIdCondition();

    /// <summary><c>header_id = x.id</c>, x to be resolved to <c>txn_headers</c>.</summary>
    [GeneratedRegex(@"\bheader_id = (\w+)\.id\b")]
    private static partial Regex WalkedHeaderCondition();
}
