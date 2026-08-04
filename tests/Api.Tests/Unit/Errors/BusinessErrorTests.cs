using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Coffer.Api.Errors;

namespace Coffer.Api.Tests.Unit.Errors;

/// <summary>
/// Pure checks on <see cref="BusinessError.Problem"/>. Beyond the 422 + stable
/// <c>code</c> envelope, the result must stamp its code into
/// <see cref="HttpContext.Items"/> as it executes, so
/// <c>RequestAccessLogMiddleware</c> can report the business outcome on the access
/// line (the per-endpoint outcome logging ADR-0086 deferred). Wrapping the
/// ProblemDetails result must not change the wire response.
/// </summary>
public sealed class BusinessErrorTests
{
    private static DefaultHttpContext NewContext(out MemoryStream body)
    {
        body = new MemoryStream();
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Response.Body = body;
        return ctx;
    }

    [Fact]
    public async Task Problem_stamps_the_code_for_access_logging_and_preserves_the_422_envelope()
    {
        var ctx = NewContext(out var body);

        var result = BusinessError.Problem(BusinessError.Codes.LedgerNotVisible, "not visible to this user");
        await result.ExecuteAsync(ctx);

        // Access-log side channel: the code is recorded under the shared key.
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, ctx.Items[BusinessError.CodeItemKey]);

        // Wire response is unchanged by the wrapper — 422 + code + detail.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(BusinessError.Codes.LedgerNotVisible, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("not visible to this user", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Problem_carries_an_overridden_title()
    {
        var ctx = NewContext(out var body);

        var result = BusinessError.Problem(
            BusinessError.Codes.SnapshotManualAtCap, "at the manual cap", title: "Snapshot limit reached");
        await result.ExecuteAsync(ctx);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal("Snapshot limit reached", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(BusinessError.Codes.SnapshotManualAtCap, ctx.Items[BusinessError.CodeItemKey]);
    }
}
