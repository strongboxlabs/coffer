using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Coffer.Api.Auth;
using Coffer.Api.Contracts;
using Coffer.Api.Db.Repositories;
using Coffer.Api.Errors;
using Coffer.Api.Ingest;

namespace Coffer.Api.Endpoints;

/// <summary>
/// Provider-neutral per-ledger file-upload ingest (ADR-0031 Phase 4
/// for OFX/QFX; ADR-0042 for QIF). A single
/// <see cref="MapFileIngestEndpoints"/> helper registers the
/// <c>preview</c> + <c>import</c> pair for one provider; the OFX and
/// QIF entry points are thin calls into it. The only per-provider
/// variation is the route segment, the provider-key constant, and the
/// error-code prefix — all passed as arguments — so the observable
/// routes + error codes are byte-identical to the former
/// per-provider endpoint classes.
/// </summary>
/// <remarks>
/// <para>Two endpoints: <c>preview</c> parses the uploaded file and
/// returns its discovered account blocks (no DB writes); <c>import</c>
/// re-uploads the file with a confirmed mapping (one Coffer account ↔
/// one provider account) and runs the orchestrator's file path.
/// Multi-account files require N calls to <c>import</c> — one per
/// mapping the user confirms — which keeps each call atomic.</para>
/// </remarks>
public static class FileIngestEndpoints
{
    /// <summary>Upper bound on accepted upload size. Real-world QFX
    /// statements run a few hundred KB; a 5 MB cap leaves plenty of
    /// headroom for multi-year exports while bounding memory + parse
    /// cost. Larger files are almost certainly mis-uploads.</summary>
    public const long MaxUploadBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Register <c>POST /api/ledgers/{ledgerId}/ingest/{routeSegment}/preview</c>
    /// + <c>/import</c> for one file provider.
    /// </summary>
    /// <param name="routes">The route builder.</param>
    /// <param name="providerKey">The <see cref="IFileProvider.ProviderKey"/>
    /// the orchestrator dispatches on (e.g. <c>"ofx"</c>,
    /// <c>"qif"</c>).</param>
    /// <param name="routeSegment">URL segment under
    /// <c>/ingest/</c> (e.g. <c>"ofx"</c>, <c>"qif"</c>).</param>
    /// <param name="errorPrefix">Prefix for this provider's business
    /// error codes (e.g. <c>"ofx"</c> → <c>ofx_file_required</c>,
    /// <c>ofx_parse_failed</c>, <c>ofx_provider_account_required</c>).</param>
    public static IEndpointRouteBuilder MapFileIngestEndpoints(
        this IEndpointRouteBuilder routes,
        string providerKey,
        string routeSegment,
        string errorPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerKey);
        ArgumentException.ThrowIfNullOrEmpty(routeSegment);
        ArgumentException.ThrowIfNullOrEmpty(errorPrefix);

        var group = routes.MapGroup($"/api/ledgers/{{ledgerId:guid}}/ingest/{routeSegment}")
                          .RequireAuthorization()
                          .DisableAntiforgery()   // multipart upload; auth via session cookie
                          .RequireLedgerAccess();

        group.MapPost("/preview",
                (Guid ledgerId,
                 IFormFile? file,
                 ICurrentUserAccessor currentUser,
                 LedgersRepository ledgers,
                 IngestOrchestrator orchestrator,
                 CancellationToken cancellationToken) =>
                    PreviewAsync(ledgerId, file, currentUser, ledgers, orchestrator,
                                 providerKey, errorPrefix, cancellationToken))
             .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

        group.MapPost("/import",
                (Guid ledgerId,
                 IFormFile? file,
                 [FromForm] Guid accountId,
                 [FromForm] string providerAccountId,
                 ICurrentUserAccessor currentUser,
                 LedgersRepository ledgers,
                 AccountsRepository accounts,
                 IngestOrchestrator orchestrator,
                 CancellationToken cancellationToken) =>
                    ImportAsync(ledgerId, file, accountId, providerAccountId, currentUser,
                                ledgers, accounts, orchestrator,
                                providerKey, errorPrefix, cancellationToken))
             .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

