using System.Net;
using System.Text.Json;

using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration;

/// <summary>
/// Pins the API's contract that every non-success response is an RFC 9457
/// <c>application/problem+json</c> envelope with at minimum a
/// <c>status</c> matching the HTTP status code and a <c>traceId</c> for log
/// correlation.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProblemDetailsTests
{
    private readonly PostgresFixture _postgres;

    public ProblemDetailsTests(PostgresFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Unmapped_route_returns_problem_details_404()
    {
        await using var factory = new ApiFactory(_postgres);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/no-such-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal("application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(
            doc.RootElement.GetProperty("traceId").GetString()),
            "traceId extension should be populated by ProblemDetailsExtensions.");
    }
}
