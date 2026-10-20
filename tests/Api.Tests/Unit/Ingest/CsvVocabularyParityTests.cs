using System.Text.RegularExpressions;

using Coffer.Api.Ingest.Csv;
using Coffer.Api.Mcp;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// One vocabulary, taught in four places — and only the gate decides what it is.
/// </summary>
/// <remarks>
/// <para>A mapping document's accepted values live in <see cref="CsvMappingValidator"/>.
/// Three other things TELL people about them: the MCP tool description, the TypeScript
/// union the web wizard's picker is built from, and the comments the wizard writes into
/// the document it emits. None of them can read the validator's tables — two are
/// compile-time constants in another language, one is an attribute argument, which must
/// be constant — so all three are copies.</para>
/// <para>Both directions of drift hurt, differently. A value the validator accepts but
/// nobody mentions is a feature nobody can find. A value one of these teaches but the
/// gate refuses is worse: it reads as instructions and ends in an error the reader did
/// not cause and cannot diagnose, because the document they were handed was the wrong
/// one. So these tests compare sets, not subsets.</para>
/// </remarks>
public sealed class CsvVocabularyParityTests
{
    [Fact]
    public void The_example_the_MCP_tool_teaches_is_one_the_gate_accepts()
    {
        // An example is documentation that runs: an agent copies it, changes a column
        // number and sends it back. Nothing else in the build would notice if it had
        // stopped validating, because a description string is never executed.
        var document = Dedent(CsvMappingTools.ExampleDocument);

        var mapping = CsvMappingValidator.Validate(document, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(mapping);
        // Not just "it parsed" — the example is also what teaches the reader what each
        // key DOES, so the values it demonstrates have to be the ones it appears to.
        Assert.Equal(CsvDelimiter.Tab, mapping!.Delimiter);
        Assert.Equal(1, mapping.DateColumn);
        Assert.Equal(3, mapping.PayeeColumn);
        Assert.Equal(4, mapping.MemoColumn);
        Assert.Equal(2, mapping.AmountColumn);
        Assert.True(mapping.AmountInvert);
    }

    [Fact]
    public void The_delimiters_the_MCP_tool_lists_are_exactly_the_ones_accepted()
    {
        AssertSameVocabulary(
            "the MCP tool description",
            CsvMappingValidator.AcceptedDelimiters,
            Split(CsvMappingTools.DelimiterList));
    }

    [Fact]
    public void The_amount_shapes_the_MCP_tool_names_are_exactly_the_ones_accepted()
    {
        AssertSameVocabulary(
            "the MCP tool description",
            CsvMappingValidator.AcceptedAmountShapes,
            Split(CsvMappingTools.ShapeList));
    }

    [Fact]
    public void The_delimiters_the_web_type_allows_are_exactly_the_ones_accepted()
    {
        // The wizard's picker and the comment it writes into the emitted document are
        // both derived from this union, so pinning the union pins all three at once.
        // Checked across the language boundary because there is no other seam: nothing
        // in a C# build reads a .ts file, and nothing in a web build reads the gate.
        AssertSameVocabulary(
            "the web's CsvDelimiterName",
            CsvMappingValidator.AcceptedDelimiters,
            WebUnion("CsvDelimiterName"));
    }

    [Fact]
    public void The_amount_shapes_the_web_type_allows_are_exactly_the_ones_accepted()
    {
        // Arrived with the parsed-shape DTO: the form populates itself from what the
        // server says a document contains, so `signed` and `debit_credit` now cross the
        // boundary as data and not only as something the wizard writes.
        AssertSameVocabulary(
            "the web's CsvAmountShapeName",
            CsvMappingValidator.AcceptedAmountShapes,
            WebUnion("CsvAmountShapeName"));
    }

    /// <summary>The members of a string-literal union declared in csvMapping.ts.</summary>
    private static List<string> WebUnion(string typeName)
    {
        var source = File.ReadAllText(Locate("src/Web/src/lib/types/csvMapping.ts"));
        var declaration = Regex.Match(source, $@"export type {Regex.Escape(typeName)}\s*=\s*([^;]+);");
        Assert.True(declaration.Success,
            $"Could not find `export type {typeName} = ...;` in csvMapping.ts. If it was "
            + "renamed or moved, this guard needs pointing at its new home rather than "
            + "deleting — it is the only thing holding the web vocabulary to the gate.");

        return [.. declaration.Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('\'', '"'))];
    }

    [Fact]
    public void Every_name_a_document_can_use_survives_the_round_trip_back_out()
    {
        // The form loads a saved document by asking the server what it says, so a name
        // travels OUT as well as in. A delimiter that came in as `semicolon` and went
        // back out as anything else would reopen in the wizard as a different file
        // format — silently, since both names are legal.
        foreach (var name in CsvMappingValidator.AcceptedDelimiters)
        {
            var mapping = CsvMappingValidator.Validate(DocumentWith(delimiter: name), out var errors);

            Assert.Empty(errors);
            Assert.Equal(name, CsvMappingValidator.NameOf(mapping!.Delimiter));
        }
    }

    [Fact]
    public void Every_amount_shape_survives_the_round_trip_back_out()
    {
        foreach (var name in CsvMappingValidator.AcceptedAmountShapes)
        {
            // Each shape owns different keys, so the document has to be built per shape
            // rather than having `shape:` swapped in a fixed one.
            var amount = name == "signed"
                ? "  shape: signed\n  column: 2\n  invert: true"
                : "  shape: debit_credit\n  debit_column: 3\n  credit_column: 4";
            var mapping = CsvMappingValidator.Validate(DocumentWith(amount: amount), out var errors);

            Assert.Empty(errors);
            Assert.Equal(name, CsvMappingValidator.NameOf(mapping!.AmountShape));
        }
    }

    /// <summary>A valid document, varying only the part under test.</summary>
    private static string DocumentWith(string delimiter = "tab", string? amount = null) =>
        "version: 1\n"
        + $"delimiter: {delimiter}\n"
        + "header_rows: 0\n"
        + "date:\n  column: 1\n  format: MM/dd/yyyy\n"
        + "payee:\n  column: 3\n"
        + "amount:\n" + (amount ?? "  shape: signed\n  column: 2\n  invert: true") + "\n";

    /// <summary>
    /// Compare as sets, and say which way it drifted — "they differ" sends the reader
    /// hunting for which of two lists is wrong.
    /// </summary>
    private static void AssertSameVocabulary(
        string what, IReadOnlyCollection<string> accepted, IReadOnlyCollection<string> taught)
    {
        var missing = accepted.Except(taught, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var invented = taught.Except(accepted, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && invented.Count == 0,
            $"""
            {what} has drifted from CsvMappingValidator.

            Accepted by the gate : {string.Join(", ", accepted.OrderBy(v => v, StringComparer.Ordinal))}
            Taught by {what,-11}: {string.Join(", ", taught.OrderBy(v => v, StringComparer.Ordinal))}

            Accepted but never mentioned : {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}
            Taught but the gate refuses  : {(invented.Count == 0 ? "(none)" : string.Join(", ", invented))}
            """);
    }

    /// <summary>Split a `a | b | c` display list into its values.</summary>
    private static List<string> Split(string list) =>
        [.. list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>Strip the two spaces of display indent the description carries.</summary>
    private static string Dedent(string block) =>
        string.Join('\n', block.Split('\n').Select(line => line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line));

    private static string Locate(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate '{relative}' from {AppContext.BaseDirectory}");
    }
}
