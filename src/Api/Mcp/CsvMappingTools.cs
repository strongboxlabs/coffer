using System.ComponentModel;

using ModelContextProtocol.Server;

using Coffer.Api.Db.Repositories;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Mcp;

/// <summary>
/// Read-only MCP tools over the delimited-import mapping documents (ADR-0031 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// The workflow these exist for: a person pastes the first few lines of a statement, the
/// assistant works out the shape, calls <c>validate_csv_mapping</c> until the document is
/// clean, and only then calls <c>save_csv_mapping</c> (a write tool). Composing the YAML
/// is the part a model is genuinely good at; guessing whether it is correct is not, which
/// is why the validator is exposed as its own tool rather than left as a side effect of
/// saving.
/// </para>
/// <para>
/// Writes live on <see cref="McpWriteTools"/>, behind the write guard. RLS scopes every
/// read here to the bearer's grants.
/// </para>
/// </remarks>
[McpServerToolType]
public static class CsvMappingTools
{
    [McpServerTool(Name = "list_csv_mappings"), Description(
        "The delimited-import mapping documents on a ledger: id, name, and the YAML " +
        "definition as written. A mapping describes ONE institution's export shape " +
        "(delimiter, which 1-based column holds what, how the amount is signed) and is " +
        "reusable across accounts. Use get_csv_mapping for one, validate_csv_mapping to " +
        "check a draft without saving, save_csv_mapping to store one.")]
    public static async Task<IReadOnlyList<McpCsvMapping>> ListCsvMappings(
        CsvMappingsRepository mappings,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        CancellationToken cancellationToken = default) =>
        (await mappings.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            .Select(m => new McpCsvMapping(m.Id, m.Name, m.DefinitionYaml, m.SchemaVersion))
            .ToList();

    [McpServerTool(Name = "get_csv_mapping"), Description(
        "One mapping document, by id, with its YAML exactly as written — comments and " +
        "formatting included. Read this before editing so an update does not discard " +
        "the author's notes.")]
    public static async Task<McpCsvMapping?> GetCsvMapping(
        CsvMappingsRepository mappings,
        [Description("Ledger id (GUID) from list_ledgers.")] Guid ledgerId,
        [Description("Mapping id (GUID) from list_csv_mappings.")] Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        var row = await mappings.GetAsync(ledgerId, mappingId, cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new McpCsvMapping(row.Id, row.Name, row.DefinitionYaml, row.SchemaVersion);
    }

    /// <summary>
    /// The vocabularies this tool teaches, and the example it teaches them with.
    /// </summary>
    /// <remarks>
    /// <para>An attribute argument must be a compile-time constant, so the description
    /// cannot read <see cref="CsvMappingValidator.AcceptedDelimiters"/> at run time.
    /// These consts are therefore a COPY of the gate's own vocabulary, and exactly the
    /// kind of copy that goes stale: a delimiter added to the validator and not here
    /// leaves an agent unaware it exists, and a name here the gate refuses sends one
    /// into an error it cannot diagnose. <c>CsvVocabularyParityTests</c> holds them to
    /// the validator's own tables.</para>
    /// <para>The example is a const of its own because an example is documentation that
    /// RUNS: an agent copies it, changes a column number, and sends it back. Nothing in
    /// an ordinary build would notice if it had stopped validating, because a
    /// description string is never executed — so a test puts this one through the gate.
    /// Two spaces of display indent, stripped before that check.</para>
    /// </remarks>
    internal const string DelimiterList = "comma | tab | semicolon | pipe";

    /// <summary>The amount shapes, as the description names them.</summary>
    internal const string ShapeList = "signed | debit_credit";

    /// <summary>The worked example in the description. See <see cref="DelimiterList"/>.</summary>
    internal const string ExampleDocument =
        "  version: 1\n" +
        "  delimiter: tab            # " + DelimiterList + "\n" +
        "  header_rows: 0            # optional, default 0\n" +
        "  footer_rows: 0            # optional, default 0\n" +
        "  date:   { column: 1, format: MM/dd/yyyy }   # .NET format, M is month\n" +
        "  payee:  { column: 3 }\n" +
        "  memo:   { column: 4 }     # optional\n" +
        "  amount: { shape: signed, column: 2, invert: true }\n";

    [McpServerTool(Name = "validate_csv_mapping"), Description(
        "Check a mapping document WITHOUT saving it. Returns valid plus every problem " +
        "found, each with a key path and a line number — all of them, not just the " +
        "first, so a draft can be fixed in one pass. " +
        "Unknown keys are ERRORS, not ignored: a mistyped key would leave a field " +
        "unmapped and the importer would read a different column as money. " +
        "Comments are ignored, so a document may explain itself. " +
        "The document looks like this (columns are 1-BASED):\n" +
        ExampleDocument +
        "amount.shape is one of: " + ShapeList + ". For debit_credit give debit_column " +
        "and credit_column instead of column, and no invert. " +
        "Set invert: true when the file signs from the ISSUER's point of view — a " +
        "purchase POSITIVE because it increases what you owe — since Coffer signs from " +
        "the account holder's, where a purchase is money out.")]
    public static McpCsvMappingVerdict ValidateCsvMapping(
        [Description("The YAML document to check.")] string definitionYaml)
    {
        var mapping = CsvMappingsRepository.Validate(definitionYaml ?? "", out var errors);
        return new McpCsvMappingVerdict(
            mapping is not null,
            errors.Select(e => new McpCsvMappingProblem(e.Path, e.Line, e.Message)).ToList());
    }
}

/// <summary>One stored mapping, as MCP sees it.</summary>
public sealed record McpCsvMapping(Guid Id, string Name, string DefinitionYaml, int SchemaVersion);

/// <summary>One problem with a document: which key, which line, and what is wrong.</summary>
public sealed record McpCsvMappingProblem(string Path, int? Line, string Message);

/// <summary>Whether a document is usable, and everything wrong with it if not.</summary>
public sealed record McpCsvMappingVerdict(bool Valid, IReadOnlyList<McpCsvMappingProblem> Problems);
