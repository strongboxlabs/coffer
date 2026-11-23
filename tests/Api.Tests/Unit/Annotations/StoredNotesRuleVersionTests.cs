using System.Security.Cryptography;
using System.Text;

using Coffer.Api.Db;
using Coffer.Api.Migrations;
using Coffer.Domain.Annotations;

using static Coffer.Api.Tests.Unit.Annotations.Scenarios;

namespace Coffer.Api.Tests.Unit.Annotations;

/// <summary>
/// The stored memo-and-tags presentation is the rule's output at the time it was
/// written. A change to that output (the rule's vectors, how its facts are built from a
/// header's rows, or how the result is stored) leaves every existing install holding the
/// old values, so it ships as a numbered C# step that recomputes everything, and the
/// step also retires older snapshots through the schema version. This guard ties the two
/// together: the digest of the stored output over the perspective matrix is recorded
/// per recompute step, and the current output must match the digest of the last one.
/// </summary>
/// <remarks>
/// <para>On a failure here: add the recompute step (a <see cref="MigrationRunner.CSharpSteps"/>
/// entry after a new numbered script), then record the digest this test prints under
/// that step's name. A recorded digest is never edited: it is the output its step wrote
/// to installs that have run it.</para>
/// <para>Keyed on the output, not on the source text: a refactor that changes nothing
/// stored passes, and any change to what is stored fails, wherever it was made.</para>
/// </remarks>
public sealed class StoredNotesRuleVersionTests
{
    /// <summary>Every recompute step, with the digest of the output it stores. Append only.</summary>
    private static readonly Dictionary<string, string> Recorded = new(StringComparer.Ordinal)
    {
        [LegNotesBackfill.Name] = "3C1C39FF440E846C116D3322332BEDF644E2F6ADD2A846E4D1F0B96E4E71C7E0",
    };

    [Fact]
    public void The_stored_output_is_the_one_the_last_recompute_step_stores()
    {
        var steps = MigrationRunner.CSharpSteps.Select(s => s.Name).ToList();
        Assert.Equal(steps.Order(StringComparer.Ordinal), Recorded.Keys.Order(StringComparer.Ordinal));
        // The step a run through every step computes in, chosen as the runner chooses it.
        var last = MigrationRunner.ComputingStep(steps, steps.ToHashSet(StringComparer.Ordinal))!;
        var digest = Digest(Golden());
        Assert.True(digest == Recorded[last],
            $"The stored memo-and-tags output over the perspective matrix is now {digest}; the last recompute step, "
            + $"{last}, stores {Recorded[last]}. A change to what is stored needs a new recompute step: add it, "
            + "then record this digest under its name. Never edit a recorded digest.");
    }

    [Fact]
    public void The_golden_output_covers_every_kind_and_every_tag_role()
    {
        // The digest is only as wide as the matrix: it must reach every kind the rule
        // produces and every role a stored tag row carries, or a change there passes.
        var text = Golden();
        foreach (var kind in Enum.GetNames<LegKind>())
            Assert.Contains($"kind={kind}|", text, StringComparison.Ordinal);
        foreach (var role in new[] { "s", "p", "i", "t" })
            Assert.Matches($@"tags=[^\n]*:[spit]*{role}", text);
    }

    private static readonly Dictionary<Guid, Guid> HoldingsOf = new()
    {
        [Brokerage] = Holdings,
        [OtherBrokerage] = OtherHoldings,
    };

    /// <summary>The stored output for every shape of the matrix, under every placement of notes.</summary>
    internal static string Golden()
    {
        var text = new StringBuilder();
        foreach (var (name, shape) in All())
        {
            var legCount = shape.Legs.Count;
            // No notes; the header's only; each leg's own alone; everything at once.
            var placements = new List<(string Label, bool Header, int[] Legs)> { ("none", false, []), ("header", true, []) };
            placements.AddRange(Enumerable.Range(0, legCount).Select(i => ($"leg{i}", false, new[] { i })));
            placements.Add(("all", true, [.. Enumerable.Range(0, legCount)]));

            foreach (var (label, header, own) in placements)
            {
                var headerId = Id($"{name}/header");
                var legIds = Enumerable.Range(0, legCount).Select(i => Id($"{name}/leg{i}")).ToList();
                var input = new NotesHeaderInput(
                    headerId, shape.Action, shape.OriginatingAccountId,
                    header ? "header memo" : null,
                    header ? [Id("tag/header")] : [],
                    [.. shape.Legs.Select((l, i) => new NotesLegInput(
                        legIds[i], l.Key.AccountId, l.Key.PostingIndex,
                        own.Contains(i) ? $"memo {i}" : null,
                        own.Contains(i) ? [Id($"tag/{i}")] : []))]);
                foreach (var leg in StoredNotes.Compute(input, HoldingsOf).OrderBy(l => legIds.IndexOf(l.LegId)))
                {
                    var stored = LegNotes.ToRows(leg, Id("ledger"));
                    var n = stored.Notes;
                    string Leg(Guid id) => legIds.IndexOf(id) is var i and >= 0 ? $"leg{i}" : id == headerId ? "header" : "?";
                    string Tag(Guid id) => id == Id("tag/header") ? "th"
                        : Enumerable.Range(0, legCount).FirstOrDefault(k => Id($"tag/{k}") == id, -1) is var j and >= 0 ? $"t{j}" : "?";
                    text.Append(
                        $"{name}|{label}|{Leg(n.LegId)}@{NameOf(shape.Legs[legIds.IndexOf(n.LegId)].Key.AccountId)}|{Leg(n.HeaderId)}"
                        + $"|kind={n.Kind}|ro={n.IsReadOnly}|groups={n.GroupsByHeader}|memo={n.Memo ?? "∅"}|inh={n.MemoInherited}"
                        + $"|inhMemo={n.InheritedMemo ?? "∅"}|from={(n.InheritFromAccountId is { } a ? NameOf(a) : "∅")}"
                        + $"|tagsInh={n.TagsInherited}|hdrMemo={n.HeaderMemo ?? "∅"}|hdrInPlay={n.HeaderInPlay}"
                        + "|tags=" + string.Join(',', stored.Tags.OrderBy(t => Tag(t.TagId), StringComparer.Ordinal).Select(t =>
                            $"{Tag(t.TagId)}@{Leg(t.HeaderId)}:{(t.Shown ? "s" : "")}{(t.InPlay ? "p" : "")}{(t.Inherited ? "i" : "")}{(t.TransactionLevel ? "t" : "")}"))
                        + "\n");
                }
            }
        }
        return text.ToString();
    }

    private static string Digest(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
