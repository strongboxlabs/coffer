using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

using Coffer.Api.Db;

namespace Coffer.Api.Errors;

/// <summary>
/// Answers the ledger write lock's busy refusal as a 422 business refusal with
/// <see cref="BusinessError.Codes.LedgerWriteBusy"/>, in the shape
/// <see cref="BusinessError.Problem"/> gives, rather than as the 500 an unhandled exception
/// would be.
/// </summary>
/// <remarks>It recognises <see cref="LedgerWriteBusyException"/> from
/// <see cref="LedgerWriteLock"/>, and SQLSTATE <see cref="LedgerWriteLock.BusySqlState"/>
/// from a statement that took the lock itself, wherever either is wrapped
/// (<see cref="LedgerWriteLock.IsBusy"/>).</remarks>
public sealed class LedgerWriteBusyExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (!LedgerWriteLock.IsBusy(exception)) return false;
        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        httpContext.Items[BusinessError.CodeItemKey] = BusinessError.Codes.LedgerWriteBusy;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Unprocessable request",
                Detail = LedgerWriteLock.BusyDetail,
                Extensions = { ["code"] = BusinessError.Codes.LedgerWriteBusy },
            },
            Exception = exception,
        }).ConfigureAwait(false);
    }
}
