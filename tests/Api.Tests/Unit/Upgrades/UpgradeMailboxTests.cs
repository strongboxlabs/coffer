using Coffer.Api.Tests.Support;

using Coffer.Api.Upgrades;

namespace Coffer.Api.Tests.Unit.Upgrades;

/// <summary>
/// The API's end of the upgrade mailbox (ADR-0110): a request is an empty file named for it, and
/// only well-formed host names are read back. Anything else in the directory is ignored.
/// </summary>
public sealed class UpgradeMailboxTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coffer-mbx-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));

    private UpgradeMailbox NewMailbox() => new(_dir, _clock);

    private void Touch(params string[] names)
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in names) File.WriteAllBytes(Path.Combine(_dir, name), []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_request_is_an_empty_file_named_for_it()
    {
        var id = NewMailbox().WriteRequest("0.129.0");

        var file = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal($"req_v1_{id}_0.129.0_1790000000", Path.GetFileName(file));
        Assert.Equal(0, new FileInfo(file).Length);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.129")]
    [InlineData("0.129.0.1")]
    [InlineData("00.129.0")]
    [InlineData("0.129.0_1")]
    [InlineData("v0.129.0")]
    [InlineData("../0.129.0")]
    public void A_request_for_something_that_is_not_a_release_version_is_refused(string version)
    {
        Assert.Throws<ArgumentException>(() => NewMailbox().WriteRequest(version));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public void Two_requests_never_share_an_id()
    {
        var mailbox = NewMailbox();

        var ids = Enumerable.Range(0, 50).Select(_ => mailbox.WriteRequest("0.129.0")).ToHashSet();

        Assert.Equal(50, ids.Count);
    }

    [Fact]
    public void An_empty_or_missing_directory_reads_as_nothing()
    {
        var mailbox = NewMailbox();

        Assert.Null(mailbox.Read().Host);
        mailbox.EnsureDirectory();
        var view = mailbox.Read();
        Assert.Empty(view.Requests);
        Assert.Empty(view.Statuses);
        Assert.Null(view.Host);
    }

    [Fact]
    public void The_host_names_are_read_back_and_the_newest_state_wins()
    {
        Touch($"st_v1_{Id}_started_0.128.1_0.129.0_1790000010",
              $"st_v1_{Id}_done_0.128.1_0.129.0_1790000099",
              "hb_v1_running_1790000100",
              "hb_v1_disabled_1790000200");

        var view = NewMailbox().Read();

        Assert.Equal("disabled", view.Host!.State);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_200), view.Host.At);
        Assert.Equal(["done", "started"], view.Statuses.Select(s => s.State).ToArray());
        Assert.All(view.Statuses, s => Assert.Equal(Id, s.Id));
    }

    [Fact]
    public void Names_that_are_not_exactly_the_format_are_ignored()
    {
        var odd = new List<string>
        {
            $"st_v1_{Id.ToUpperInvariant()}_done_0.1.0_0.2.0_1790000000",   // upper-case id
            $"st_v1_{Id}_done_0.1.0_0.2.0_179000000",                       // short epoch
            $"st_v1_{Id}_Done_0.1.0_0.2.0_1790000000",                      // state not lower-case
            "hb_v1_running_0790000000",                                     // leading zero in the epoch
            "hb_v1_running_1790000000.bak",
            "hb_v2_running_1790000000",
            "notes.txt",
        };
        // Windows cannot hold these names (it drops a trailing space and rejects a newline); the host is Linux.
        if (!OperatingSystem.IsWindows())
        {
            odd.Add($"req_v1_{Id}_0.129.0_1790000000 ");
            odd.Add($"req_v1_{Id}_0.129.0_1790000000\n");   // a `$` anchor would let this one through
            odd.Add("hb_v1_running_1790000000\n");
        }
        Touch(odd.ToArray());

        var view = NewMailbox().Read();

        Assert.Empty(view.Requests);
        Assert.Empty(view.Statuses);
        Assert.Null(view.Host);
    }

    [Fact]
    public void A_request_the_host_has_not_consumed_is_listed()
    {
        var mailbox = NewMailbox();
        var id = mailbox.WriteRequest("0.129.0");

        var request = Assert.Single(mailbox.Read().Requests);

        Assert.Equal(id, request.Id);
        Assert.Equal("0.129.0", request.Version);
    }
}
