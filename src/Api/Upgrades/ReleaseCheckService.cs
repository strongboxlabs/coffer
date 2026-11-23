using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using Coffer.Api.Configuration;
using Coffer.Api.Db.Repositories;

namespace Coffer.Api.Upgrades;

/// <summary>The latest release as the app shows it. Notes are plain text, capped; the screen renders them escaped.</summary>
public sealed record ReleaseInfo(string Version, string Notes, string Url, DateTimeOffset? PublishedAt);

/// <summary>One release check: what it found, when, and why it failed if it did. The error never names a token.</summary>
public sealed record ReleaseCheckResult(ReleaseInfo? Latest, DateTimeOffset CheckedAt, string? Error);

/// <summary>
/// The last check, held in memory (ADR-0110). A successful result is also stored in
/// <c>system_settings</c> and read back once after a restart, so what the Upgrade tab and the
/// navigation show does not depend on whether a check has run since the process started.
/// </summary>
public sealed class ReleaseCheckState
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    public ReleaseCheckResult? Last { get; internal set; }

    /// <summary>The stored result has been looked for, found or not, so it is read once.</summary>
    internal bool StoredLoaded { get; set; }
}

/// <summary>
/// Reads the latest release from GitHub (ADR-0110). Lazy: with the automatic check on, a screen load
/// checks when the last result is older than <see cref="Staleness"/>; with it off, only
/// <see cref="CheckNowAsync"/> goes to the network. The API only reads release metadata and never
/// downloads an image: the host pulls it. A private fork is read with the sealed token, sent to
/// the GitHub API only.
/// </summary>
public sealed partial class ReleaseCheckService
{
    /// <summary>How long a result is trusted before the lazy check looks again.</summary>
    public static readonly TimeSpan Staleness = TimeSpan.FromHours(6);

    /// <summary>The longest release-notes text kept.</summary>
    public const int MaxNotesLength = 4000;

    private readonly HttpClient _http;
    private readonly SystemSettingsRepository _settings;
    private readonly UpgradeRepoTokenStore _token;
    private readonly IOptions<ApiOptions> _options;
    private readonly ReleaseCheckState _state;
    private readonly TimeProvider _clock;
    private readonly UpgradeNotices? _notices;
    private readonly ILogger<ReleaseCheckService>? _logger;

    public ReleaseCheckService(
        HttpClient http, SystemSettingsRepository settings, UpgradeRepoTokenStore token,
        IOptions<ApiOptions> options, ReleaseCheckState state, TimeProvider clock,
        UpgradeNotices? notices = null, ILogger<ReleaseCheckService>? logger = null)
    {
        _http = http;
        _settings = settings;
        _token = token;
        _options = options;
        _state = state;
        _clock = clock;
        _notices = notices;
        _logger = logger;
    }

