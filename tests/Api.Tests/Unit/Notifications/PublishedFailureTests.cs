using System.Text.RegularExpressions;

using Coffer.Api.Notifications;

namespace Coffer.Api.Tests.Unit.Notifications;

/// <summary>
/// What may appear in text that leaves the machine.
/// </summary>
public sealed class PublishedFailureTests
{
    // A message shaped like the ones that actually leak: an Npgsql failure names
    // the host, the port and the database, and every one of those is a thing an
    // outbound webhook holder should not learn.
    private const string LeakyMessage =
        "Failed to connect to 10.4.2.17:5432 for database coffer_prod "
        + "as user coffer_service (file /var/lib/postgresql/data/pg_hba.conf)";

    [Fact]
    public void A_database_failure_is_described_without_quoting_it()
    {
        var described = PublishedFailure.Describe(
            new Npgsql.NpgsqlException(LeakyMessage));

        Assert.Equal(
            "the database rejected the operation. The full message is in the server log.",
            described);

        // Named individually rather than as one "does not contain the message"
        // check, so a failure says WHICH piece of infrastructure escaped.
        Assert.DoesNotContain("10.4.2.17", described, StringComparison.Ordinal);
        Assert.DoesNotContain("5432", described, StringComparison.Ordinal);
        Assert.DoesNotContain("coffer_prod", described, StringComparison.Ordinal);
        Assert.DoesNotContain("coffer_service", described, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/lib", described, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_failure_says_so_rather_than_guessing_a_category()
    {
        var described = PublishedFailure.Describe(new InvalidOperationException(LeakyMessage));

        Assert.StartsWith("an unexpected error", described, StringComparison.Ordinal);
        Assert.DoesNotContain("10.4.2.17", described, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), "a network request failed or timed out")]
    [InlineData(typeof(TimeoutException), "a network request failed or timed out")]
    [InlineData(typeof(IOException), "a file could not be read or written")]
    public void Each_recognised_kind_keeps_its_own_words(Type exceptionType, string expected)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, LeakyMessage)!;
        var described = PublishedFailure.Describe(ex);

        Assert.StartsWith(expected, described, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakyMessage, described, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_payload_carries_the_type_and_nothing_else()
    {
        var detail = PublishedFailure.DetailFor(new Npgsql.NpgsqlException(LeakyMessage));

        var only = Assert.Single(detail);
        Assert.Equal("error_type", only.Key);
        Assert.Equal("NpgsqlException", only.Value);
    }

    /// <summary>
    /// No published summary anywhere in the API is built out of an exception
    /// message.
    /// </summary>
    /// <remarks>
    /// The reason this is a source scan rather than a behavioural test: the defect
    /// was ONE call site out of five, it survived review, and the next handler to
    /// publish a failure will reach for <c>ex.Message</c> for exactly the reason
    /// this one did — it is the obvious thing to write and nothing objects. A
    /// per-site test only covers the sites someone remembered; this covers the
    /// pattern, the same way the raw-SQL audit does for a different rule.
    ///
    /// <para>Deliberately narrow: it looks only at the <c>Summary:</c> argument of
    /// a <c>NotificationEvent</c>. Logging an exception message is correct and
    /// common, and a check that flagged it would be turned off within a week.</para>
    /// </remarks>
    [Fact]
    public void No_published_summary_is_built_from_an_exception_message()
    {
        var apiRoot = LocateApiSource();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("Summary:", StringComparison.Ordinal)) continue;

                // The argument may wrap; read until the line that ends it.
                var argument = string.Join(' ', lines.Skip(i).Take(4));
                var upToNextArg = argument.Split(new[] { "Detail:", "Topic:", "EventKey:" },
                    StringSplitOptions.None)[0];

                if (Regex.IsMatch(upToNextArg, @"\b(ex|exception|failure|error)\w*\.Message\b"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A published summary quotes an exception message. That text is delivered to "
            + "whatever URL an operator configured, and exception messages name hosts, "
            + "ports, database names and file paths. Use PublishedFailure.Describe(ex).\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The scan above is worthless if it cannot find the source, so prove it
    /// found something to look at rather than passing over an empty set.
    /// </summary>
    [Fact]
    public void The_source_scan_actually_reaches_the_api_and_its_publishers()
    {
        var apiRoot = LocateApiSource();
        var files = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal))
            .ToList();

        Assert.True(files.Count > 100, $"only {files.Count} source files found under {apiRoot}");
        Assert.Contains(files, f => Path.GetFileName(f) == "DailyBackupJobHandler.cs");
        Assert.Contains(files, f => Path.GetFileName(f) == "JobMonitorSignal.cs");

        var summaries = files.SelectMany(File.ReadAllLines)
            .Count(l => l.TrimStart().StartsWith("Summary:", StringComparison.Ordinal));
        Assert.True(summaries >= 5, $"only {summaries} published summaries found — the scan is looking at the wrong thing");
    }

    private static string LocateApiSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Api");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not find src/Api walking up from {AppContext.BaseDirectory}.");
    }
}
