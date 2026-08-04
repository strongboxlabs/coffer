using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Coffer.Api.Logging;

/// <summary>
/// Adds <c>traceId</c> and (when authenticated) <c>userId</c> to the logger
/// scope so every log line emitted during a request is correlatable.
/// Combined with the <see cref="Errors.ProblemDetailsExtensions"/>'s
/// <c>traceId</c> extension on error responses, the ID round-trips between
/// client and log without any tracing infrastructure.
/// </summary>
public sealed class RequestScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestScopeMiddleware> _logger;

    public RequestScopeMiddleware(RequestDelegate next, ILogger<RequestScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var scope = new Dictionary<string, object?>
        {
            ["traceId"] = context.TraceIdentifier,
        };

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userId))
            scope["userId"] = userId;

        using (_logger.BeginScope(scope))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
