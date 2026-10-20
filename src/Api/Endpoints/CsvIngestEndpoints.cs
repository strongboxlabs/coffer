using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;
using Coffer.Api.Ingest.Csv;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Delimited-file ingest and the mapping documents that describe it (ADR-0031 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// Does NOT use <see cref="FileIngestEndpoints.MapFileIngestEndpoints"/>, whose contract
/// is that the only per-provider variation is three strings. This provider needs a
/// mapping per request, which is a fourth thing — so it maps its own routes and calls
/// the shared handlers, rather than bending a helper whose whole value is that it has no
/// per-provider branches.
/// </para>
/// <para>
/// <b>A mapping may be saved OR a draft.</b> Every upload takes either a
/// <c>mappingId</c> or a <c>mappingYaml</c>. The draft path is what lets the wizard —
/// and an MCP client composing YAML from a few sample lines — try a delimiter without
/// persisting anything first. Requiring a saved row would mean storing half-built
/// mappings just to see whether they work.
/// </para>
/// </remarks>
public static class CsvIngestEndpoints
{
    public static IEndpointRouteBuilder MapCsvIngestEndpoints(this IEndpointRouteBuilder routes)
    {
        var ingest = routes.MapGroup("/api/ledgers/{ledgerId:guid}/ingest/csv")
                           .RequireAuthorization()
                           .DisableAntiforgery()   // multipart upload; auth via session cookie
                           .RequireLedgerAccess();

        ingest.MapPost("/preview", PreviewAsync)
              .WithMetadata(new RequestSizeLimitAttribute(FileIngestEndpoints.MaxUploadBytes));
        ingest.MapPost("/import", ImportAsync)
              .WithMetadata(new RequestSizeLimitAttribute(FileIngestEndpoints.MaxUploadBytes));

        var mappings = routes.MapGroup("/api/ledgers/{ledgerId:guid}/csv-mappings")
                             .RequireAuthorization()
                             .RequireLedgerAccess();

        mappings.MapGet("/", ListMappingsAsync);
        mappings.MapGet("/{id:guid}", GetMappingAsync);
        mappings.MapPost("/", CreateMappingAsync);
        mappings.MapPut("/{id:guid}", UpdateMappingAsync);
        mappings.MapDelete("/{id:guid}", DeleteMappingAsync);
        // Validate without saving. The iteration path for anything composing a document.
        mappings.MapPost("/validate", ValidateMappingAsync);

        return routes;
    }

    // ----- ingest -------------------------------------------------------------

    private static async Task<IResult> PreviewAsync(
        Guid ledgerId,
        IFormFile? file,
        [FromForm] Guid? mappingId,
        [FromForm] string? mappingYaml,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        CsvMappingsRepository mappings,
        IngestOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var (mapping, failure) = await ResolveAsync(
            ledgerId, mappingId, mappingYaml, mappings, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        return await FileIngestEndpoints.PreviewAsync(
            ledgerId, file, currentUser, ledgers, orchestrator,
            CsvGenericFileProvider.Key, "csv", cancellationToken, mapping)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ImportAsync(
        Guid ledgerId,
        IFormFile? file,
        [FromForm] Guid accountId,
        [FromForm] string providerAccountId,
        [FromForm] Guid? mappingId,
        [FromForm] string? mappingYaml,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        CsvMappingsRepository mappings,
        IngestOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var (mapping, failure) = await ResolveAsync(
            ledgerId, mappingId, mappingYaml, mappings, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        return await FileIngestEndpoints.ImportAsync(
            ledgerId, file, accountId, providerAccountId, currentUser, ledgers, accounts,
            orchestrator, CsvGenericFileProvider.Key, "csv", cancellationToken, mapping)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A saved mapping or a draft, validated either way.
    /// </summary>
    /// <remarks>
    /// A SAVED mapping is re-validated on read rather than trusted. It was valid when it
    /// was stored, but `schema_version` exists precisely because that is a statement
    /// about a past validator — and a document that no longer validates must fail here,
    /// naming why, rather than reach the parser and fail once per row.
    /// </remarks>
    private static async Task<(CsvMapping? Mapping, IResult? Failure)> ResolveAsync(
        Guid ledgerId,
        Guid? mappingId,
        string? mappingYaml,
        CsvMappingsRepository mappings,
        CancellationToken cancellationToken)
    {
        var hasDraft = !string.IsNullOrWhiteSpace(mappingYaml);
        if (mappingId is null && !hasDraft)
        {
            return (null, BusinessError.Problem("csv_mapping_required",
                "Pass either mappingId (a saved mapping) or mappingYaml (a draft)."));
        }
        if (mappingId is not null && hasDraft)
        {
            // Refused rather than picking one. Silently preferring either would mean the
            // file was read by a mapping the caller did not choose.
            return (null, BusinessError.Problem("csv_mapping_ambiguous",
                "Pass mappingId or mappingYaml, not both."));
        }

        string yaml;
        if (hasDraft)
        {
            yaml = mappingYaml!;
        }
        else
        {
            var row = await mappings.GetAsync(ledgerId, mappingId!.Value, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                return (null, BusinessError.Problem("csv_mapping_not_found",
                    "No such mapping on this ledger."));
            }
            yaml = row.DefinitionYaml;
        }

        var mapping = CsvMappingsRepository.Validate(yaml, out var errors);
        if (mapping is null)
        {
            return (null, Results.UnprocessableEntity(new CsvMappingValidationResponse(
                Valid: false, Errors: Describe(errors))));
        }
        return (mapping, null);
    }

    // ----- mapping documents --------------------------------------------------

    private static async Task<IResult> ListMappingsAsync(
        Guid ledgerId, CsvMappingsRepository mappings, CancellationToken cancellationToken) =>
        Results.Ok((await mappings.ListAsync(ledgerId, cancellationToken).ConfigureAwait(false))
            .Select(m => new CsvMappingDto(m.Id, m.Name, m.DefinitionYaml, m.SchemaVersion,
                m.CreatedAt, m.UpdatedAt))
            .ToList());

    private static async Task<IResult> GetMappingAsync(
        Guid ledgerId, Guid id, CsvMappingsRepository mappings, CancellationToken cancellationToken)
    {
        var row = await mappings.GetAsync(ledgerId, id, cancellationToken).ConfigureAwait(false);
        return row is null
            ? BusinessError.Problem("csv_mapping_not_found", "No such mapping on this ledger.")
            : Results.Ok(new CsvMappingDto(row.Id, row.Name, row.DefinitionYaml,
                row.SchemaVersion, row.CreatedAt, row.UpdatedAt));
    }

    private static Task<IResult> CreateMappingAsync(
        Guid ledgerId, CsvMappingWriteRequest request,
        CsvMappingsRepository mappings, CancellationToken cancellationToken) =>
        SaveAsync(ledgerId, null, request, mappings, cancellationToken);

    private static Task<IResult> UpdateMappingAsync(
        Guid ledgerId, Guid id, CsvMappingWriteRequest request,
        CsvMappingsRepository mappings, CancellationToken cancellationToken) =>
        SaveAsync(ledgerId, id, request, mappings, cancellationToken);

    private static async Task<IResult> SaveAsync(
        Guid ledgerId, Guid? id, CsvMappingWriteRequest request,
        CsvMappingsRepository mappings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BusinessError.Problem("csv_mapping_name_required", "A mapping needs a name.");

        var result = await mappings
            .SaveAsync(ledgerId, id, request.Name, request.DefinitionYaml ?? "", cancellationToken)
            .ConfigureAwait(false);

        if (result.NameTaken)
            return BusinessError.Problem("csv_mapping_name_taken",
                "Another mapping on this ledger already uses that name.");
        if (result.Errors.Count > 0)
            return Results.UnprocessableEntity(new CsvMappingValidationResponse(
                Valid: false, Errors: Describe(result.Errors)));
        if (result.Saved is null)
            return BusinessError.Problem("csv_mapping_not_found", "No such mapping on this ledger.");

        var saved = result.Saved;
        return Results.Ok(new CsvMappingDto(saved.Id, saved.Name, saved.DefinitionYaml,
            saved.SchemaVersion, saved.CreatedAt, saved.UpdatedAt));
    }

    private static async Task<IResult> DeleteMappingAsync(
        Guid ledgerId, Guid id, CsvMappingsRepository mappings, CancellationToken cancellationToken) =>
        await mappings.DeleteAsync(ledgerId, id, cancellationToken).ConfigureAwait(false)
            ? Results.NoContent()
            : BusinessError.Problem("csv_mapping_not_found", "No such mapping on this ledger.");

    /// <summary>
    /// Check a document without saving it. Always 200 — an invalid document is a valid
    /// question, and the answer is the list of problems.
    /// </summary>
    private static IResult ValidateMappingAsync(Guid ledgerId, CsvMappingWriteRequest request)
    {
        _ = ledgerId;
        var mapping = CsvMappingsRepository.Validate(request.DefinitionYaml ?? "", out var errors);
        return Results.Ok(new CsvMappingValidationResponse(
            Valid: mapping is not null, Errors: Describe(errors), Mapping: Shape(mapping)));
    }

    /// <summary>
    /// What the document says, for a caller that has to render it — null when it says
    /// nothing usable, because half a reading is worse than none.
    /// </summary>
    private static CsvMappingShapeDto? Shape(CsvMapping? mapping) =>
        mapping is null ? null : new CsvMappingShapeDto(
            mapping.Version,
            CsvMappingValidator.NameOf(mapping.Delimiter),
            mapping.HeaderRows,
            mapping.FooterRows,
            mapping.DateColumn,
            mapping.DateFormat,
            mapping.PayeeColumn,
            mapping.MemoColumn,
            CsvMappingValidator.NameOf(mapping.AmountShape),
            mapping.AmountColumn,
            mapping.AmountInvert,
            mapping.DebitColumn,
            mapping.CreditColumn);

    private static IReadOnlyList<CsvMappingErrorDto> Describe(IReadOnlyList<CsvMappingError> errors) =>
        errors.Select(e => new CsvMappingErrorDto(e.Path, e.Line, e.Message)).ToList();
}
