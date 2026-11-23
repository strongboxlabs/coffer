using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Coffer.Api.Db;
using Coffer.Api.Db.Entities;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The raw memo-and-tags inputs (a leg's own memo, a header's own memo, the header and
/// leg tag links) are read only where the rule's inputs, the writers' own values for an
/// edit, or the tag dictionary need them. Every reader of what a register shows reads
/// the stored presentation instead.
/// </summary>
/// <remarks>
/// <para>The scan reads the API assembly's compiled code, not its text, so it sees the
/// symbol itself: a property read in a LINQ expression (a token the expression tree
/// names), in a lambda, in an async method or a local function, each attributed to the
/// method a person wrote. A DTO field that shares a name with an input is not an input
/// and is never flagged.</para>
/// <para>It fails on a read outside the allow-list, on an allow-list entry nothing
/// reads (stale), on a scan that found too little to be reading the assembly, and on
/// its own self-test. The scan follows the entities that map the inputs, so a second
/// mapping of an input column or link table (a keyless view entity, say) fails too
/// unless it is listed.</para>
/// </remarks>
public sealed partial class AnnotationReaderCatalogTests
{
    /// <summary>The places allowed to read a raw input, by type and method, each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // The rule's inputs.
        ["LegNotes.ReadInputsAsync"] = "reads every input of the rule to compute the stored presentation",
        ["LegNotesRecomputeInterceptor.CaptureAsync"] = "finds the headers a save touches through the tag links it adds or deletes",
        // The mapping of the columns.
        ["AppDbContext.OnModelCreating"] = "maps the input columns and the link tables",
        // The write primitive's adapter: the stored own values a plan is made against
        // (every writer, a copy and a repoint included), the tag links it diffs, and the
        // feed's own memo archived before the first edit.
        ["AnnotationWriter.LoadManyAsync"] = "reads a transaction's own memos and tag links for the planner",
        ["AnnotationWriter.DiffHeaderTagsAsync"] = "the header's own tag links, diffed against a plan's",
        ["AnnotationWriter.DiffLegTagsAsync"] = "a leg's own tag links, diffed against a plan's",
        ["AnnotationWriter.CaptureOriginalsAsync"] = "archives the feed's own memo before the first edit",
        ["IngestOrchestrator.PromoteClearedAsync"] =
            "seeds the originals of a row with none, when a promote-on-clear rounds, with the synced leg's own memo, the feed's while the row is unedited (ADR-0114 D15)",
        ["AnnotationWriter.RepointPendingTag"] =
            "moves the pending tag links of a save that lost a race for a new tag's name",
        ["AnnotationWriter.StampNewLegsAsync"] =
            "computes a new leg's stored presentation from the own values the plan is storing, before the insert",
        // A header no side presents.
        ["RemindersRepository.LeglessNotesAsync"] = "a template with no legs has no stored presentation; its reminder shows its own memo and its editor its own tags",
        // The tag dictionary: usage counts, merge, delete, cleanup.
        ["TagsRepository.ListWithUsageAsync"] = "counts each tag's stored links",
        ["TagsRepository.HeadersCarryingAsync"] = "finds the transactions a tag change recomputes",
        ["TagsRepository.MergeAsync"] = "repoints the links of a merged tag",
        ["TagsRepository.IsUnused"] = "finds tags no link names, for cleanup",
    };

    [Fact]
    public void Only_the_listed_places_read_the_raw_memo_and_tag_inputs()
    {
        var reads = RawInputReads(typeof(AppDbContext).Assembly).ToList();
        Assert.True(reads.Count > 20, $"found only {reads.Count} reads of the raw inputs; the scan is not reading the assembly");

        var violations = reads
            .Where(r => !Allowed.ContainsKey(r.Place))
            .GroupBy(r => r.Place)
            .Select(g => $"{g.Key} reads {string.Join(", ", g.Select(r => r.Symbol).Distinct().Order(StringComparer.Ordinal))}: " +
                         "read the stored presentation (StoredNotesQuery), or list the place with its reason")
            .Order(StringComparer.Ordinal)
            .ToList();
        var stale = Allowed.Keys.Where(k => reads.All(r => r.Place != k))
            .Select(k => $"{k}: listed, but reads no raw input; remove the entry");
        Assert.True(violations.Count == 0 && !stale.Any(), string.Join("\n", violations.Concat(stale)));
    }

    /// <summary>
    /// The entity types allowed to map a raw input (the header memo, the leg memo, a tag
    /// link table), by context and type, each with its reason. The scan above follows
    /// these types' members, so a second mapping of the same column would be a read it
    /// never sees.
    /// </summary>
    private static readonly Dictionary<string, string> InputMappings = new(StringComparer.Ordinal)
    {
        ["AppDbContext.TxnHeaderRow"] = "the header, whose memo the scan follows",
        ["AppDbContext.TxnLegRow"] = "the leg, whose memo the scan follows",
        ["AppDbContext.TxnHeaderTagRow"] = "the header tag links, which the scan follows",
        ["AppDbContext.TxnLegTagRow"] = "the leg tag links, which the scan follows",
        ["LegNotesBackfill.Projection.HeaderRow"] = "the upgrade's frozen projection of the rule's inputs, read only to compute",
        ["LegNotesBackfill.Projection.LegRow"] = "the upgrade's frozen projection of the rule's inputs, read only to compute",
        ["LegNotesBackfill.Projection.HeaderTagRow"] = "the upgrade's frozen projection of the rule's inputs, read only to compute",
        ["LegNotesBackfill.Projection.LegTagRow"] = "the upgrade's frozen projection of the rule's inputs, read only to compute",
    };

    [Fact]
    public void No_model_maps_a_raw_input_but_the_listed_entities()
    {
        var contexts = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => typeof(DbContext).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.Contains(typeof(AppDbContext), contexts);

        var found = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var type in contexts)
        {
            using var context = StoredNotesWriteCatalogTests.ModelOnly(type);
            if (context is null)
            {
                violations.Add($"{type.FullName}: a context whose model this check cannot build; make it constructible from options or check it here");
                continue;
            }
            foreach (var entity in context.Model.GetEntityTypes())
            {
                StoreObjectIdentifier id;
                if (entity.GetTableName() is { } table) id = StoreObjectIdentifier.Table(table, entity.GetSchema());
                else if (entity.GetViewName() is { } view) id = StoreObjectIdentifier.View(view, entity.GetViewSchema());
                else continue;
                var columns = entity.GetProperties().Select(p => p.GetColumnName(id)).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var maps = id.Name switch
                {
                    "txn_headers" => columns.Contains("memo"),
                    "txn_legs" => columns.Contains("leg_memo"),
                    "txn_header_tags" or "txn_leg_tags" => true,
                    _ => false,
                };
                if (!maps) continue;
                var name = $"{TypeName(type)}.{entity.ClrType.Name}";
                found.Add(name);
                if (!InputMappings.ContainsKey(name))
                    violations.Add($"{name} maps a raw input on {id.Name}: read it through the listed entities, or list it with its reason");
            }
        }
        violations.AddRange(InputMappings.Keys.Where(k => !found.Contains(k))
            .Select(k => $"{k}: listed as mapping a raw input but no model maps it so; remove the entry"));
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void The_scan_finds_a_read_in_every_form_and_names_the_method_written()
    {
        // The samples below, compiled into this assembly: each reads one raw input in a
        // different form, and each is found under the method that holds it.
        var reads = RawInputReads(typeof(Samples).Assembly)
            .Where(r => r.Place.StartsWith(nameof(AnnotationReaderCatalogTests) + "." + nameof(Samples) + ".", StringComparison.Ordinal))
            .ToList();
        string Place(string method) => $"{nameof(AnnotationReaderCatalogTests)}.{nameof(Samples)}.{method}";

        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.Direct)) && r.Symbol == "TxnLegRow.get_LegMemo");
        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.InAnExpression)) && r.Symbol == "TxnHeaderRow.get_Memo");
        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.InALambda)) && r.Symbol == "TxnLegRow.get_LegMemo");
        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.InAnAsyncMethodAsync)) && r.Symbol == "AppDbContext.get_TxnHeaderTags");
        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.InALocalFunction)) && r.Symbol == "TxnLegTagRow.get_TagId");
        Assert.Contains(reads, r => r.Place == Place(nameof(Samples.ThroughTheGenericSet)) && r.Symbol == "Set<TxnLegTagRow>");
        // A DTO field that shares a name with an input is not one.
        Assert.DoesNotContain(reads, r => r.Place == Place(nameof(Samples.ADtoField)));
        Assert.Equal(6, reads.Select(r => r.Place).Distinct().Count());
    }

    /// <summary>Reads of the raw inputs in each form the compiler can give them, for the self-test.</summary>
    internal static class Samples
    {
        internal static string? Direct(TxnLegRow leg) => leg.LegMemo;

        internal static System.Linq.Expressions.Expression<Func<TxnHeaderRow, string?>> InAnExpression() => h => h.Memo;

        internal static IEnumerable<string?> InALambda(IEnumerable<TxnLegRow> legs) => legs.Select(l => l.LegMemo);

        internal static async Task<int> InAnAsyncMethodAsync(AppDbContext db)
        {
            await Task.Yield();
            return db.TxnHeaderTags.Count();
        }

        internal static Guid InALocalFunction(TxnLegTagRow row)
        {
            return Of(row);
            static Guid Of(TxnLegTagRow r) => r.TagId;
        }

        internal static IQueryable<TxnLegTagRow> ThroughTheGenericSet(AppDbContext db) => db.Set<TxnLegTagRow>();

        internal static string? ADtoField(Coffer.Api.Contracts.ReminderLegDto leg) => leg.LegMemo;
    }

    // =====================================================================
    // The scan
    // =====================================================================

    /// <summary>One read of a raw input: where (the method a person wrote, as Type.Method) and what.</summary>
    internal sealed record Read(string Place, string Symbol);

    /// <summary>The raw inputs: their getters, the link tables' sets and rows.</summary>
    private static bool IsRawInput(MemberInfo member, out string symbol)
    {
        symbol = $"{member.DeclaringType?.Name}.{member.Name}";
        switch (member)
        {
            case MethodInfo { Name: "get_LegMemo" } m when m.DeclaringType == typeof(TxnLegRow):
            case MethodInfo { Name: "get_Memo" } h when h.DeclaringType == typeof(TxnHeaderRow):
            case MethodInfo { Name: "get_TxnHeaderTags" or "get_TxnLegTags" } s when s.DeclaringType == typeof(AppDbContext):
                return true;
            case MethodInfo { IsGenericMethod: true, Name: nameof(DbContext.Set) } set
                when set.DeclaringType == typeof(DbContext) && set.GetGenericArguments().Any(IsLinkRow):
                symbol = $"Set<{set.GetGenericArguments()[0].Name}>";
                return true;
        }
        return member.DeclaringType is { } declaring && IsLinkRow(declaring);
    }

    private static bool IsLinkRow(Type type) => type == typeof(TxnHeaderTagRow) || type == typeof(TxnLegTagRow);

    /// <summary>Every read of a raw input in <paramref name="assembly"/>'s method bodies.</summary>
    internal static IEnumerable<Read> RawInputReads(Assembly assembly)
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in assembly.GetTypes())
        {
            // The entities themselves and the context's set declarations hold the
            // inputs; reading them there is what they are.
            if (type == typeof(TxnLegRow) || type == typeof(TxnHeaderRow) || IsLinkRow(type)) continue;
            var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
            foreach (var method in methods)
            {
                foreach (var member in ReferencedMembers(method))
                {
                    if (!IsRawInput(member, out var symbol)) continue;
                    if (method.DeclaringType == typeof(AppDbContext) && method.Name is "get_TxnHeaderTags" or "get_TxnLegTags") continue;
                    yield return new Read(PlaceOf(method), symbol);
                }
            }
        }
    }

    /// <summary>
    /// The method a person wrote that <paramref name="method"/> belongs to: itself, or for
    /// a lambda, a local function or an async state machine the method the compiler
    /// generated it from, on the outermost type a person wrote.
    /// </summary>
    internal static string PlaceOf(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name;
        string? generatedFrom = null;
        if (GeneratedName().Match(name) is { Success: true } own) generatedFrom = own.Groups[1].Value;
        while (type.Name.StartsWith('<'))
        {
            if (generatedFrom is null && GeneratedName().Match(type.Name) is { Success: true } machine
                && machine.Groups[1].Value.Length > 0)
                generatedFrom = machine.Groups[1].Value;
            type = type.DeclaringType!;
        }
        return $"{TypeName(type)}.{generatedFrom ?? name}";
    }

    private static string TypeName(Type type) =>
        type.DeclaringType is { } outer ? $"{TypeName(outer)}.{type.Name}" : type.Name;

    // "<Method>b__3_0", "<Method>d__12", "<<Method>b__0>g__Local|1_2", "<>c__DisplayClass4_0" (no name).
    [GeneratedRegex(@"^<+([^<>]*)>")]
    private static partial Regex GeneratedName();

    /// <summary>Every member <paramref name="method"/>'s body names by token.</summary>
    internal static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { il = null; }
        if (il is null) yield break;
        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } g ? g.GetGenericArguments() : null;

        var i = 0;
        while (i < il.Length)
        {
            OpCode op;
            if (il[i] == 0xFE) { op = TwoByte[il[i + 1]]; i += 2; }
            else { op = OneByte[il[i]]; i += 1; }

            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(il, i);
                MemberInfo? member = null;
                try { member = method.Module.ResolveMember(token, typeArgs, methodArgs); }
                catch (ArgumentException) { }
                if (member is not null) yield return member;
            }
            i += OperandSize(op, il, i);
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
        _ => 4,
    };

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static AnnotationReaderCatalogTests()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)field.GetValue(null)!;
            var value = (ushort)op.Value;
            if (value < 0x100) OneByte[value] = op;
            else if ((value & 0xFF00) == 0xFE00) TwoByte[value & 0xFF] = op;
        }
    }
}
