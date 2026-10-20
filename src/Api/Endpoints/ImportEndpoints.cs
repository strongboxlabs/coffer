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
        HttpRequest request, IMoneydanceImportService service, ImportMemoryGate gate,
        Microsoft.Extensions.Options.IOptions<Configuration.ApiOptions> options, CancellationToken cancellationToken)
    {
        // One parsed export in memory at a time, taken before the upload is read (FU-092).
        using var slot = gate.TryEnter();
        if (slot is null) return Busy();

        var (file, _, error) = await ReadUploadAsync(
                request, requireLedgerName: false, options.Value.Import.MaxUploadBytes, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null) return error;

        var parseError = TryParse(file!, out var export);
        if (parseError is not null) return parseError;

        // Preview is entirely inline, so this scope owns the parsed document and
        // must release it — a parsed export costs several times its file size. ToPreviewDto
        // materializes its counts, so the DTO outlives the export safely.
        using (export)
        {
            return Results.Ok(ToPreviewDto(service.Preview(export!)));
        }
    }

    /// <summary>Create the named new ledger and kick off the background import.</summary>
    private static async Task<IResult> StartAsync(
        HttpRequest request, ICurrentUserAccessor currentUser, ImportJobRunner runner, ImportMemoryGate gate,
        Microsoft.Extensions.Options.IOptions<Configuration.ApiOptions> options, CancellationToken cancellationToken)
    {
        var slot = gate.TryEnter();
        if (slot is null) return Busy();
        try
        {
            var (file, ledgerName, error) = await ReadUploadAsync(
                    request, requireLedgerName: true, options.Value.Import.MaxUploadBytes, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null) return error;

            var parseError = TryParse(file!, out var export);
            if (parseError is not null) return parseError;

            // Ownership of the parsed export, and of the memory slot it holds, passes to
            // the runner here — including on the rejected path, where the runner releases
            // both. Do NOT wrap this in a using: the import is fire-and-forget and outlives
            // this request, so disposing here would tear the document out from under it.
            var owned = slot;
            slot = null;
            var jobId = runner.Start(currentUser.UserId, ledgerName!, export!, owned);
            if (jobId is null)
                return BusinessError.Problem(BusinessError.Codes.ImportAlreadyRunning,
                    "An import is already running. Wait for it to finish before starting another.");

            return Results.Ok(new ImportJobResponse(
                jobId.Value.ToString(), "running", 0, MoneydanceImportService.PipelineStepCount,
                Step: null, LedgerId: null, Error: null));
        }
        finally
        {
            slot?.Dispose();
        }
    }

    private static IResult Busy() =>
        BusinessError.Problem(BusinessError.Codes.ImportAlreadyRunning,
            "Another Moneydance import is being read or run. Exports are imported one at a time, "
            + "because each needs about six and a half times its file size in memory; try again "
            + "when it has finished.");

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
        HttpRequest request, bool requireLedgerName, long maxUploadBytes, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (null, null, BusinessError.Problem(BusinessError.Codes.ImportInvalid,
                "Send multipart/form-data with a 'file' field."));

        // A Moneydance export can be tens of MB, past Kestrel's 30 MB default, so the cap is
        // this import's own (FU-092): it used to be lifted to nothing, leaving only the 4 GiB
        // multipart ceiling. A megabyte over the file for the rest of the form.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxUploadBytes + 1024 * 1024;

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
            when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, null, TooLarge(maxUploadBytes));
        }
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return (null, null, BusinessError.Problem(BusinessError.Codes.ImportFileRequired,
                "Upload a non-empty Moneydance export in the 'file' field."));
        if (file.Length > maxUploadBytes)
            return (null, null, TooLarge(maxUploadBytes));

        // Whether this server can hold the parse, asked before it is attempted rather than
        // learned from an OutOfMemoryException that may land in another request.
        var budget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (EstimatedParseBytes(file.Length) > budget)
            return (null, null, NotEnoughMemory(file.Length, budget));

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
        catch (OutOfMemoryException)
        {
            // Not a recovery strategy — a translation. A held export costs roughly
            // 6.5x the file size (measured on a large real export), and when
            // that exceeded the container the user got the generic "An error occurred
            // while processing your request" from the global handler, with the actual
            // cause visible only as one log line inside a container. Whatever else is
            // true, an operator needs to be told which knob to turn. Nothing is left
            // half-written: the parse either produced a document or it did not.
            return NotEnoughMemory(file.Length, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
    }

    /// <summary>What a parsed export holds: about 6.5 times the file (measured on a large
    /// real export).</summary>
    internal static long EstimatedParseBytes(long fileBytes) => fileBytes * 13 / 2;

    private static IResult NotEnoughMemory(long fileBytes, long budget) =>
        BusinessError.Problem(BusinessError.Codes.ImportParseFailed,
            $"This export ({fileBytes / 1048576} MB) needs about "
            + $"{EstimatedParseBytes(fileBytes) / 1048576} MB of memory to import, and this "
            + $"server has {budget / 1048576} MB in total. Raise the API container's "
            + "memory limit (COFFER_API_MEM_LIMIT) and retry.");

    private static IResult TooLarge(long maxUploadBytes) =>
        BusinessError.Problem(BusinessError.Codes.ImportTooLarge,
            $"This export is over the {maxUploadBytes / 1048576} MB an import accepts. An operator "
            + "can raise the limit with COFFER_IMPORT_MAX_UPLOAD_MB.");

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
