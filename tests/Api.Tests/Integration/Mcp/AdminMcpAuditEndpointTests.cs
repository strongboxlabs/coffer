using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Contracts;
using Coffer.Api.Db;
using Coffer.Api.Db.Entities;
using Coffer.Api.Mcp;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Mcp;

/// <summary>
/// The admin MCP write-audit surface (ADR-0081 D5, <c>/api/admin/mcp/audit</c>): the
/// RequireAdmin gate and the cross-user list + clear. Dev-auth is stamped admin, so
/// the default factory client is an admin client; a non-admin cookie is forbidden.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminMcpAuditEndpointTests
{
    private readonly PostgresFixture _fixture;

    public AdminMcpAuditEndpointTests(PostgresFixture fixture) => _fixture = fixture;

    private McpAuditRecorder Recorder() =>
        new(new ServiceDbContextFactory(Options.Create(
            new ApiOptions { ServiceConnectionString = _fixture.ServiceConnectionString })));

    [Fact]
    public async Task Non_admin_cookie_is_forbidden()
    {
        var alice = await SyntheticLedger.CreateAsync(_fixture);   // not an admin
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled().WithoutDevAuth();
        var cookie = await alice.IssueSessionCookieAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"coffer.session={cookie}");

        var resp = await client.GetAsync("/api/admin/mcp/audit");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_lists_then_clears_the_write_audit()
    {
        var ledger = await SyntheticLedger.CreateAsync(_fixture);
        var recorder = Recorder();
        var auditId = await recorder.RecordAttemptAsync(
            ledger.UserId, "set_transaction_tags", arguments: null, traceId: null);
        await recorder.FinalizeAsync(auditId, InvocationStatus.Ok, "tagged 1");

        await using var factory = new ApiFactory(_fixture).WithMcpEnabled();   // dev-auth = admin
        using var client = factory.CreateClient();

        var listed = await client.GetFromJsonAsync<List<McpAuditEntryDto>>("/api/admin/mcp/audit");
        Assert.NotNull(listed);
        Assert.Contains(listed!, e => e.UserId == ledger.UserId && e.ToolName == "set_transaction_tags");

        var cleared = await client.DeleteAsync("/api/admin/mcp/audit");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var after = await client.GetFromJsonAsync<List<McpAuditEntryDto>>("/api/admin/mcp/audit");
        Assert.DoesNotContain(after!, e => e.UserId == ledger.UserId);
    }
}
