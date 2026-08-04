using System.Text.Json;
using Coffer.Api.Auth;
using Coffer.Api.Errors;
using Coffer.Api.Import;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Microsoft.AspNetCore.Http.Features;

namespace Coffer.Api.Endpoints;

/// <summary>
/// In-app Moneydance import (ADR-0071 D2). Any authenticated user can upload an
/// MD export and create a brand-new ledger from it — new-ledger-only, which also
/// satisfies the ADR-0052 seed-once guard. The import runs as a background job
/// (it's a large, long write); the client previews, starts, then polls status.
/// </summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/imports/moneydance")
                          .RequireAuthorization()      // any authenticated user
                          .DisableAntiforgery();        // multipart upload; auth via session cookie

        group.MapPost("/preview", PreviewAsync);
        group.MapPost("/", StartAsync);
        group.MapGet("/{jobId:guid}", GetStatus);
        return routes;
    }

    /// <summary>Parse the upload and return per-type counts — no DB writes.</summary>
    private static async Task<IResult> PreviewAsync(
        HttpRequest request, IMoneydanceImportService service, CancellationToken cancellationToken)
    {
        var (file, _, error) = await ReadUploadAsync(request, requireLedgerName: false, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        var parseError = TryParse(file!, out var export);
        if (parseError is not null) return parseError;

        return Results.Ok(ToPreviewDto(service.Preview(export!)));
    }

    /// <summary>Create the named new ledger and kick off the background import.</summary>
    private static async Task<IResult> StartAsync(
        HttpRequest request, ICurrentUserAccessor currentUser, ImportJobRunner runner,
        CancellationToken cancellationToken)
    {
        var (file, ledgerName, error) = await ReadUploadAsync(request, requireLedgerName: true, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        var parseError = TryParse(file!, out var export);
        if (parseError is not null) return parseError;

        var jobId = runner.Start(currentUser.UserId, ledgerName!, export!);
        if (jobId is null)
            return BusinessError.Problem(BusinessError.Codes.ImportAlreadyRunning,
                "An import is already running. Wait for it to finish before starting another.");

        return Results.Ok(new ImportJobResponse(
            jobId.Value.ToString(), "running", 0, MoneydanceImportService.PipelineStepCount,
            Step: null, LedgerId: null, Error: null));
    }

    /// <summary>Poll a job's progress. Scoped to the caller's own jobs.</summary>
    private static IResult GetStatus(Guid jobId, ICurrentUserAccessor currentUser, ImportJobRegistry registry)
    {
        var job = registry.Snapshot(jobId);
        if (job is null || job.UserId != currentUser.UserId)
            return Results.NotFound();
        return Results.Ok(ToJobDto(job));
    }

    // ---- helpers ----

    private static async Task<(IFormFile? File, string? LedgerName, IResult? Error)> ReadUploadAsync(
        HttpRequest request, bool requireLedgerName, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (null, null, BusinessError.Problem(BusinessError.Codes.ImportInvalid,
                "Send multipart/form-data with a 'file' field."));

        // A Moneydance export can be tens of MB — lift Kestrel's per-request cap;
        // the multipart length limit (~128 MB) is the practical ceiling for the UI.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return (null, null, BusinessError.Problem(BusinessError.Codes.ImportFileRequired,
                "Upload a non-empty Moneydance export in the 'file' field."));

        var ledgerName = form["ledgerName"].ToString().Trim();
        if (requireLedgerName && string.IsNullOrWhiteSpace(ledgerName))
            return (null, null, BusinessError.Problem(BusinessError.Codes.LedgerNameRequired,
                "A name for the new ledger is required."));

        return (file, ledgerName, null);
    }

    private static IResult? TryParse(IFormFile file, out MdExport? export)
    {
        export = null;
        try
        {
            using var stream = file.OpenReadStream();
            export = MdItemReader.Read(stream);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return BusinessError.Problem(BusinessError.Codes.ImportParseFailed,
                $"Could not read the Moneydance export: {ex.Message}");
        }
    }

    private static ImportPreviewResponse ToPreviewDto(MoneydanceImportPreview p) =>
        new(p.Exporter, p.MoneydanceBuild, p.ExportDate, p.TotalItems,
            p.ObjTypeCounts.Select(c => new ImportPreviewCount(c.ObjType, c.Count)).ToList());

    private static ImportJobResponse ToJobDto(ImportJob j) =>
        new(j.Id.ToString(),
            j.State switch
            {
                ImportJobState.Running => "running",
                ImportJobState.Succeeded => "succeeded",
                _ => "failed",
            },
            j.Completed, j.Total, j.Step, j.LedgerId?.ToString(), j.Error);

    private sealed record ImportPreviewResponse(
        string Exporter, long Build, long ExportDate, int TotalItems, IReadOnlyList<ImportPreviewCount> Counts);
    private sealed record ImportPreviewCount(string ObjType, int Count);
    private sealed record ImportJobResponse(
        string JobId, string State, int Completed, int Total, string? Step, string? LedgerId, string? Error);
}
