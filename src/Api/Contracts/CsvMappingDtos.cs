namespace Coffer.Api.Contracts;

/// <summary>
/// One stored mapping document (mig 222).
/// </summary>
/// <param name="DefinitionYaml">
/// The YAML as its author wrote it. Returned verbatim, not re-rendered: the point of a
/// hand-editable document is that opening it shows what was written, comments included.
/// </param>
public sealed record CsvMappingDto(
    Guid Id,
    string Name,
    string DefinitionYaml,
    int SchemaVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Create or replace a mapping.</summary>
public sealed record CsvMappingWriteRequest
{
    /// <summary>What the picker lists. Unique per ledger, case-insensitively.</summary>
    public string? Name { get; init; }

    /// <summary>The YAML document. Validated before anything is stored.</summary>
    public string? DefinitionYaml { get; init; }
}

/// <summary>
/// One problem with a document, addressed so its author can find it.
/// </summary>
/// <param name="Path">Dotted key path, e.g. <c>amount.column</c>.</param>
/// <param name="Line">1-based line in the source, or null for a missing key.</param>
public sealed record CsvMappingErrorDto(string Path, int? Line, string Message);

/// <summary>
/// The verdict on a document.
/// </summary>
/// <remarks>
/// Carries EVERY problem, not the first. Someone hand-editing YAML one error per round
/// trip is a bad afternoon, and an MCP client composing a mapping wants the whole list
/// in one response so it can fix them together.
/// </remarks>
public sealed record CsvMappingValidationResponse(
    bool Valid,
    IReadOnlyList<CsvMappingErrorDto> Errors,
    CsvMappingShapeDto? Mapping = null);

/// <summary>
/// What the document actually SAYS, once read.
/// </summary>
/// <remarks>
/// <para>The validator already builds this to decide whether a document is usable, and
/// used to throw it away. Returning it is what lets a saved mapping open in the guided
/// form: the form cannot populate itself from YAML it has not parsed, and the honest
/// answer to "who parses it" is the parser of record, not a second reader written in
/// another language that could disagree with it.</para>
/// <para>Named in the document's own vocabulary — <c>tab</c>, <c>signed</c> — rather
/// than as enum members, so the round trip through the form cannot silently rename a
/// value the document spells one way.</para>
/// </remarks>
public sealed record CsvMappingShapeDto(
    int Version,
    string Delimiter,
    int HeaderRows,
    int FooterRows,
    int DateColumn,
    string DateFormat,
    int PayeeColumn,
    int? MemoColumn,
    string AmountShape,
    int? AmountColumn,
    bool AmountInvert,
    int? DebitColumn,
    int? CreditColumn);
