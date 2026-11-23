using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Transactions;

/// <summary>
/// Every route and connected-app tool that names a transaction by id answers a merged-away
/// one (<c>is_merged_into</c> set) as it answers an id that does not exist (decided by the
/// owner, 2026-11-18; ADR-0114 D15), or says why it need not. Fail closed in both
/// directions: a new route or tool that names a transaction by id fails until it is listed
/// here, and a listed one that no longer exists fails until it is removed.
/// </summary>
/// <remarks>
/// <para>A name says transaction when it contains header, transaction, txn or leg, in any
/// case. A route names a transaction by id when (1) a route parameter's name says
/// transaction, or the parameter follows a literal segment that does (<c>/transactions/{id}</c>);
/// (2) a handler parameter of an id type (<c>Guid</c>, <c>Guid?</c>, an array or any
/// <c>IEnumerable</c> of them), bound from the route or the query string, has such a name;
/// or (3) a parameter that is not a service (the request body, or an
/// <c>[AsParameters]</c> type) carries a property ending in <c>HeaderId</c>,
/// <c>TransactionId</c>, <c>TxnId</c> or <c>LegId</c> (singular or plural), at any depth up
/// to <see cref="MaxDepth"/>, looking through nullables, arrays and collections. A tool
/// names one by (2) or (3) over its own parameters. (settled by Claude, 2026-11-18)</para>
/// <para>Each guarded entry names its guard site; <see cref="MergedAwayRowWritesTests"/>
/// aims each guarded write at a merged-away row reached through real paths and holds its
/// answer to the one for an id that does not exist.</para>
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class MergedAwayGuardCatalogTests : DatabaseTest
{
    public MergedAwayGuardCatalogTests(PostgresFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    private const string Bank = "/api/ledgers/{ledgerId:guid}/transactions";
    private const string Investment = "/api/ledgers/{ledgerId:guid}/investment-transactions";
    private const int MaxDepth = 4;

    /// <summary>Answers a merged-away transaction as an id that does not exist, and where.</summary>
    private static readonly Dictionary<string, string> Guarded = new(StringComparer.Ordinal)
    {
        [$"PATCH {Bank}/{{headerId:guid}}"] =
            "TransactionsRepository.PatchCoreAsync, and LegAccountIdsAsync / StoredPairsAsync for the posting validation",
        [$"PUT {Bank}/{{headerId:guid}}/recon-status"] = "TransactionsRepository.SetReconStatusAsync",
        [$"POST {Bank}/{{headerId:guid}}/approve"] = "TransactionsRepository.PatchCoreAsync",
        [$"DELETE {Bank}/{{headerId:guid}}"] = "TransactionsRepository.DeleteAsync",
        [$"GET {Bank}/{{headerId:guid}}/legs"] =
            "RegisterRepository.GetAllLegsForHeaderAsync: the read the editor loads a transaction from",
        [$"GET {Bank}/{{headerId:guid}}/similar-payees"] = "TransactionsRepository.GetSimilarPayeesAsync's anchor",
        [$"GET {Bank}/{{headerId:guid}}/merge-candidates"] =
            "TransactionsRepository.GetMergeCandidatesAsync's anchor (a live review row)",
        [$"PATCH {Investment}/{{headerId:guid}}"] = "InvestmentTransactionsRepository.PatchAsync",
        [$"DELETE {Investment}/{{headerId:guid}}"] = "InvestmentTransactionsRepository.DeleteAsync",
        [$"POST {Investment}/{{headerId:guid}}/approve"] = "InvestmentTransactionsRepository.ApproveAsync",
        [$"GET {Investment}/{{headerId:guid}}/merge-candidates"] =
            "InvestmentTransactionsRepository.GetMergeCandidatesAsync's anchor (a live review row)",
        ["POST /api/ledgers/{ledgerId:guid}/in-kind-transfers/convert"] =
            "InvestmentTransactionsRepository.LoadDisposalOrAcquisitionLegAsync",
        [$"POST {Bank}/bulk-recon-status"] = "BulkTransactionsRepository.BuildSelectionQueryAsync: an unknown id is ignored",
        [$"POST {Bank}/bulk-delete"] =
            "BulkTransactionsRepository.BuildSelectionQueryAsync and HasReadOnlyRowAsync: an unknown id is ignored",
        [$"POST {Bank}/bulk-unhide"] =
            "BulkTransactionsRepository.BuildSelectionQueryAsync and HasReadOnlyRowAsync: an unknown id is ignored",
        [$"POST {Bank}/bulk-move-account"] =
            "BulkTransactionsRepository.BuildSelectionQueryAsync and HasReadOnlyRowAsync: an unknown id is ignored",
        [$"POST {Bank}/selection-summary"] = "BulkTransactionsRepository.BuildSelectionQueryAsync: an unknown id is not counted",
        ["mcp:set_transaction_category"] = "TransactionsRepository.RecategorizeAsync: rejected as not-in-ledger",
        ["mcp:set_split_posting_category"] =
            "TransactionsRepository.RecategorizeSplitPostingsAsync: rejected as not-in-ledger",
        ["mcp:set_transaction_tags"] = "TransactionsRepository.SetTransactionTagsAsync: reported as not in the ledger",
        ["mcp:convert_in_kind_transfer"] = "InvestmentTransactionsRepository.LoadDisposalOrAcquisitionLegAsync",
    };

    /// <summary>Names a transaction by id and need not answer a merged-away one as absent, and why.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        [$"POST {Bank}/{{headerId:guid}}/merge"] =
            "the merge's own gate refuses a tombstone at either end (merge-source-invalid), as it did before",
        [$"POST {Investment}/{{headerId:guid}}/merge"] =
            "the merge's own gate refuses a tombstone at either end (merge-source-invalid), as it did before",
        [$"POST {Bank}/balances"] =
            "a read of stored balances for the rows a register window already shows; it writes nothing and loads no editor",
        ["GET /api/ledgers/{ledgerId:guid}/accounts/{accountId:guid}/securities/{securityId:guid}/lots/"] =
            "a read: exclude_header_id leaves one transaction out of the holdings walk, which already leaves "
            + "merged-away rows out, so a merged-away id excludes nothing, as an unknown id does",
        [$"POST {Bank}/"] = "a create: a posting's legId is ignored on create, so it names no stored transaction",
        ["POST /api/ledgers/{ledgerId:guid}/reminders/"] =
            "a reminder create: a posting's legId is ignored, so it names no stored transaction",
        ["POST /api/ledgers/{ledgerId:guid}/reminders/{reminderId:guid}/fire/bank"] =
            "fires an occurrence through the bank create, where a posting's legId is ignored",
        ["PATCH /api/ledgers/{ledgerId:guid}/reminders/{reminderId:guid}"] =
            "a posting's legId may name only a line of the reminder's own template (RemindersRepository.LineIdRefusal); "
            + "any other id, a register row's leg included, is refused as not in the template",
    };

    private static readonly Regex IdProperty =
        new("(Header|Transaction|Txn|Leg)Ids?$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task Every_route_and_tool_that_names_a_transaction_by_id_is_guarded_or_exempt_with_a_reason()
    {
        await using var factory = new ApiFactory(Db).WithMcpEnabled();
        using var _ = factory.CreateClient();
        var isService = factory.Services.GetRequiredService<IServiceProviderIsService>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText ?? "";
            var byRoute = RouteNamesTransaction(endpoint.RoutePattern);
            var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
            var byParameter = handler is not null
                && handler.GetParameters().Any(p => NamesTransactionById(p, isService));
            var bodyType = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
            var byBody = bodyType is not null && CarriesTransactionIds(bodyType, depth: 0);
            if (!byRoute && !byParameter && !byBody) continue;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                found.Add($"{method} {pattern}");
        }

        foreach (var tool in typeof(Coffer.Api.Mcp.McpWriteTools).Assembly.GetTypes()
                     .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                     .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
                     .Where(x => x.Tool is not null))
        {
            if (tool.Method.GetParameters().Any(p => NamesTransactionById(p, isService)))
                found.Add($"mcp:{tool.Tool!.Name}");
        }

        // Fail closed on the scan itself: one entry per way it can see a transaction id, so
        // a matcher that stopped matching cannot pass on a smaller set.
        Assert.Contains($"PATCH {Bank}/{{headerId:guid}}", found);                // route parameter
        Assert.Contains($"POST {Bank}/bulk-delete", found);                        // body, nested
        Assert.Contains("mcp:set_transaction_tags", found);                        // tool, Guid[]
        Assert.Contains("mcp:set_transaction_category", found);                    // tool, Guid[]
        Assert.Contains(found, f => f.EndsWith("/lots/", StringComparison.Ordinal));  // query, Guid?
        Assert.Contains("PATCH /api/ledgers/{ledgerId:guid}/reminders/{reminderId:guid}", found); // body, LegId

        var listed = Guarded.Keys.Concat(Exempt.Keys).ToHashSet(StringComparer.Ordinal);
        var both = Guarded.Keys.Intersect(Exempt.Keys, StringComparer.Ordinal).ToList();
        Assert.True(both.Count == 0, "Listed as both guarded and exempt:\n  " + string.Join("\n  ", both));

        var unlisted = found.Where(f => !listed.Contains(f)).ToList();
        Assert.True(unlisted.Count == 0,
            "These name a transaction by id and are neither guarded against a merged-away row (MergedAway) "
            + "nor exempt with a reason:\n  " + string.Join("\n  ", unlisted));

        var stale = listed.Where(l => !found.Contains(l)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "Listed, but no route or tool names a transaction by id there any more:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>A name that says header, transaction, txn or leg.</summary>
    private static bool SaysTransaction(string? name) =>
        name is not null
        && (name.Contains("header", StringComparison.OrdinalIgnoreCase)
            || name.Contains("transaction", StringComparison.OrdinalIgnoreCase)
            || name.Contains("txn", StringComparison.OrdinalIgnoreCase)
            || name.Contains("leg", StringComparison.OrdinalIgnoreCase));

    /// <summary>A route parameter whose name says transaction, or that follows a literal
    /// segment that does, such as <c>/transactions/{id}</c>.</summary>
    private static bool RouteNamesTransaction(RoutePattern pattern)
    {
        var previousSaysTransaction = false;
        foreach (var segment in pattern.PathSegments)
        {
            foreach (var part in segment.Parts)
            {
                if (part is RoutePatternParameterPart parameter
                    && (SaysTransaction(parameter.Name) || previousSaysTransaction))
                    return true;
            }
            previousSaysTransaction = segment.Parts.All(p => p is RoutePatternLiteralPart)
                && segment.Parts.OfType<RoutePatternLiteralPart>().Any(l => SaysTransaction(l.Content));
        }
        return false;
    }

    /// <summary>A handler or tool parameter that names a transaction by id: an id-typed one
    /// whose name (or bound query name) says transaction, or a non-service one that carries
    /// a transaction id property.</summary>
    private static bool NamesTransactionById(ParameterInfo p, IServiceProviderIsService isService)
    {
        if (IsIdType(p.ParameterType))
        {
            var boundName = p.GetCustomAttributes()
                .OfType<IFromQueryMetadata>().Select(a => a.Name)
                .Concat(p.GetCustomAttributes().OfType<IFromRouteMetadata>().Select(a => a.Name))
                .FirstOrDefault(n => n is not null);
            return SaysTransaction(p.Name) || SaysTransaction(boundName);
        }
        if (isService.IsService(p.ParameterType)) return false;
        return CarriesTransactionIds(p.ParameterType, depth: 0);
    }

    /// <summary><c>Guid</c>, <c>Guid?</c>, or an array or <c>IEnumerable</c> of either.</summary>
    private static bool IsIdType(Type type)
    {
        if (type == typeof(Guid) || type == typeof(Guid?)) return true;
        var element = ElementType(type);
        return element is not null && (element == typeof(Guid) || element == typeof(Guid?));
    }

    /// <summary>The element type of an array or <c>IEnumerable&lt;T&gt;</c> (not a string),
    /// else null.</summary>
    private static Type? ElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];
        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>A Coffer type that carries a transaction id: a property ending in HeaderId,
    /// TransactionId, TxnId or LegId (or the plural), itself or nested, looking through
    /// nullables, arrays and collections.</summary>
    private static bool CarriesTransactionIds(Type type, int depth)
    {
        if (depth > MaxDepth) return false;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ElementType(type) is { } element) return CarriesTransactionIds(element, depth);
        if (type.Namespace is not { } ns || !ns.StartsWith("Coffer.", StringComparison.Ordinal)
            || type.IsEnum || type.IsPrimitive)
            return false;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IdProperty.IsMatch(property.Name)) return true;
            if (CarriesTransactionIds(property.PropertyType, depth + 1)) return true;
        }
        return false;
    }
}
