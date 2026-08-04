using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Coffer.Api.Errors;

/// <summary>
/// RFC 9457 (the ProblemDetails update to RFC 7807) error envelope. Every
/// non-success response — bound model errors, authorisation failures,
/// unhandled exceptions — comes back as <c>application/problem+json</c> so
/// clients can rely on one shape. The user-pinned API engineering standards
/// require this from PR 3.1 onward.
/// </summary>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Register the in-box <see cref="Microsoft.AspNetCore.Http.ProblemDetailsServiceCollectionExtensions.AddProblemDetails(IServiceCollection)"/>
    /// pipeline with two customisations:
    ///   1. <c>traceId</c> is always populated from
    ///      <see cref="HttpContext.TraceIdentifier"/> so log correlation
    ///      works even when distributed tracing isn't on yet.
    ///   2. <c>instance</c> is the request path so a problem document is
    ///      self-locating in logs.
    /// </summary>
    public static IServiceCollection AddCofferProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var http = context.HttpContext;
                context.ProblemDetails.Instance ??= http.Request.Path.Value;
                context.ProblemDetails.Extensions["traceId"] = http.TraceIdentifier;
            };
        });
        return services;
    }

    /// <summary>
    /// Wire the ProblemDetails-aware exception handler and the status-code
    /// page middleware. Order matters: the exception handler must run before
    /// any endpoint, the status-code pages just after so 401/403/404 etc.
    /// also come back as ProblemDetails JSON.
    /// </summary>
    public static IApplicationBuilder UseCofferProblemDetails(this WebApplication app)
    {
        // The default ASP.NET developer exception page is HTML and would
        // mask the JSON envelope these endpoints contract for; route every
        // unhandled exception through the ProblemDetails-aware handler in
        // every environment.
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
