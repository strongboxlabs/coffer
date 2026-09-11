using System.Globalization;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Coffer.Api.Ingest.Csv;

/// <summary>
/// Turns a mapping document into a <see cref="CsvMapping"/>, or into a list of
/// everything wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE GATE. Migration 222 stores the definition as YAML text rather than typed
/// columns, which means the database asserts almost nothing: not that a signed shape
/// names an amount column, not that an index is positive, not that the delimiter is one
/// of a known set. Every constraint the column design would have had lives here, and
/// has to be stricter, not looser, for the trade to be worth making.
/// </para>
/// <para>
/// <b>UNKNOWN KEYS ARE ERRORS.</b> A document reader's instinct is to ignore what it does
/// not recognise, and that instinct is wrong here: a mistyped <c>ammount</c> would leave
/// the amount unmapped and the importer would read some other column as money, silently.
/// A typo has to fail loudly. The same rule applies inside every block, and to keys that
/// are valid for the OTHER amount shape — <c>invert</c> under a debit/credit shape means
/// its author believed something the importer will not do.
/// </para>
/// <para>
/// <b>Every problem is reported, not just the first.</b> Someone hand-editing YAML fixing
/// one error per round trip is a bad afternoon, and an MCP client iterating on a mapping
/// wants the whole list in one response.
/// </para>
/// <para>
/// Walks the representation model by hand rather than deserialising into a POCO. The
/// deserialiser can be made to reject unknown properties, but it cannot report a key
/// PATH and a LINE for each problem, and those are the two things an author needs.
/// </para>
/// </remarks>
public static class CsvMappingValidator
{
    /// <summary>The only schema version this validator understands.</summary>
    public const int CurrentVersion = 1;

    private static readonly IReadOnlyDictionary<string, CsvDelimiter> Delimiters =
        new Dictionary<string, CsvDelimiter>(StringComparer.Ordinal)
        {
            ["comma"] = CsvDelimiter.Comma,
            ["tab"] = CsvDelimiter.Tab,
            ["semicolon"] = CsvDelimiter.Semicolon,
            ["pipe"] = CsvDelimiter.Pipe,
        };

    private static readonly IReadOnlyDictionary<string, CsvAmountShape> Shapes =
        new Dictionary<string, CsvAmountShape>(StringComparer.Ordinal)
        {
            ["signed"] = CsvAmountShape.Signed,
            ["debit_credit"] = CsvAmountShape.DebitCredit,
        };

    private static readonly string[] RootKeys =
        ["version", "delimiter", "header_rows", "footer_rows", "date", "payee", "memo", "amount"];

    /// <summary>
    /// The delimiter names a document may use.
    /// </summary>
    /// <remarks>
    /// Exposed because this vocabulary is TAUGHT in three other places — the MCP tool
    /// description, the web wizard's picker, and the comments the wizard writes into the
    /// document it emits — and a list that is copied is a list that goes stale. Only two
    /// of those can read this at run time; the rest are held to it by
    /// <c>CsvVocabularyParityTests</c>.
    /// </remarks>
    public static IReadOnlyCollection<string> AcceptedDelimiters { get; } = [.. Delimiters.Keys];

    /// <summary>The amount shapes a document may use. See <see cref="AcceptedDelimiters"/>.</summary>
    public static IReadOnlyCollection<string> AcceptedAmountShapes { get; } = [.. Shapes.Keys];

    /// <summary>
    /// The name a document would spell this delimiter with.
    /// </summary>
    /// <remarks>
    /// Read out of the same table that reads them in, so a name can never travel out
    /// differently from how it came. A second switch here would be a fifth copy of the
    /// vocabulary, which is the thing CsvVocabularyParityTests exists to prevent.
    /// </remarks>
    public static string NameOf(CsvDelimiter delimiter) => NameIn(Delimiters, delimiter);

    /// <summary>The name a document would spell this amount shape with.</summary>
    public static string NameOf(CsvAmountShape shape) => NameIn(Shapes, shape);

    private static string NameIn<T>(IReadOnlyDictionary<string, T> table, T value)
        where T : struct, Enum
    {
        foreach (var entry in table)
        {
            if (EqualityComparer<T>.Default.Equals(entry.Value, value)) return entry.Key;
        }

        // Unreachable while the table is the only thing that produces these values —
        // and worth saying so loudly if that ever stops being true, because a silent
        // fallback would put an unspellable name into a document.
        throw new InvalidOperationException(
            $"{typeof(T).Name}.{value} has no name in the document vocabulary.");
    }

