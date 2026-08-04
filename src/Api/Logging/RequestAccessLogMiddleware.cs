using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Coffer.Api.Errors;

namespace Coffer.Api.Logging;

/// <summary>
/// One Information line per request — method, path, status, duration — covering the
/// HTTP API and the <c>/mcp</c> transport alike (ADR-0086). Framework request
/// logging stays at <c>Warning</c> to avoid Kestrel/routing spam; this is the
/// single access line we own. Registered just after
/// <see cref="RequestScopeMiddleware"/>, so each line carries the request's
/// <c>traceId</c> scope. Logged in a <c>finally</c> so a thrown/aborted request is
/// still recorded (with whatever status the pipeline produced).
///
/// When a request ends in a business rejection, <c>BusinessError.Problem</c> stamps
/// the stable business <c>code</c> into <see cref="HttpContext.Items"/> as the
/// result executes; the access line then reports *why* it failed (e.g.
/// <c>-&gt; 422 (ledger-not-visible)</c>), not just the bare status — the
/// per-endpoint business-outcome logging ADR-0086 deferred.
/// </summary>
public sealed class RequestAccessLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestAccessLogMiddleware> _logger;

    public RequestAccessLogMiddleware(RequestDelegate next, ILogger<RequestAccessLogMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            var businessCode = context.Items.TryGetValue(BusinessError.CodeItemKey, out var raw)
                ? raw as string
                : null;

            if (businessCode is null)
            {
                _logger.LogInformation(
                    "{Method} {Path} -> {StatusCode} in {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogInformation(
                    "{Method} {Path} -> {StatusCode} ({BusinessCode}) in {ElapsedMs}ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    businessCode,
                    sw.ElapsedMilliseconds);
            }
        }
    }
}
