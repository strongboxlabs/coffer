using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Coffer.Api.Upgrades;

/// <summary>
/// The API's end of the upgrade mailbox (ADR-0110): a directory on a dedicated volume the host
/// updater reads. A request is an EMPTY file whose name is the request,
/// <c>req_v1_{32 hex id}_{x.y.z}_{10-digit epoch}</c>; the host reads names only, never contents,
/// and decides everything itself. The host writes back <c>st_v1_{id}_{state}_{from}_{to}_{epoch}</c>
/// per request and one <c>hb_v1_{state}_{epoch}</c> it replaces on every tick. The API shares the
/// volume and can write any of those names, so what it reads here is the host's word only as far as
/// the volume can be trusted, and <c>app-upgrades.sh status</c> on the host is the authority.
/// </summary>
public sealed partial class UpgradeMailbox
{
    /// <summary>
    /// How long the host honours a request (its <c>EXPIRY</c>, 900 seconds). Past that it refuses the
    /// request as expired and removes it, so a request older than this no longer holds the mailbox.
    /// </summary>
    public static readonly TimeSpan RequestExpiry = TimeSpan.FromMinutes(15);

    private readonly string _directory;
    private readonly TimeProvider _clock;

    public UpgradeMailbox(string directory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _clock = clock;
    }

    [GeneratedRegex(@"^req_v1_(?<id>[0-9a-f]{32})_(?<ver>(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4}))_(?<epoch>[1-9][0-9]{9})\z")]
    private static partial Regex RequestName();

    [GeneratedRegex(@"^st_v1_(?<id>[0-9a-f]{32})_(?<state>[a-z0-9]{1,16})_(?<from>[0-9.]{1,17})_(?<to>[0-9.]{1,17})_(?<epoch>[1-9][0-9]{9})\z")]
    private static partial Regex StatusName();

    [GeneratedRegex(@"^hb_v1_(?<state>[a-z-]{1,16})_(?<epoch>[1-9][0-9]{9})\z")]
    private static partial Regex StateName();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z")]
    private static partial Regex VersionPattern();

    public static bool IsVersion(string? value) => value is not null && VersionPattern().IsMatch(value);

    /// <summary>The mailbox directory exists, so a missing one is an error and not an empty mailbox.</summary>
    public void EnsureDirectory() => Directory.CreateDirectory(_directory);

    /// <summary>
    /// Write a request for <paramref name="version"/>, issued now. Created exclusively, so an id is
    /// never reused. Returns the request id. The name carries everything; the file is empty.
    /// </summary>
    public string WriteRequest(string version)
    {
        if (!IsVersion(version)) throw new ArgumentException("Not a release version (x.y.z).", nameof(version));
        EnsureDirectory();
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var epoch = _clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var path = Path.Combine(_directory, $"req_v1_{id}_{version}_{epoch}");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        return id;
    }

    /// <summary>What the mailbox holds now: pending requests, the host's statuses, and its state name.</summary>
    public MailboxView Read()
    {
        var requests = new List<MailboxRequest>();
        var statuses = new List<MailboxStatus>();
        MailboxState? state = null;
        if (!Directory.Exists(_directory)) return new MailboxView(requests, statuses, null);

        foreach (var file in Directory.EnumerateFiles(_directory).Take(2000))
        {
            var name = Path.GetFileName(file);
            if (RequestName().Match(name) is { Success: true } r)
            {
                requests.Add(new MailboxRequest(r.Groups["id"].Value, r.Groups["ver"].Value, Epoch(r.Groups["epoch"].Value)));
            }
            else if (StatusName().Match(name) is { Success: true } s)
            {
                statuses.Add(new MailboxStatus(s.Groups["id"].Value, s.Groups["state"].Value,
                    s.Groups["from"].Value, s.Groups["to"].Value, Epoch(s.Groups["epoch"].Value)));
            }
            else if (StateName().Match(name) is { Success: true } h)
            {
                var candidate = new MailboxState(h.Groups["state"].Value, Epoch(h.Groups["epoch"].Value));
                if (state is null || candidate.At > state.At) state = candidate;
            }
        }
        statuses.Sort((a, b) => b.At.CompareTo(a.At));
        requests.Sort((a, b) => b.At.CompareTo(a.At));
        return new MailboxView(requests, statuses, state);
    }

    private static DateTimeOffset Epoch(string seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(seconds, CultureInfo.InvariantCulture));
}

/// <summary>A request still in the mailbox: the host has not consumed it yet.</summary>
public sealed record MailboxRequest(string Id, string Version, DateTimeOffset At);

/// <summary>One thing the host said about a request: started, done, expired, notlatest, exit12, and so on.</summary>
public sealed record MailboxStatus(string Id, string State, string From, string To, DateTimeOffset At);

/// <summary>The host's own state name: running or disabled, and when it last wrote it.</summary>
public sealed record MailboxState(string State, DateTimeOffset At);

/// <summary>Everything the mailbox holds that the API reads.</summary>
public sealed record MailboxView(
    IReadOnlyList<MailboxRequest> Requests, IReadOnlyList<MailboxStatus> Statuses, MailboxState? Host);