        return routes;
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/ingest/{segment}/preview</c>.
    /// Parse the uploaded file and return its discovered account
    /// blocks. No DB writes.
    /// </summary>
    private static async Task<IResult> PreviewAsync(
        Guid ledgerId,
        IFormFile? file,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        IngestOrchestrator orchestrator,
        string providerKey,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");
        if (file is null || file.Length == 0)
            return BusinessError.Problem($"{errorPrefix}_file_required",
                $"Upload a non-empty {errorPrefix.ToUpperInvariant()} file in the `file` form field.");

        await using var stream = file.OpenReadStream();
        Coffer.Api.Ingest.FileResult parsed;
        try
        {
            // Preview takes a placeholder context — preview never
            // writes, so AccountId / TriggeredByUserId aren't read.
            var context = new FileIngestContext(
                LedgerId: ledgerId,
                AccountId: Guid.Empty,
                TriggeredByUserId: currentUser.UserId);
            parsed = await orchestrator.PreviewFileAsync(
                providerKey, stream, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BusinessError.Problem($"{errorPrefix}_parse_failed", ex.Message);
        }

        return Results.Ok(new FileIngestPreviewResponse(
            Accounts: parsed.DiscoveredAccounts
                .Select(a => new FileIngestAccountDto(
                    a.ProviderAccountId, a.AccountType, a.Currency, a.TransactionCount))
                .ToList(),
            Errors: parsed.Errors
                .Select(e => new FileIngestErrorDto(e.Code, e.Message))
                .ToList()));
    }

    /// <summary>
    /// <c>POST /api/ledgers/{ledgerId}/ingest/{segment}/import</c>.
    /// Re-upload the file plus a confirmed mapping
    /// (<c>accountId</c> + <c>providerAccountId</c>) and run the
    /// import. Returns the same shape as a SimpleFIN sync result so
    /// the SPA's existing summary panel can render it. To import N
    /// accounts from one file, call this endpoint N times.
    /// </summary>
    private static async Task<IResult> ImportAsync(
        Guid ledgerId,
        IFormFile? file,
        Guid accountId,
        string providerAccountId,
        ICurrentUserAccessor currentUser,
        LedgersRepository ledgers,
        AccountsRepository accounts,
        IngestOrchestrator orchestrator,
        string providerKey,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        var visible = await ledgers.GetVisibleByIdAsync(
            currentUser.UserId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (visible is null)
            return BusinessError.Problem(BusinessError.Codes.LedgerNotVisible,
                "Ledger not found or not visible to this user.");
        if (file is null || file.Length == 0)
            return BusinessError.Problem($"{errorPrefix}_file_required",
                $"Upload a non-empty {errorPrefix.ToUpperInvariant()} file in the `file` form field.");
        if (accountId == Guid.Empty)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "accountId form field is required.");
        if (string.IsNullOrWhiteSpace(providerAccountId))
            return BusinessError.Problem($"{errorPrefix}_provider_account_required",
                "providerAccountId form field is required — copy the value from the preview response.");

        var belongs = await accounts.BelongsToLedgerAsync(
            ledgerId, accountId, cancellationToken).ConfigureAwait(false);
        if (!belongs)
            return BusinessError.Problem(BusinessError.Codes.AccountNotInLedger,
                "Account does not belong to this ledger.");

        await using var stream = file.OpenReadStream();
        IngestRunOutcome outcome;
        try
        {
            var context = new FileIngestContext(
                LedgerId: ledgerId,
                AccountId: accountId,
                TriggeredByUserId: currentUser.UserId,
                ProviderAccountId: providerAccountId);
            outcome = await orchestrator.RunFileAsync(
                providerKey, stream, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BusinessError.Problem($"{errorPrefix}_parse_failed", ex.Message);
        }

        return Results.Ok(new FileIngestImportResponse(
            SyncRunId: outcome.SyncRunId,
            AccountsDiscovered: outcome.AccountsDiscovered,
            TransactionsForReview: outcome.TransactionsForReview,
            AlreadyKnown: outcome.AlreadyKnown,
            Errors: outcome.Errors
                .Select(e => new FileIngestErrorDto(e.Code, e.Message))
                .ToList()));
    }
}
