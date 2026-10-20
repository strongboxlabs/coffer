namespace Coffer.Api.Ingest;

/// <summary>
/// File-based ingest provider (ADR-0031 §1). A file provider is
/// stateless per upload — accepts a payload stream + parsing
/// context, parses it into typed records, returns them. No
/// long-lived connection.
/// </summary>
/// <remarks>
/// <para>The split from <see cref="IPullProvider"/> is intentional:
/// pull providers carry connection lifecycle (auth, last-synced,
/// reauth status); file providers don't. Collapsing them into a
/// single interface with a synthetic-connection hack would muddy
/// provider code — ADR-0031 §"Trigger cardinality".</para>
///
/// <para>Concrete providers land in subsequent phases of ADR-0031:
/// OFX / QFX (Phase 4), generic CSV with per-institution mapping
/// (Phase 5), per-institution custom CSV providers (Phase 6, e.g.
/// <c>BrokerageCsvProvider</c>).</para>
///
/// <para>The shape of <see cref="FileResult"/> resolves with
/// ADR-0031 §D1; this scaffold leaves it minimal.</para>
/// </remarks>
public interface IFileProvider
{
    /// <summary>
    /// Stable identifier used by the orchestrator to dispatch a
    /// file upload to the right provider. Examples:
    /// <c>"ofx"</c>, <c>"qfx"</c>, <c>"csv-generic"</c>,
    /// <c>"csv-brokerage-a"</c>.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Parse a single uploaded file. <paramref name="payload"/> is
    /// the raw byte stream the user uploaded (or fetched from S3
    /// once direct-upload lands); the provider is responsible for
    /// any format-specific decoding (charset detection, line
    /// endings, etc.).
    ///
    /// Parse / validation errors map to
    /// <see cref="FileResult.Errors"/> when partial recovery is
    /// possible; thrown exceptions are reserved for unrecoverable
    /// faults the orchestrator should surface as a failed
    /// <c>sync_runs</c> row.
    /// </summary>
    Task<FileResult> ParseAsync(
        Stream payload,
        FileIngestContext context,
        CancellationToken cancellationToken);
}
