using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Coffer.Api.Logging;
using Coffer.Api.Notifications;
using Coffer.Api.Notifications.Providers;
using Coffer.Api.Tests.Integration.Infra;

namespace Coffer.Api.Tests.Integration.Logging;

/// <summary>
/// No credential reaches a log line the default configuration writes (FU-083): not the
/// token in an invite or setup link, which the access log used to write with the raw path,
/// and not the URL of a delivery target, which HttpClient's own logging wrote on every send.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class AccessLogRedactionTests
{
    private readonly PostgresFixture _fixture;

    public AccessLogRedactionTests(PostgresFixture fixture) => _fixture = fixture;

    private const string Secret = "Zq7pV3secretTokenK2";

    private static void AssertNowhere(CapturedLogs logs, string secret) =>
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains(secret, StringComparison.Ordinal));

    [Theory]
    [InlineData("GET", "/api/auth/invite/" + Secret, "/api/auth/invite/{redacted}")]
    [InlineData("POST", "/api/auth/invite/" + Secret + "/begin", "/api/auth/invite/{redacted}/begin")]
    [InlineData("GET", "/api/auth/setup/" + Secret + "/info", "/api/auth/setup/{redacted}/info")]
    [InlineData("GET", "/API/Auth/Invite/" + Secret, "/API/Auth/Invite/{redacted}")]   // routing ignores case
    [InlineData("GET", "/api/oauth/consent/" + Secret, "/api/oauth/consent/{redacted}")]
    [InlineData("GET", "/invite/" + Secret, "/invite/{redacted}")]                      // the SPA page
    [InlineData("GET", "/setup/" + Secret, "/setup/{redacted}")]
    public async Task A_link_token_is_never_written_by_the_access_log(string method, string path, string logged)
    {
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(_fixture)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();

        using var _ = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Contains(logs.Lines, l =>
            l.Category == typeof(RequestAccessLogMiddleware).FullName
            && l.Message.StartsWith($"{method} {logged} -> ", StringComparison.Ordinal));
        AssertNowhere(logs, Secret);
    }

    [Fact]
    public async Task An_identifier_in_a_path_is_kept()
    {
        // What the access log is FOR: which ledger, which account. Redacting by route
        // shape would have cost these too.
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(_fixture)
            .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
        using var client = factory.CreateClient();
        var ledgerId = Guid.NewGuid();

        using var _ = await client.GetAsync($"/api/ledgers/{ledgerId}/accounts");

        Assert.Contains(logs.Lines, l => l.Message.StartsWith(
            $"GET /api/ledgers/{ledgerId}/accounts -> ", StringComparison.Ordinal));
    }

    // --- the catalog: every path parameter is classified -------------------------------

    /// <summary>
    /// Route parameters whose values are not secret, though nothing in their constraint
    /// says so. A new one fails <see cref="Every_api_route_parameter_is_an_identifier_a_declared_non_secret_or_redacted"/>
    /// until it is placed here or under a <see cref="LoggedPath.SecretSegmentPrefixes"/> entry.
    /// </summary>
    private static readonly Dictionary<string, string> NotSecret = new()
    {
        ["id"] = "a backup file's name",
        ["clientId"] = "an OAuth client's public identifier",
        ["catalogKey"] = "a built-in report's key",
        ["projection"] = "a projection's name",
        ["jobType"] = "a scheduled job's type",
        ["documentName"] = "an OpenAPI document's name",
        ["grantId"] = "an OAuth grant's id; ending one needs the session of the user it belongs to",
        ["path"] = "the SPA fallback; its pages are classified by the router catalog below",
    };

    private static readonly string[] IdentifierConstraints = ["guid", "int", "long"];

    [Fact]
    public async Task Every_api_route_parameter_is_an_identifier_a_declared_non_secret_or_redacted()
    {
        await using var factory = new ApiFactory(_fixture).WithMcpEnabled();
        using var _ = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(routes);

        var unclassified = new List<string>();
        var usedPrefixes = new HashSet<string>();
        foreach (var route in routes)
        {
            var literal = new StringBuilder("/");
            foreach (var segment in route.RoutePattern.PathSegments)
            {
                foreach (var parameter in segment.Parts.OfType<RoutePatternParameterPart>())
                {
                    var prefix = literal.ToString();
                    var redacted = segment.Parts.Count == 1
                        && LoggedPath.SecretSegmentPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase);
                    if (redacted) usedPrefixes.Add(prefix.ToLowerInvariant());

                    var identifier = parameter.ParameterPolicies.Any(p =>
                        IdentifierConstraints.Contains(p.Content, StringComparer.OrdinalIgnoreCase));
                    if (!redacted && !identifier && !NotSecret.ContainsKey(parameter.Name))
                        unclassified.Add($"{route.RoutePattern.RawText} {{{parameter.Name}}}");
                }
                // A parameter stays a placeholder, so no later prefix can match across it.
                literal.Append(string.Concat(segment.Parts.Select(p => p switch
                {
                    RoutePatternLiteralPart l => l.Content,
                    RoutePatternParameterPart p2 => "{" + p2.Name + "}",
                    RoutePatternSeparatorPart s => s.Content,
                    _ => "",
                })));
                literal.Append('/');
            }
        }

        Assert.True(unclassified.Count == 0,
            "These route parameters may carry a secret into the access log. Add the prefix to "
            + "LoggedPath.SecretSegmentPrefixes, or declare the name NotSecret with a reason:\n  "
            + string.Join("\n  ", unclassified.Distinct()));
        Assert.Contains("/api/auth/invite/", usedPrefixes);
        Assert.Contains("/api/auth/setup/", usedPrefixes);
        Assert.Contains("/api/oauth/consent/", usedPrefixes);
    }

    [GeneratedRegex(@"path:\s*'([^']*\$[^']*)'")]
    private static partial Regex RouterPath();

    [Fact]
    public void Every_spa_route_parameter_is_an_identifier_or_redacted()
    {
        // The pages an invite or setup link opens are served by the fallback, which no API
        // route classifies; the router is where they are declared.
        var router = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Web", "src", "router.ts"));
        var paths = RouterPath().Matches(router).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(paths);

        var unclassified = new List<string>();
        var usedPrefixes = new HashSet<string>();
        foreach (var path in paths)
        {
            var segments = path.Trim('/').Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (!segments[i].StartsWith('$')) continue;
                var prefix = "/" + string.Concat(segments.Take(i).Select(s => s + "/"));
                if (LoggedPath.SecretSegmentPrefixes.Contains(prefix)) { usedPrefixes.Add(prefix); continue; }
                // Every SPA identifier is a route id the API's own catalog types as a guid.
                if (segments[i].EndsWith("Id", StringComparison.Ordinal)) continue;
                unclassified.Add($"{path} {segments[i]}");
            }
        }

        Assert.True(unclassified.Count == 0,
            "These SPA route parameters may carry a secret into the access log:\n  "
            + string.Join("\n  ", unclassified));
        Assert.Contains("/invite/", usedPrefixes);
        Assert.Contains("/setup/", usedPrefixes);
    }

    // --- outbound: a delivery target's URL is a credential ------------------------------

    [Fact]
    public async Task A_delivery_does_not_write_its_target_url_to_the_log()
    {
        // A Slack or Discord webhook, a Healthchecks ping URL and a SimpleFIN access URL
        // all carry their secret in the path. HttpClient's logging writes the request URL
        // at Information on every send ("Start processing HTTP request POST https://…"),
        // which is why the category is held at Warning in appsettings.json.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var received = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync();
                var stream = peer.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return Encoding.ASCII.GetString(buffer, 0, read);
            });

            var logs = new CapturedLogs();
            await using var factory = new ApiFactory(_fixture)
                .WithConfig("Api:Outbound:AllowedNetworks", "127.0.0.1")
                .WithServices(s => s.AddSingleton<ILoggerProvider>(logs));
            using var _ = factory.CreateClient();
            var webhook = factory.Services.GetServices<INotificationSubscriber>()
                .OfType<WebhookSubscriber>().Single();

            await webhook.DeliverAsync(
                new NotificationEvent(NotificationSeverity.Warning, NotificationTopics.Backup,
                    "backup.stale", "No backup in 60 hours."),
                new SubscriberConfig($"http://127.0.0.1:{port}/hooks/{Secret}"),
                CancellationToken.None);

            // It was delivered, through the client the app registers ...
            Assert.StartsWith($"POST /hooks/{Secret} ", await received);
            // ... and nothing the default configuration writes names where.
            AssertNowhere(logs, Secret);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Api")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
