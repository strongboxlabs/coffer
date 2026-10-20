using System.Text.RegularExpressions;

namespace Coffer.Api.Tests.Unit;

/// <summary>
/// Nothing in src/Api protects data with ASP.NET Core Data Protection (FU-095).
/// </summary>
/// <remarks>
/// <para>Its keys are not configured here, so they take the framework default: an
/// unencrypted XML key ring in the process user's home directory, which in the container
/// is outside every volume. Nothing uses it today, so that costs nothing. The first
/// consumer would inherit both faults at once: data it protected would become
/// unreadable when the container is recreated, which an upgrade does, and the key ring
/// would sit on disk in the clear beside a master key that is not.</para>
///
/// <para>So adding one is a decision, not an import. Persist the ring on the
/// <c>coffer_keys</c> volume, encrypt it at rest, give it an application name, and change
/// this guard in the same commit. Asserted against the source, as
/// <see cref="ClockDisciplineTests"/> is: a consumer has no symptom until the restart
/// that loses its data. Lines that start with <c>//</c> are skipped.</para>
/// </remarks>
public sealed class DataProtectionGuardTests
{
    private static readonly Regex Consumer = new(
        @"\bIDataProtect(?:or|ionProvider)\b|\bITimeLimitedDataProtector\b|\bDataProtectionProvider\b"
        + @"|\.\s*CreateProtector\s*\(|\bAddDataProtection\s*\(|\bMicrosoft\.AspNetCore\.DataProtection\b",
        RegexOptions.Compiled);

    [Fact]
    public void No_code_in_the_api_protects_data_with_data_protection()
    {
        var root = Path.Combine(RepoRoot(), "src", "Api");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (Consumer.IsMatch(lines[i])) hits.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(hits.Count == 0,
            "Data Protection is used, with keys the API never configured: unencrypted, and lost "
            + "with the container. Persist the key ring on the coffer_keys volume and encrypt it "
            + "first (see this test's remarks):\n  " + string.Join("\n  ", hits));
    }

    [Fact]
    public void The_guard_sees_a_consumer()
    {
        // A pattern that matched nothing would pass forever.
        Assert.Matches(Consumer, "public Foo(IDataProtectionProvider provider)");
        Assert.Matches(Consumer, "var p = provider.CreateProtector(\"purpose\");");
        Assert.Matches(Consumer, "builder.Services.AddDataProtection()");
        Assert.DoesNotMatch(Consumer, "var protectedValue = SecretFile.Read(path);");
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
