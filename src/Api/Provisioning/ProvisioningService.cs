using Coffer.Api.Configuration;
using Coffer.Importer.Moneydance.Db;
using Coffer.Importer.Moneydance.Json;
using Coffer.Importer.Moneydance.Pipeline;
using Microsoft.Extensions.Options;

namespace Coffer.Api.Provisioning;

/// <summary>
/// Demo-ledger seeding (ADR-0088). Creates a Demo ledger from the bundled sample
/// dataset when the user ticks the box during first-run setup.
/// </summary>
/// <remarks>
/// Supersedes the ADR-0071 D1 <c>coffer-api provision --mode clean|demo</c>
/// subcommand, now retired. That design shaped install state before the first
/// user existed and depended on migration-seeded placeholder ledgers; those are
/// gone (migration 186), so there is nothing to "clean" and the demo seed is
/// just a normal authenticated import triggered from setup.
/// </remarks>
public sealed class ProvisioningService
{
    private const string DemoLedgerName = "Demo";
    private const string DemoResource = "Coffer.Api.Provisioning.moneydance-export-demo.json";

    private readonly IMoneydanceImportService _import;
    private readonly string _serviceConnectionString;

    public ProvisioningService(
        IMoneydanceImportService import,
        IOptions<ApiOptions> options)
    {
        _import = import;
        _serviceConnectionString = options.Value.ServiceConnectionString;
    }

    /// <summary>
    /// Create a Demo ledger from the bundled sample dataset, owned by
    /// <paramref name="ownerUserId"/>. Returns the new ledger's id and name.
    /// </summary>
    /// <remarks>
    /// New-ledger-only, matching the in-app Moneydance import (ADR-0071 D2) and
    /// the ADR-0052 seed-once guard — it never writes into an existing ledger.
    /// The dataset brings its own category tree, so the starter catalogue is
    /// deliberately NOT layered on top.
    /// </remarks>
    public async Task<(Guid LedgerId, string Name)> ProvisionDemoAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var export = LoadDemoExport();

        // Long command timeout for the bulk COMMIT; connect as coffer_service.
        var factory = new DbConnectionFactory(_serviceConnectionString);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var result = await _import.ImportAsync(
            connection,
            export,
            existingLedgerId: null,
            newLedgerName: DemoLedgerName,
            ownerUserId: ownerUserId,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        return (result.LedgerId, result.LedgerName);
    }

    private static MdExport LoadDemoExport()
    {
        using var stream = typeof(ProvisioningService).Assembly.GetManifestResourceStream(DemoResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{DemoResource}' not found — demo provisioning needs the bundled sample dataset.");
        return MdItemReader.Read(stream);
    }
}
