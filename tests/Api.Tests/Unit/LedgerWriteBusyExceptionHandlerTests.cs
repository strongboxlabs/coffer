using System.Text.Json;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

using Coffer.Api.Db;
using Coffer.Api.Errors;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// The ledger write lock's busy refusal is a business refusal: a 422 with
/// <c>ledger-write-busy</c> in the problem shape every 422 has, whether it comes from the
/// helper (<see cref="LedgerWriteBusyException"/>) or from a statement that took the lock
/// itself (SQLSTATE LW001), and however it is wrapped.
/// </summary>
public sealed class LedgerWriteBusyExceptionHandlerTests
{
    private static ServiceProvider Services() =>
        new ServiceCollection().AddLogging().AddOptions().AddCofferProblemDetails().BuildServiceProvider();

    private static DefaultHttpContext Request(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Path = "/api/ledgers/l/transactions";
        http.Request.Headers.Accept = "application/json";
        http.Response.Body = new MemoryStream();
        return http;
    }

    public static TheoryData<string> Refusals => new() { "helper", "statement" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task The_busy_refusal_is_answered_as_a_422_with_its_code(string from)
    {
        await using var services = Services();
        var handler = services.GetServices<IExceptionHandler>().OfType<LedgerWriteBusyExceptionHandler>().Single();
        Exception refusal = from == "helper"
            ? new LedgerWriteBusyException(Guid.NewGuid(), new InvalidOperationException("held"))
            : new InvalidOperationException("wrapped",
                new PostgresException("ledger is being written", "ERROR", "ERROR", LedgerWriteLock.BusySqlState));
        var http = Request(services);

        Assert.True(await handler.TryHandleAsync(http, refusal, CancellationToken.None));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, http.Response.StatusCode);
        Assert.Equal(BusinessError.Codes.LedgerWriteBusy, http.Items[BusinessError.CodeItemKey]);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);
        var problem = body.RootElement;
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Equal("Unprocessable request", problem.GetProperty("title").GetString());
        Assert.Equal(LedgerWriteLock.BusyDetail, problem.GetProperty("detail").GetString());
        Assert.Equal(BusinessError.Codes.LedgerWriteBusy, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _), "the problem lacks the traceId every problem carries");
    }

    [Fact]
    public async Task Any_other_exception_is_left_to_the_next_handler()
    {
        await using var services = Services();
        var handler = services.GetServices<IExceptionHandler>().OfType<LedgerWriteBusyExceptionHandler>().Single();
        var http = Request(services);

        Assert.False(await handler.TryHandleAsync(http,
            new InvalidOperationException("other", new PostgresException("deadlock", "ERROR", "ERROR", "40P01")),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, http.Response.Body.Length);
    }
}
