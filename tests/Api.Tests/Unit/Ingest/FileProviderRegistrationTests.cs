using System.Reflection;

using Coffer.Api.Ingest;

namespace Coffer.Api.Tests.Unit.Ingest;

/// <summary>
/// Every file provider must be registered in BOTH places, or it fails at import time.
/// </summary>
/// <remarks>
/// <para>A provider needs two registrations that live far apart: the DI container, so
/// the orchestrator can find it, and <c>IngestOrchestrator</c>'s origin map, so the row
/// it writes has a <c>txn_headers.origin</c>. Missing the second compiles, starts,
/// serves, and previews a file perfectly — then throws when the user presses Import,
/// after they have chosen a file and an account.</para>
///
/// <para>Which is precisely what the Fidelity provider did. It was found by a person
/// importing their own statement, because nothing in the build could see the map. This
/// test is that missing pair of eyes: it reflects over every IFileProvider in the
/// assembly, so a new one is covered the moment it exists rather than when someone
/// remembers to add a case.</para>
/// </remarks>
public sealed class FileProviderRegistrationTests
{
    private static IEnumerable<IFileProvider> AllProviders() =>
        typeof(IngestOrchestrator).Assembly
            .GetTypes()
            .Where(t => typeof(IFileProvider).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IFileProvider)Activator.CreateInstance(t)!);

    [Fact]
    public void Every_file_provider_has_an_origin_mapping()
    {
        var providers = AllProviders().ToList();
        // A guard over an empty set is not a guard. If reflection stops finding
        // providers — renamed interface, constructor gaining a dependency — this test
        // would pass silently while covering nothing.
        Assert.True(providers.Count >= 3,
            $"Expected to find the file providers by reflection, found {providers.Count}. "
            + "If a provider now takes constructor dependencies, this discovery needs "
            + "widening rather than deleting.");

        var missing = providers
            .Select(p => p.ProviderKey)
            .Where(key => !IngestOrchestrator.OriginRegistry.ContainsKey(key))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            $"""
            These file providers have no txn_headers.origin mapping, so an import through
            them throws AFTER the user has chosen a file and an account:

                {string.Join("\n    ", missing)}

            Add each to IngestOrchestrator's ProviderOriginFor map.
            """);
    }

    [Fact]
    public void Every_origin_mapping_names_a_provider_that_exists()
    {
        // The other direction. A mapping for a key nothing answers to is dead weight
        // that reads as coverage — and the next person adding a provider will copy the
        // stale entry as a template.
        var keys = AllProviders().Select(p => p.ProviderKey).ToHashSet(StringComparer.Ordinal);
        var orphaned = IngestOrchestrator.OriginRegistry.Keys
            .Where(k => !keys.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphaned.Count == 0,
            "These origin mappings name no provider: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void Every_file_import_is_recorded_as_a_file_import()
    {
        // origin is what the register's row icon reads. The schema admits exactly
        // 'manual', 'online_import' and 'file_import'; a file provider claiming anything
        // else would either violate the CHECK or mislabel the row.
        foreach (var (key, metadata) in IngestOrchestrator.OriginRegistry)
        {
            Assert.Equal("file_import", metadata.Origin);
            // provider_key is NOT NULL for every non-manual row (mig 107).
            Assert.False(string.IsNullOrWhiteSpace(metadata.ProviderKey),
                $"'{key}' maps to a blank provider_key.");
        }
    }
}