    /// <summary>
    /// Parse and validate. Returns null and a non-empty <paramref name="errors"/> when the
    /// document is unusable; returns a mapping and an empty list when it is not.
    /// </summary>
    public static CsvMapping? Validate(string yaml, out IReadOnlyList<CsvMappingError> errors)
    {
        var problems = new List<CsvMappingError>();

        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                problems.Add(new CsvMappingError("", null, "The document is empty."));
                errors = problems;
                return null;
            }
            root = stream.Documents[0].RootNode as YamlMappingNode;
            if (root is null)
            {
                problems.Add(new CsvMappingError("", 1,
                    "The document must be a mapping of keys to values, e.g. `delimiter: tab`."));
                errors = problems;
                return null;
            }
        }
        catch (YamlException ex)
        {
            // The parser's own message names the construct; the line is what makes it
            // findable in an editor.
            problems.Add(new CsvMappingError("", (int)ex.Start.Line, "Not valid YAML: " + ex.Message));
            errors = problems;
            return null;
        }

        RejectUnknownKeys(root, "", RootKeys, problems);

        var version = RequiredInt(root, "version", "", problems);
        if (version is not null && version != CurrentVersion)
        {
            problems.Add(new CsvMappingError("version", LineOf(root, "version"),
                $"Unsupported version {version}. This build understands version {CurrentVersion}."));
        }

        var delimiter = RequiredEnum(root, "delimiter", "", Delimiters, problems);
        var headerRows = OptionalInt(root, "header_rows", "", problems) ?? 0;
        var footerRows = OptionalInt(root, "footer_rows", "", problems) ?? 0;
        if (headerRows < 0)
            problems.Add(new CsvMappingError("header_rows", LineOf(root, "header_rows"),
                "Cannot be negative."));
        if (footerRows < 0)
            problems.Add(new CsvMappingError("footer_rows", LineOf(root, "footer_rows"),
                "Cannot be negative."));

        var (dateColumn, dateFormat) = ReadDate(root, problems);
        var payeeColumn = ReadColumnBlock(root, "payee", required: true, problems);
        var memoColumn = ReadColumnBlock(root, "memo", required: false, problems);
        var amount = ReadAmount(root, problems);

        // A file cannot hold a date and an amount in the same field, and reading one as
        // the other is the failure this catches before it reaches money.
        foreach (var (label, column) in new[]
                 {
                     ("amount.column", amount.AmountColumn),
                     ("amount.debit_column", amount.DebitColumn),
                     ("amount.credit_column", amount.CreditColumn),
                 })
        {
            if (column is not null && dateColumn is not null && column == dateColumn)
            {
                problems.Add(new CsvMappingError(label, null,
                    $"Column {column} is already the date column; one field cannot be both."));
            }
        }

        if (problems.Count > 0)
        {
            errors = problems;
            return null;
        }

        errors = [];
        return new CsvMapping(
            Version: version!.Value,
            Delimiter: delimiter!.Value,
            HeaderRows: headerRows,
            FooterRows: footerRows,
            DateColumn: dateColumn!.Value,
            DateFormat: dateFormat!,
            PayeeColumn: payeeColumn!.Value,
            MemoColumn: memoColumn,
            AmountShape: amount.Shape!.Value,
            AmountColumn: amount.AmountColumn,
            AmountInvert: amount.Invert,
            DebitColumn: amount.DebitColumn,
            CreditColumn: amount.CreditColumn);
    }

    // ----- blocks -------------------------------------------------------------

    private static (int? Column, string? Format) ReadDate(
        YamlMappingNode root, List<CsvMappingError> problems)
    {
        var block = RequiredMap(root, "date", problems);
        if (block is null) return (null, null);

        RejectUnknownKeys(block, "date", ["column", "format"], problems);
        var column = RequiredColumn(block, "column", "date", problems);
        var format = RequiredString(block, "format", "date", problems);

        if (format is not null)
        {
            // Checked here rather than discovered per row: a format the importer cannot
            // use fails on EVERY line at import time, which reads as "the file is broken"
            // rather than "the mapping is".
            //
            // A round-trip probe alone is not enough, and the test that caught this is
            // worth keeping in mind: .NET treats unknown characters in a multi-character
            // format as LITERALS, so `nonsense` renders to a fixed string and parses
            // straight back. Self-consistent, and not a date format. What actually makes
            // a string one is that it can express a day, a month and a year — so that is
            // what is required, alongside the probe that catches formats which throw.
            var stripped = WithoutLiterals(format);
            var missing = new List<string>();
            if (!stripped.Contains('y', StringComparison.Ordinal)) missing.Add("year (y)");
            if (!stripped.Contains('M', StringComparison.Ordinal)) missing.Add("month (M)");
            if (!stripped.Contains('d', StringComparison.Ordinal)) missing.Add("day (d)");
            if (missing.Count > 0)
            {
                problems.Add(new CsvMappingError("date.format", LineOf(block, "format"),
                    $"'{format}' cannot express a date — no {string.Join(", no ", missing)}. "
                    + "Use .NET custom format specifiers, e.g. MM/dd/yyyy. "
                    + "Note M is month; lowercase m is minutes."));
            }
            else
            {
                try
                {
                    var probe = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
                    var rendered = probe.ToString(format, CultureInfo.InvariantCulture);
                    if (!DateTime.TryParseExact(rendered, format, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _))
                    {
                        problems.Add(new CsvMappingError("date.format", LineOf(block, "format"),
                            $"'{format}' is not a date format this build can read back."));
                    }
                }
                catch (FormatException)
                {
                    problems.Add(new CsvMappingError("date.format", LineOf(block, "format"),
                        $"'{format}' is not a usable .NET date format."));
                }
            }
        }

        return (column, format);
    }

    private static int? ReadColumnBlock(
        YamlMappingNode root, string key, bool required, List<CsvMappingError> problems)
    {
        if (!TryGet(root, key, out var node))
        {
            if (required)
                problems.Add(new CsvMappingError(key, null, "Required, and missing."));
            return null;
        }

        if (node is not YamlMappingNode block)
        {
            problems.Add(new CsvMappingError(key, (int)node.Start.Line,
                $"Must be a block with a `column:` key, e.g. `{key}:\n  column: 3`."));
            return null;
        }

        RejectUnknownKeys(block, key, ["column"], problems);
        return RequiredColumn(block, "column", key, problems);
    }

    private readonly record struct AmountBlock(
        CsvAmountShape? Shape, int? AmountColumn, bool Invert, int? DebitColumn, int? CreditColumn);

    private static AmountBlock ReadAmount(YamlMappingNode root, List<CsvMappingError> problems)
    {
        var block = RequiredMap(root, "amount", problems);
        if (block is null) return default;

        var shapeText = RequiredString(block, "shape", "amount", problems);
        CsvAmountShape? shape =
            shapeText is not null && Shapes.TryGetValue(shapeText, out var known) ? known : null;
        if (shapeText is not null && shape is null)
        {
            // Named from the table rather than spelled out, so the message cannot
            // outlive the vocabulary it describes.
            problems.Add(new CsvMappingError("amount.shape", LineOf(block, "shape"),
                $"'{shapeText}' is not a shape. Use one of: {string.Join(", ", Shapes.Keys)}."));
        }

        // Keys are validated PER SHAPE. `invert` under debit_credit is not a harmless
        // extra — its author believed the importer would flip a sign it never reads.
        switch (shape)
        {
            case CsvAmountShape.Signed:
                RejectUnknownKeys(block, "amount", ["shape", "column", "invert"], problems);
                return new AmountBlock(
                    shape,
                    RequiredColumn(block, "column", "amount", problems),
                    OptionalBool(block, "invert", "amount", problems) ?? false,
                    null, null);

            case CsvAmountShape.DebitCredit:
                RejectUnknownKeys(block, "amount", ["shape", "debit_column", "credit_column"], problems);
                return new AmountBlock(
                    shape,
                    null, false,
                    RequiredColumn(block, "debit_column", "amount", problems),
                    RequiredColumn(block, "credit_column", "amount", problems));

            default:
                // Shape unknown, so per-shape key checking would produce noise on top of
                // the real error. Report nothing further about this block.
                return default;
        }
    }


    /// <summary>
    /// A format with its quoted literals removed, so `'day' dd` does not look as though
    /// it already has a day specifier in the word "day".
    /// </summary>
    private static string WithoutLiterals(string format)
    {
        var sb = new System.Text.StringBuilder(format.Length);
        char? quote = null;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (quote is null && (c == '\'' || c == '"')) { quote = c; continue; }
            if (quote == c) { quote = null; continue; }
            if (quote is not null) continue;
            // A backslash escapes the next character into a literal.
            if (c == '\\') { i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ----- primitives ---------------------------------------------------------

    private static void RejectUnknownKeys(
        YamlMappingNode node, string path, string[] allowed, List<CsvMappingError> problems)
    {
        foreach (var entry in node.Children)
        {
            var key = (entry.Key as YamlScalarNode)?.Value;
            if (key is null || Array.IndexOf(allowed, key) >= 0) continue;
            var where = path.Length == 0 ? key : path + "." + key;
            problems.Add(new CsvMappingError(where, (int)entry.Key.Start.Line,
                $"Unknown key '{key}'. Allowed here: {string.Join(", ", allowed)}."));
        }
    }

    private static bool TryGet(YamlMappingNode node, string key, out YamlNode value)
    {
        foreach (var entry in node.Children)
        {
            if ((entry.Key as YamlScalarNode)?.Value == key)
            {
                value = entry.Value;
                return true;
            }
        }
        value = null!;
        return false;
    }

    private static int? LineOf(YamlMappingNode node, string key) =>
        TryGet(node, key, out var v) ? (int)v.Start.Line : null;

    private static YamlMappingNode? RequiredMap(
        YamlMappingNode root, string key, List<CsvMappingError> problems)
    {
        if (!TryGet(root, key, out var node))
        {
            problems.Add(new CsvMappingError(key, null, "Required, and missing."));
            return null;
        }
        if (node is YamlMappingNode map) return map;
        problems.Add(new CsvMappingError(key, (int)node.Start.Line, "Must be a block of keys."));
        return null;
    }

    private static string? RequiredString(
        YamlMappingNode node, string key, string path, List<CsvMappingError> problems)
    {
        var where = path.Length == 0 ? key : path + "." + key;
        if (!TryGet(node, key, out var value))
        {
            problems.Add(new CsvMappingError(where, null, "Required, and missing."));
            return null;
        }
        var text = (value as YamlScalarNode)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(new CsvMappingError(where, (int)value.Start.Line, "Must be a non-empty value."));
            return null;
        }
        return text;
    }

    private static CsvDelimiter? RequiredEnum(
        YamlMappingNode node, string key, string path,
        IReadOnlyDictionary<string, CsvDelimiter> allowed, List<CsvMappingError> problems)
    {
        var text = RequiredString(node, key, path, problems);
        if (text is null) return null;
        if (allowed.TryGetValue(text, out var value)) return value;
        problems.Add(new CsvMappingError(key, LineOf(node, key),
            $"'{text}' is not a delimiter. Use one of: {string.Join(", ", allowed.Keys)}."));
        return null;
    }

    private static int? RequiredInt(
        YamlMappingNode node, string key, string path, List<CsvMappingError> problems)
    {
        var where = path.Length == 0 ? key : path + "." + key;
        if (!TryGet(node, key, out var value))
        {
            problems.Add(new CsvMappingError(where, null, "Required, and missing."));
            return null;
        }
        var text = (value as YamlScalarNode)?.Value;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;
        problems.Add(new CsvMappingError(where, (int)value.Start.Line,
            $"'{text}' is not a whole number."));
        return null;
    }

    private static int? OptionalInt(
        YamlMappingNode node, string key, string path, List<CsvMappingError> problems) =>
        TryGet(node, key, out _) ? RequiredInt(node, key, path, problems) : null;

    private static bool? OptionalBool(
        YamlMappingNode node, string key, string path, List<CsvMappingError> problems)
    {
        if (!TryGet(node, key, out var value)) return null;
        var text = (value as YamlScalarNode)?.Value;
        if (bool.TryParse(text, out var b)) return b;
        problems.Add(new CsvMappingError(path + "." + key, (int)value.Start.Line,
            $"'{text}' is not true or false."));
        return null;
    }

    /// <summary>A 1-based column index. Zero is refused, loudly.</summary>
    private static int? RequiredColumn(
        YamlMappingNode node, string key, string path, List<CsvMappingError> problems)
    {
        var n = RequiredInt(node, key, path, problems);
        if (n is null) return null;
        if (n >= 1) return n;
        problems.Add(new CsvMappingError(path + "." + key, LineOf(node, key),
            $"Columns are numbered from 1, so {n} is not a column. "
            + "A 0 here would read a different field rather than fail."));
        return null;
    }
}