    /// <summary>What is stored of a successful check: enough to show the Upgrade tab again.</summary>
    private sealed record StoredCheck(
        string Version, string Notes, string Url, DateTimeOffset? PublishedAt, DateTimeOffset CheckedAt);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}\z")]
    private static partial Regex RepositoryPattern();

    /// <summary>The last result, checking first only when the automatic check is on and it is stale or absent.</summary>
    public async Task<ReleaseCheckResult?> CurrentAsync(CancellationToken ct = default)
    {
        await LoadStoredAsync(ct).ConfigureAwait(false);
        var last = _state.Last;
        var fresh = last is not null && _clock.GetUtcNow() - last.CheckedAt < Staleness;
        if (fresh) return last;
        var auto = await _settings.GetBoolAsync(SystemSettingsRepository.UpgradeAutoCheckKey, fallback: false, ct)
            .ConfigureAwait(false);
        if (!auto) return last;
        // The held result, not the raw one the check returns: a lazy check that fails must show the
        // release last found with the failure beside it, as Check now does.
        await CheckNowAsync(ct).ConfigureAwait(false);
        return _state.Last;
    }

    /// <summary>Go to GitHub now, whatever the setting says. One check at a time.</summary>
    public async Task<ReleaseCheckResult> CheckNowAsync(CancellationToken ct = default)
    {
        // After a restart the release last found is in storage, not yet in memory: read it first, or a
        // check that fails would have nothing to keep.
        await LoadStoredAsync(ct).ConfigureAwait(false);
        await _state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await FetchAsync(ct).ConfigureAwait(false);
            // What the screen holds keeps the release last found, with the failure beside it: a check
            // that could not reach GitHub says nothing about the release, so it must not hide one the
            // install already knows. The caller still gets the raw result, because a decision (the
            // upgrade start) must never rest on a release this check did not just read.
            _state.Last = result.Latest is null && _state.Last?.Latest is { } known
                ? new ReleaseCheckResult(known, result.CheckedAt, result.Error)
                : result;
            _state.StoredLoaded = true;

            // Only a result that found a release is kept and announced: a failed check says nothing
            // about the release, and must not erase what was last known or clear a standing notice.
            if (result.Latest is { } latest)
            {
                await StoreAsync(result, latest, ct).ConfigureAwait(false);
                if (_notices is not null)
                    await _notices.ReconcileLockedAsync(latest.Version, Meta.VersionInfo.Semver, ct).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    /// <summary>After a restart, the last good result is read back once (nothing is checked).</summary>
    private async Task LoadStoredAsync(CancellationToken ct)
    {
        if (_state.StoredLoaded) return;
        await _state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.StoredLoaded) return;
            _state.StoredLoaded = true;
            if (_state.Last is not null) return;

            var json = await _settings.GetValueJsonAsync(SystemSettingsRepository.UpgradeLastCheckKey, ct)
                .ConfigureAwait(false);
            if (json is null) return;
            try
            {
                var stored = JsonSerializer.Deserialize<StoredCheck>(json);
                if (stored is null || !UpgradeMailbox.IsVersion(stored.Version)) return;
                var url = stored.Url.StartsWith("https://github.com/", StringComparison.Ordinal) ? stored.Url : "";
                var notes = stored.Notes.Length > MaxNotesLength ? stored.Notes[..MaxNotesLength] : stored.Notes;
                _state.Last = new ReleaseCheckResult(
                    new ReleaseInfo(stored.Version, notes, url, stored.PublishedAt), stored.CheckedAt, null);
            }
            catch (JsonException)
            {
                // A stored value that does not parse is as good as none: the next check replaces it.
            }
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private async Task StoreAsync(ReleaseCheckResult result, ReleaseInfo latest, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new StoredCheck(latest.Version, latest.Notes, latest.Url, latest.PublishedAt, result.CheckedAt));
            await _settings.SetValueJsonAsync(
                SystemSettingsRepository.UpgradeLastCheckKey, json, updatedBy: null,
                _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not being able to remember the result must not fail the check that found it.
            _logger?.LogWarning(ex, "Could not store the release check result.");
        }
    }

    private async Task<ReleaseCheckResult> FetchAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var repository = _options.Value.Upgrade.Repository;
        if (!RepositoryPattern().IsMatch(repository ?? string.Empty))
            return new ReleaseCheckResult(null, now, "The configured repository is not owner/name.");

        var privateRepo = await _settings.GetBoolAsync(SystemSettingsRepository.UpgradePrivateRepoKey, fallback: false, ct)
            .ConfigureAwait(false);
        string? token = null;
        if (privateRepo)
        {
            token = await _token.GetAsync(ct).ConfigureAwait(false);
            if (token is null)
                return new ReleaseCheckResult(null, now, "The private fork needs its token entered again.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Coffer-release-check");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new ReleaseCheckResult(null, now, privateRepo
                    ? "No release was found; check the token can read the repository."
                    : "No release was found.");
            if (!response.IsSuccessStatusCode)
                return new ReleaseCheckResult(null, now, $"GitHub answered {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body, now);
        }
        catch (HttpRequestException)
        {
            return new ReleaseCheckResult(null, now, "GitHub could not be reached.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReleaseCheckResult(null, now, "GitHub did not answer in time.");
        }
    }

    private static ReleaseCheckResult Parse(string body, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var version = tag is not null && tag.StartsWith('v') ? tag[1..] : tag;
            if (!UpgradeMailbox.IsVersion(version))
                return new ReleaseCheckResult(null, now, "The latest release is not tagged as a version.");

            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
            if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];
            var url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            // Shown as a link: only a github.com address is kept.
            if (!url.StartsWith("https://github.com/", StringComparison.Ordinal)) url = "";
            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
            return new ReleaseCheckResult(new ReleaseInfo(version!, notes, url, published), now, null);
        }
        catch (JsonException)
        {
            return new ReleaseCheckResult(null, now, "GitHub's answer could not be read.");
        }
    }

    /// <summary>True when <paramref name="candidate"/> is a strictly newer x.y.z than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string candidate, string installed)
    {
        if (!UpgradeMailbox.IsVersion(candidate) || !UpgradeMailbox.IsVersion(installed)) return false;
        return Version.Parse(candidate) > Version.Parse(installed);
    }
}
