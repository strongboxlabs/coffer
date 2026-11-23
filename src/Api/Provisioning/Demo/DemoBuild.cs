using Coffer.Importer.Moneydance.Json;

namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// One generated Demo: the export's bytes, the card statement CSV, and the manifest
/// the seeding step reads.
/// </summary>
/// <remarks>
/// Bytes, then <see cref="MdItemReader"/>, rather than a hand-built
/// <see cref="MdExport"/>: an item's fields are views into a document the export
/// owns, and the raw JSON of each account, transaction and reminder becomes its
/// stored provider payload. Parsing the bytes sends the Demo through exactly an
/// upload's parse.
/// </remarks>
public sealed record DemoBuild(byte[] Json, string CardStatementCsv, DemoManifest Manifest)
{
    /// <summary>Parse the export. The caller owns, and disposes, the result.</summary>
    public MdExport ToExport() => MdItemReader.Read(new MemoryStream(Json, writable: false));
}
