using System.ComponentModel;

using Coffer.Importer.Moneydance.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coffer.Importer.Moneydance;

/// <summary>
/// CLI entry point for <c>coffer-import-moneydance audit</c> — answers
/// the question "how much of this MD export is the importer dropping
/// or transforming lossily?" by walking the raw <see cref="MdItem"/>
/// stream and counting occurrences of each known-lossy attribute.
/// </summary>
/// <remarks>
/// Companion to <c>docs/moneydance-import-fidelity.md</c> (the prose
/// audit). This command runs the same audit against a large real-world
/// export so the gap-list translates to concrete counts: "yes 17 of
/// your transactions have per-leg tags that disagree with the header"
/// vs. "this is purely theoretical, your data doesn't trigger it."
///
/// Why a CLI subcommand and not a one-off script: the importer already
/// has typed readers for the MD JSON shape (<see cref="MdItem"/>,
/// <see cref="MdItemReader"/>); duplicating that parsing in Python
/// would silently drift from the canonical understanding of the
/// format. Extending the existing CLI keeps a single source of truth.
/// </remarks>
internal sealed class AuditCommand : Command<AuditCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the Moneydance JSON export file.")]
        [CommandArgument(0, "<export-file>")]
        public string ExportFile { get; init; } = string.Empty;

        [Description("Show up to this many example rows per finding.")]
        [CommandOption("--examples <N>")]
        public int Examples { get; init; } = 5;
    }

    protected override int Execute(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var path = settings.ExportFile;
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Export file not found:[/] {path}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Loading [cyan]{Path.GetFileName(path)}[/] …");
        var export = MdItemReader.ReadFile(path);
        var txns = export.AllItems.Where(i => i.ObjType == "txn").ToList();
        AnsiConsole.MarkupLine(
            $"  {export.AllItems.Count:N0} items, {txns.Count:N0} txns\n");

        AuditItemTypeCensus(export.AllItems);
        AuditPerLegTags(txns, settings.Examples);
        AuditPerLegStatus(txns, settings.Examples);
        AuditOlOrigPayee(txns, settings.Examples);
        AuditOlOrigMemo(txns);
        AuditOfxFeedIds(txns);
        AuditSingleLegLegDesc(txns, settings.Examples);
        AuditAttachmentsAcrossAllItems(export.AllItems);
        AuditCustomFields(txns);
        AuditCurrencyConversion(txns);
        AuditUnreadRootKeys(txns);

        return 0;
    }

    // -----------------------------------------------------------------
    // Item-type census — counts every distinct obj_type in the export
    // and marks whether the importer's pipeline handles it. Surfaces
    // wholesale-dropped item types (budgets, address book, saved
    // reports, etc.) that the per-txn audits never see.
    // -----------------------------------------------------------------
    private static void AuditItemTypeCensus(IReadOnlyList<MdItem> all)
    {
        // Item types the pipeline currently consumes. Keep in sync
        // with the *ImportStep classes — adding a new step means
        // moving its obj_type from "dropped" to "imported".
        var imported = new HashSet<string>(StringComparer.Ordinal)
        {
            "acct", "txn", "curr", "csnap", "reminder", "tag",
        };

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in all)
        {
            counts.TryGetValue(item.ObjType, out var c);
            counts[item.ObjType] = c + 1;
        }

        AnsiConsole.MarkupLine("[bold]Item-type census (every `obj_type` in the export)[/]");
        var table = new Table().AddColumns("obj_type", "count", "imported?");
        foreach (var (type, count) in counts.OrderByDescending(kv => kv.Value))
        {
            var ok = imported.Contains(type);
            table.AddRow(
                type,
                count.ToString("N0"),
                ok ? "[green]yes[/]" : "[red]NO — dropped[/]");
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    // -----------------------------------------------------------------
    // Attachments — sweep ALL items (not just txns) for fields that
    // look like attachment / receipt / file references. MD stores
    // attachments as a separate item type (rather than inline on txn),
    // so the previous txn-only sweep would miss them. Counts both
    // attachment-bearing items and txns that reference them.
    // -----------------------------------------------------------------
    private static void AuditAttachmentsAcrossAllItems(IReadOnlyList<MdItem> all)
    {
        var perTypeKeyCounts = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var attachmentItemCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in all)
        {
            // Heuristic 1: dedicated attachment item types.
            var typeLower = item.ObjType.ToLowerInvariant();
            if (typeLower.Contains("attach") || typeLower.Contains("file")
                || typeLower.Contains("receipt") || typeLower == "afile"
                || typeLower == "fdata")
            {
                attachmentItemCounts.TryGetValue(item.ObjType, out var c);
                attachmentItemCounts[item.ObjType] = c + 1;
            }

            // Heuristic 2: attachment-shaped fields on any item type.
            foreach (var k in item.Fields.Keys)
            {
                var kl = k.ToLowerInvariant();
                if (kl.StartsWith("attach") || kl.Contains("file_ref")
                    || kl.Contains("receipt") || kl.Contains("file_id")
                    || kl == "files")
                {
                    if (!perTypeKeyCounts.TryGetValue(item.ObjType, out var inner))
                    {
                        inner = new SortedDictionary<string, int>(StringComparer.Ordinal);
                        perTypeKeyCounts[item.ObjType] = inner;
                    }
                    inner.TryGetValue(k, out var c);
                    inner[k] = c + 1;
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]Attachments / receipts (across ALL item types)[/]");
        if (attachmentItemCounts.Count == 0 && perTypeKeyCounts.Count == 0)
        {
            AnsiConsole.MarkupLine("  none found");
        }
        else
        {
            if (attachmentItemCounts.Count > 0)
            {
                AnsiConsole.MarkupLine("  Dedicated attachment items:");
                foreach (var (type, count) in attachmentItemCounts.OrderByDescending(kv => kv.Value))
                    AnsiConsole.MarkupLine($"    obj_type={type}: {count:N0}");
            }
            if (perTypeKeyCounts.Count > 0)
            {
                AnsiConsole.MarkupLine("  Attachment-shaped fields on existing items:");
                foreach (var (type, inner) in perTypeKeyCounts)
                {
                    foreach (var (key, count) in inner.OrderByDescending(kv => kv.Value))
                        AnsiConsole.MarkupLine($"    {type}.{key}: {count:N0}");
                }
            }
        }
        AnsiConsole.WriteLine();
    }

    // -----------------------------------------------------------------
    // Per-leg tags — MD `split.tags` exists per leg; the importer
    // unions them up into header-level tags and loses per-leg
    // attribution. Count the txns where leg-level tag disagreement
    // means the union was actually lossy.
    // -----------------------------------------------------------------
    private static void AuditPerLegTags(IReadOnlyList<MdItem> txns, int sampleLimit)
    {
        var legsWithTags = 0;
        var txnsWithPerLegTags = 0;
        var txnsWithTagDisagreement = 0;
        var examples = new List<string>();

        foreach (var t in txns)
        {
            var legTags = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var v = t.GetString($"{i}.tags");
                if (!string.IsNullOrEmpty(v))
                {
                    legsWithTags++;
                    legTags.Add(v);
                }
            }
            if (legTags.Count == 0) continue;
            txnsWithPerLegTags++;

            var headerTag = t.GetString("tags") ?? string.Empty;
            // Disagreement: at least one leg's tag value differs from
            // the header tag (or the header is empty and legs are not).
            var disagrees = legTags.Any(lt => !string.Equals(lt, headerTag, StringComparison.Ordinal))
                            || legTags.Distinct(StringComparer.Ordinal).Count() > 1;
            if (disagrees)
            {
                txnsWithTagDisagreement++;
                if (examples.Count < sampleLimit)
                {
                    var desc = t.GetString("desc") ?? "";
                    examples.Add(
                        $"  {desc[..Math.Min(40, desc.Length)],-40} " +
                        $"header={Quote(headerTag),-20} legs=[{string.Join(", ", legTags.Select(Quote))}]");
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]Per-leg tags (`split.tags`)[/]");
        AnsiConsole.MarkupLine($"  legs with tags:             {legsWithTags,8:N0}");
        AnsiConsole.MarkupLine($"  txns with per-leg tags:     {txnsWithPerLegTags,8:N0}");
        AnsiConsole.MarkupLine(
            $"  txns where leg tags differ: {txnsWithTagDisagreement,8:N0}   " +
            "[yellow]<- per-leg attribution lost on import[/]");
        foreach (var ex in examples) AnsiConsole.WriteLine(ex);
        AnsiConsole.WriteLine();
    }

    // -----------------------------------------------------------------
    // Per-leg status — MD `split.stat`. The importer reads it into
    // MdSplit.Status but never writes it; the leg's status defaults
    // to whatever the header carries. A mixed-leg-status pattern
    // (cleared + uncleared on different legs of the same event)
    // can't be represented in our schema today.
    // -----------------------------------------------------------------
    private static void AuditPerLegStatus(IReadOnlyList<MdItem> txns, int sampleLimit)
    {
        var legsWithStat = 0;
        var txnsWithPerLegStat = 0;
        var txnsWithMixedStat = 0;
        var examples = new List<string>();

        foreach (var t in txns)
        {
            var legStats = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var v = t.GetString($"{i}.stat");
                if (!string.IsNullOrEmpty(v))
                {
                    legsWithStat++;
                    legStats.Add(v);
                }
            }
            if (legStats.Count == 0) continue;
            txnsWithPerLegStat++;
            if (legStats.Distinct(StringComparer.Ordinal).Count() > 1)
            {
                txnsWithMixedStat++;
                if (examples.Count < sampleLimit)
                {
                    var desc = t.GetString("desc") ?? "";
                    examples.Add(
                        $"  {desc[..Math.Min(40, desc.Length)],-40} stats=[{string.Join(", ", legStats.Select(Quote))}]");
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]Per-leg status (`split.stat`)[/]");
        AnsiConsole.MarkupLine($"  legs with stat:              {legsWithStat,8:N0}");
        AnsiConsole.MarkupLine($"  txns with any per-leg stat:  {txnsWithPerLegStat,8:N0}");
        AnsiConsole.MarkupLine(
            $"  txns with MIXED leg statuses:{txnsWithMixedStat,8:N0}   " +
            "[yellow]<- partial-clear pattern, can't represent today[/]");
        foreach (var ex in examples) AnsiConsole.WriteLine(ex);
        AnsiConsole.WriteLine();
    }

    // -----------------------------------------------------------------
    // ol.orig-payee vs desc — original feed payee before user edit.
    // The importer uses orig as a fallback only when desc is empty;
    // for any user-curated row, the original-bank-payee trail is
    // dropped. Quantifies "how often did the user actually rename?"
    // -----------------------------------------------------------------
    private static void AuditOlOrigPayee(IReadOnlyList<MdItem> txns, int sampleLimit)
    {
        var hasOrig = 0;
        var diverges = 0;
        var examples = new List<string>();

        foreach (var t in txns)
        {
            var orig = t.GetString("ol.orig-payee");
            if (string.IsNullOrEmpty(orig)) continue;
            hasOrig++;

            var desc = (t.GetString("desc") ?? "").Trim();
            if (!string.Equals(orig.Trim(), desc, StringComparison.Ordinal))
            {
                diverges++;
                if (examples.Count < sampleLimit)
                {
                    examples.Add(
                        $"  orig={Quote(orig[..Math.Min(38, orig.Length)]),-40} " +
                        $"-> desc={Quote(desc[..Math.Min(38, desc.Length)])}");
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]`ol.orig-payee` vs user-curated `desc`[/]");
        AnsiConsole.MarkupLine($"  txns with ol.orig-payee:                    {hasOrig,8:N0}");
        AnsiConsole.MarkupLine(
            $"  where curated desc DIFFERS from original:   {diverges,8:N0}   " +
            "[yellow]<- user edit history lost on import[/]");
        foreach (var ex in examples) AnsiConsole.WriteLine(ex);
        AnsiConsole.WriteLine();
    }

    private static void AuditOlOrigMemo(IReadOnlyList<MdItem> txns)
    {
        var hasOrig = 0;
        var diverges = 0;

        foreach (var t in txns)
        {
            var orig = t.GetString("ol.orig-memo");
            if (string.IsNullOrEmpty(orig)) continue;
            hasOrig++;

            var memo = (t.GetString("memo") ?? "").Trim();
            if (!string.Equals(orig.Trim(), memo, StringComparison.Ordinal))
                diverges++;
        }

        AnsiConsole.MarkupLine("[bold]`ol.orig-memo` vs user-curated `memo`[/]");
        AnsiConsole.MarkupLine($"  txns with ol.orig-memo:                     {hasOrig,8:N0}");
        AnsiConsole.MarkupLine($"  where curated memo DIFFERS from original:   {diverges,8:N0}");
        AnsiConsole.WriteLine();
    }

    private static void AuditOfxFeedIds(IReadOnlyList<MdItem> txns)
    {
        var hasFitid = txns.Count(t => !string.IsNullOrEmpty(t.GetString("ol_fitid_1")));
        var hasFiId = txns.Count(t => !string.IsNullOrEmpty(t.GetString("ol_fi_id")));

        AnsiConsole.MarkupLine("[bold]OFX bank-feed identifiers[/]");
        AnsiConsole.MarkupLine($"  txns with ol_fitid_1:  {hasFitid,8:N0}");
        AnsiConsole.MarkupLine($"  txns with ol_fi_id:    {hasFiId,8:N0}");
        AnsiConsole.MarkupLine("    [yellow]<- the feed-side matching key, dropped on import[/]");
        AnsiConsole.WriteLine();
    }

    private static void AuditSingleLegLegDesc(IReadOnlyList<MdItem> txns, int sampleLimit)
    {
        // Single-leg iff no `1.acctid`.
        var count = 0;
        var examples = new List<string>();

        foreach (var t in txns)
        {
            if (t.Has("1.acctid")) continue;
            var legDesc = (t.GetString("0.desc") ?? "").Trim();
            var txnDesc = (t.GetString("desc") ?? "").Trim();
            if (legDesc.Length == 0 || string.Equals(legDesc, txnDesc, StringComparison.Ordinal))
                continue;
            count++;
            if (examples.Count < sampleLimit)
            {
                examples.Add(
                    $"  txn.desc={Quote(txnDesc[..Math.Min(30, txnDesc.Length)]),-32} " +
                    $"0.desc={Quote(legDesc[..Math.Min(30, legDesc.Length)])}");
            }
        }

        AnsiConsole.MarkupLine("[bold]Single-leg events with distinct `0.desc`[/]");
        AnsiConsole.MarkupLine($"  count: {count,8:N0}   [yellow]<- single-leg leg memo dropped[/]");
        foreach (var ex in examples) AnsiConsole.WriteLine(ex);
        AnsiConsole.WriteLine();
    }

    private static void AuditCustomFields(IReadOnlyList<MdItem> txns)
    {
        var keyCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in txns)
        {
            foreach (var k in t.Fields.Keys)
            {
                var kl = k.ToLowerInvariant();
                if (kl.StartsWith("udf.") || kl.StartsWith("custom_") || kl.StartsWith("user_"))
                {
                    keyCounts.TryGetValue(k, out var c);
                    keyCounts[k] = c + 1;
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]Custom fields[/]");
        if (keyCounts.Count == 0)
        {
            AnsiConsole.MarkupLine("  none found in txn items");
        }
        else
        {
            foreach (var (k, c) in keyCounts.OrderByDescending(kv => kv.Value).Take(15))
                AnsiConsole.MarkupLine($"  {k}: {c:N0} occurrences");
        }
        AnsiConsole.WriteLine();
    }

    private static void AuditCurrencyConversion(IReadOnlyList<MdItem> txns)
    {
        // Heuristic: a split's pamt != samt indicates currency conversion
        // (parent-amount in different units than split-amount).
        var fxTxns = 0;
        foreach (var t in txns)
        {
            for (var i = 0; i < 32; i++)
            {
                var samt = t.GetLong($"{i}.samt");
                var pamt = t.GetLong($"{i}.pamt");
                if (samt is null || pamt is null) continue;
                if (samt != pamt)
                {
                    fxTxns++;
                    break;
                }
            }
        }

        AnsiConsole.MarkupLine("[bold]Currency conversion (FX)[/]");
        AnsiConsole.MarkupLine(
            $"  txns with FX-converted splits: {fxTxns,8:N0}   " +
            "[yellow]<- amount preserved, FX rate/history isn't[/]");
        AnsiConsole.WriteLine();
    }

    private static void AuditUnreadRootKeys(IReadOnlyList<MdItem> txns)
    {
        // Root-level txn keys the importer's MdTxn typed reader knows
        // about. Anything else at the root that we ignore is a possible
        // gap. (Split-indexed keys like "0.desc" are deliberately
        // skipped — they're handled per-split.)
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "obj_type", "id", "_txnfile", "_modified", "acctid",
            "desc", "memo", "dt", "td", "dtentered", "stat", "chk",
            "ol.orig-payee", "ol.orig-memo", "ol_fi_id", "ol_fitid_1",
            "invest.txntype", "xfer_type", "reinvest", "tags",
        };
        var unknownCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in txns)
        {
            foreach (var k in t.Fields.Keys)
            {
                if (known.Contains(k)) continue;
                // Skip split-indexed keys
                var dot = k.IndexOf('.');
                if (dot > 0 && int.TryParse(k[..dot], out _)) continue;
                unknownCounts.TryGetValue(k, out var c);
                unknownCounts[k] = c + 1;
            }
        }

        AnsiConsole.MarkupLine("[bold]Root-level keys we don't read (high-coverage first)[/]");
        var total = Math.Max(1, txns.Count);
        foreach (var (k, c) in unknownCounts.OrderByDescending(kv => kv.Value).Take(20))
            AnsiConsole.MarkupLine($"  {k,-34} {c,10:N0}  ({100.0 * c / total,5:F1}%)");
    }

    private static string Quote(string s) => $"\"{s}\"";
}
