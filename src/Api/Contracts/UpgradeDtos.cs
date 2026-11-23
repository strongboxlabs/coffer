namespace Coffer.Api.Contracts;

/// <summary>The latest release as the screen shows it. Notes are plain text and rendered escaped.</summary>
public sealed record UpgradeReleaseDto(string Version, string Notes, string Url, DateTimeOffset? PublishedAt);

/// <summary>The host updater as the mailbox shows it: <c>running</c>, <c>disabled</c>, <c>stale</c> (silent for three ticks) or <c>absent</c>.</summary>
public sealed record UpgradeHostDto(string State, DateTimeOffset? At);

/// <summary>
/// The attempt on screen. <see cref="Phase"/> is backing-up, requested, started, done or failed;
/// <see cref="Changed"/> is whether a failed attempt got past the first change to this install.
/// </summary>
public sealed record UpgradeAttemptDto(
    string From, string To, string Phase, DateTime StartedAtUtc, string? Message, bool Changed);

/// <summary>Everything the Upgrade tab shows (ADR-0110).</summary>
public sealed record UpgradeStatusResponse(
    string Installed,
    bool InAppEnabled,
    bool AutoCheck,
    bool PrivateRepo,
    bool TokenStored,
    string Repository,
    string InstallDir,
    string HostNote,
    UpgradeHostDto Host,
    UpgradeReleaseDto? Latest,
    DateTimeOffset? CheckedAt,
    string? CheckError,
    bool UpdateAvailable,
    bool CanStart,
    string? CannotStartReason,
    UpgradeAttemptDto? Attempt);

public sealed record UpgradeSettingsRequest(bool InAppEnabled, bool AutoCheck, bool PrivateRepo);

public sealed record UpgradeTokenRequest(string Token);

/// <summary>Start an upgrade to <see cref="Version"/>; <see cref="Phrase"/> is the typed word <c>upgrade</c>.</summary>
public sealed record UpgradeStartRequest(string Version, string Phrase);
