using System.Reflection;

namespace Coffer.Api.Meta;

/// <summary>
/// The API's build identity (ADR-0044), read once from the assembly
/// attributes stamped by the <c>StampGitVersion</c> MSBuild target in
/// <c>Api.csproj</c>. Surfaced via <c>GET /api/meta/version</c> so the
/// SPA's About panel can show a build progression the user can eyeball.
///
/// Three pieces, mirrored on every layer (DB / API / UI):
///   • <see cref="Semver"/>  — the release handle, bumped by hand.
///   • <see cref="Build"/>   — the git commit count: a monotonic build
///     number that increments by one per commit on the deploy branch.
///   • <see cref="Commit"/>  — the git short SHA: exact "what's running".
///
/// All values degrade gracefully on a .git-less build (build 0 /
/// commit "nogit"), matching the MSBuild fallbacks.
/// </summary>
public static class VersionInfo
{
    static VersionInfo()
    {
        var asm = typeof(VersionInfo).Assembly;

        // InformationalVersion is "<semver>+<sha>" (we set it explicitly
        // in the build target and suppress the SDK's own +sha append).
        var informational =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
            ?? "0.0.0+nogit";
        var plus = informational.IndexOf('+');
        Semver = (plus >= 0 ? informational[..plus] : informational).Trim();
        Commit = (plus >= 0 ? informational[(plus + 1)..] : "nogit").Trim();

        var metadata = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value ?? string.Empty);

        Build = metadata.TryGetValue("BuildNumber", out var b)
                && int.TryParse(b.Trim(), out var parsed)
            ? parsed
            : 0;
        CommitDate = metadata.TryGetValue("CommitDate", out var d)
            ? d.Trim()
            : string.Empty;
    }

    /// <summary>Semver release handle, e.g. <c>0.1.0</c>.</summary>
    public static string Semver { get; }

    /// <summary>Git commit count — the monotonic build number.</summary>
    public static int Build { get; }

    /// <summary>Git short SHA, e.g. <c>68a34b7</c> (or <c>nogit</c>).</summary>
    public static string Commit { get; }

    /// <summary>Commit date (<c>yyyy-MM-dd</c>), empty if unavailable.</summary>
    public static string CommitDate { get; }
}
