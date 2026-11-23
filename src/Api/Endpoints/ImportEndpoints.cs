using System.Text.Json;
using Coffer.Api.Auth;
using Coffer.Api.Errors;
using Coffer.Api.Import;
using Coffer.Api.Quotes;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Microsoft.AspNetCore.Http.Features;

namespace Coffer.Api.Endpoints;

/// <summary>
/// In-app Moneydance import (ADR-0071 D2). Any authenticated user can upload an
/// MD export and create a brand-new ledger from it — new-ledger-only, so each
/// ledger is seeded once (ADR-0052 D2); the name is a label, never a lookup. The
/// import runs as a background job
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

        var (file, _, chosenCurrency, error) = await ReadUploadAsync(
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
            // Checked against the reporting currency sent with it, else the file's base.
            var preview = service.Preview(export!, chosenCurrency);
            return CurrencyRefusal(preview) ?? Results.Ok(ToPreviewDto(preview));
        }
    }

    /// <summary>Create the named new ledger and kick off the background import.</summary>
    private static async Task<IResult> StartAsync(
        HttpRequest request, ICurrentUserAccessor currentUser, ImportJobRunner runner, ImportMemoryGate gate,
        IMoneydanceImportService service,
        Microsoft.Extensions.Options.IOptions<Configuration.ApiOptions> options, CancellationToken cancellationToken)
    {
        var slot = gate.TryEnter();
        if (slot is null) return Busy();
        try
        {
            var (file, ledgerName, chosenCurrency, error) = await ReadUploadAsync(
                    request, requireLedgerName: true, options.Value.Import.MaxUploadBytes, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null) return error;

            var parseError = TryParse(file!, out var export);
            if (parseError is not null) return parseError;

            // The same currency check as the preview, since a start need not follow one,
            // against the reporting currency the ledger will take: a category is stamped
            // with it (ADR-0114 D7). A refused export was never handed on, so this scope
            // still owns it.
            var preview = service.Preview(export!, chosenCurrency);
            if (CurrencyRefusal(preview) is { } refusal)
            {
                export!.Dispose();
                return refusal;
            }
            // No currency chosen: the file's base currency, the wizard's own default. The
            // check above guarantees the file has one.
            var currencyCode = chosenCurrency ?? preview.BaseCurrencyCode!;

            // Ownership of the parsed export, and of the memory slot it holds, passes to
            // the runner here — including on the rejected path, where the runner releases
            // both. Do NOT wrap this in a using: the import is fire-and-forget and outlives
            // this request, so disposing here would tear the document out from under it.
            var owned = slot;
            slot = null;
            var jobId = runner.Start(currentUser.UserId, ledgerName!, export!, owned, currencyCode);
            if (jobId is null)
                return BusinessError.Problem(BusinessError.Codes.ImportAlreadyRunning,
                    "An import is already running. Wait for it to finish before starting another.");

            return Results.Ok(new ImportJobResponse(
                jobId.Value.ToString(), "running", 0, MoneydanceImportService.PipelineStepCount,
                Step: null, LedgerId: null, Error: null,
                Warnings: [], SkippedTransactions: [], SkippedTransactionsNotListed: 0));
        }
        finally
        {
            slot?.Dispose();
        }
    }

    /// <summary>The refusal while another export holds the memory gate. Shared with the
    /// Demo, which is parsed under the same gate (<see cref="DemoLedgerEndpoints"/>).</summary>
    internal static IResult Busy() =>
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

    /// <summary>
    /// Read the multipart upload: the export in <c>file</c>, the new ledger's name in
    /// <c>ledgerName</c>, and its reporting currency in <c>currencyCode</c> (ADR-0114 D7),
    /// which the wizard asks for on its preview step and sends with the start. A blank
    /// or absent currency comes back null: the start then takes the file's base currency,
    /// the default the preview reports. A code that is not one a ledger can take (ADR-0114 D15) is
    /// <c>ledger-currency-invalid</c>, on the preview as on the start, so the wizard can
    /// say so before the person names the ledger.
    /// </summary>
    private static async Task<(IFormFile? File, string? LedgerName, string? CurrencyCode, IResult? Error)> ReadUploadAsync(
        HttpRequest request, bool requireLedgerName, long maxUploadBytes, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return (null, null, null, BusinessError.Problem(BusinessError.Codes.ImportInvalid,
                "Send multipart/form-data with a 'file' field."));

        // A Moneydance export can be tens of MB, past Kestrel's 30 MB default, so the cap is
        // this import's own (FU-092) rather than lifted to nothing, which would leave only the
        // 4 GiB multipart ceiling. A megabyte over the file for the rest of the form.
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
            return (null, null, null, TooLarge(maxUploadBytes));
        }
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return (null, null, null, BusinessError.Problem(BusinessError.Codes.ImportFileRequired,
                "Upload a non-empty Moneydance export in the 'file' field."));
        if (file.Length > maxUploadBytes)
            return (null, null, null, TooLarge(maxUploadBytes));

        // Whether this server can hold the parse, asked before it is attempted rather than
        // learned from an OutOfMemoryException that may land in another request.
        var budget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (EstimatedParseBytes(file.Length) > budget)
            return (null, null, null, NotEnoughMemory(file.Length, budget));

        var ledgerName = form["ledgerName"].ToString().Trim();
        if (requireLedgerName && string.IsNullOrWhiteSpace(ledgerName))
            return (null, null, null, BusinessError.Problem(BusinessError.Codes.LedgerNameRequired,
                "A name for the new ledger is required."));

        var rawCurrency = form["currencyCode"].ToString();
        if (string.IsNullOrWhiteSpace(rawCurrency))
            return (file, ledgerName, null, null);
        // The ledger's reporting currency, as create-ledger takes it: one a ledger can take
        // (ADR-0114 D15).
        if (!CurrencyCodes.TryNormalize(rawCurrency, out var currencyCode)
            || !Coffer.Domain.Money.CurrencyMinorUnits.IsAcceptedAccountCurrency(currencyCode))
            return (null, null, null, LedgersEndpoints.LedgerCurrencyInvalid());

        return (file, ledgerName, currencyCode, null);
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
            // Not a recovery strategy — a translation. The pre-parse check above uses
            // an estimate (EstimatedParseBytes), so a parse can still run out of
            // memory; without this catch the user would get the generic "An error
            // occurred while processing your request" from the global handler, with
            // the actual cause visible only as one log line inside a container. Whatever else is
            // true, an operator needs to be told which knob to turn. Nothing is left
            // half-written: the parse either produced a document or it did not.
            return NotEnoughMemory(file.Length, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
    }

    /// <summary>The memory an export is assumed to need once parsed: the file size times a
    /// fixed safety factor of 6.5, covering the parsed document plus each item's field
    /// dictionary.</summary>
    /// <remarks>An estimate, not a bound: it decides only whether a parse is attempted
    /// against <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>. The factor is a code
    /// constant, not a setting; what an operator can change is the memory the API has
    /// (<c>COFFER_API_MEM_LIMIT</c>) and the largest upload accepted
    /// (<c>COFFER_IMPORT_MAX_UPLOAD_MB</c>).</remarks>
    internal static long EstimatedParseBytes(long fileBytes) => fileBytes * 13 / 2;

    private static IResult NotEnoughMemory(long fileBytes, long budget) =>
        BusinessError.Problem(BusinessError.Codes.ImportParseFailed, NotEnoughMemoryDetail(fileBytes, budget));

    /// <summary>Why an export of <paramref name="fileBytes"/> is not parsed on a server
    /// with <paramref name="budget"/> bytes. Shared with the Demo's own refusal.</summary>
    internal static string NotEnoughMemoryDetail(long fileBytes, long budget) =>
        $"This export ({fileBytes / 1048576} MB) needs about "
        + $"{EstimatedParseBytes(fileBytes) / 1048576} MB of memory to import, and this "
        + $"server has {budget / 1048576} MB in total. Raise the API container's "
        + "memory limit (COFFER_API_MEM_LIMIT) and retry.";

    private static IResult TooLarge(long maxUploadBytes) =>
        BusinessError.Problem(BusinessError.Codes.ImportTooLarge,
            $"This export is over the {maxUploadBytes / 1048576} MB an import accepts. An operator "
            + "can raise the limit with COFFER_IMPORT_MAX_UPLOAD_MB.");

    /// <summary>How many of an export's currency problems the refusal lists by name; the
    /// rest are counted. A file with a broken currency table could name every account.</summary>
    private const int CurrencyProblemsListed = 10;

    /// <summary>
    /// <c>import-currency-unresolved</c> when the export's currencies cannot be imported
    /// (ADR-0114): every account takes its own Moneydance currency and no currency is
    /// defaulted, so the file is refused, naming the accounts. Null when it can be.
    /// </summary>
    private static IResult? CurrencyRefusal(MoneydanceImportPreview preview)
    {
        var problems = preview.CurrencyProblems;
        if (problems.Count == 0) return null;
        var listed = string.Join(" ", problems.Take(CurrencyProblemsListed));
        var more = problems.Count > CurrencyProblemsListed
            ? $" {problems.Count - CurrencyProblemsListed} more like these are not listed."
            : string.Empty;
        return BusinessError.Problem(BusinessError.Codes.ImportCurrencyUnresolved,
            "This export cannot be imported, because Coffer cannot tell which currency these "
            + "are in: " + listed + more);
    }

    private static ImportPreviewResponse ToPreviewDto(MoneydanceImportPreview p) =>
        new(p.Exporter, p.MoneydanceBuild, p.ExportDate, p.TotalItems,
            p.ObjTypeCounts.Select(c => new ImportPreviewCount(c.ObjType, c.Count)).ToList(),
            p.BaseCurrencyCode!);

    internal static ImportJobResponse ToJobDto(ImportJob j) =>
        new(j.Id.ToString(),
            j.State switch
            {
                ImportJobState.Running => "running",
                ImportJobState.Succeeded => "succeeded",
                _ => "failed",
            },
            j.Completed, j.Total, j.Step, j.LedgerId?.ToString(), j.Error,
            j.Warnings, j.SkippedTransactions, j.SkippedTransactionsNotListed);

    /// <summary>The preview. <see cref="BaseCurrencyCode"/> is the file's base currency
    /// (ADR-0114 D7), the wizard's default for the reporting currency; a preview is only
    /// returned when the file has one.</summary>
    private sealed record ImportPreviewResponse(
        string Exporter, long Build, long ExportDate, int TotalItems, IReadOnlyList<ImportPreviewCount> Counts,
        string BaseCurrencyCode);
    private sealed record ImportPreviewCount(string ObjType, int Count);

    /// <summary>A job's status. <see cref="Warnings"/>, <see cref="SkippedTransactions"/> and
    /// <see cref="SkippedTransactionsNotListed"/> are filled once the job succeeds
    /// (ImportJobOutcome) and empty before then and on failure.</summary>
    internal sealed record ImportJobResponse(
        string JobId, string State, int Completed, int Total, string? Step, string? LedgerId, string? Error,
        IReadOnlyList<ImportWarning> Warnings,
        IReadOnlyList<ImportSkippedTransaction> SkippedTransactions,
        int SkippedTransactionsNotListed);
}
