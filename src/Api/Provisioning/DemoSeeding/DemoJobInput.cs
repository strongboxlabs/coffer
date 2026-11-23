using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// What a Demo job carries beside its export: what the examples phase needs once the
/// import has committed (ADR-0115 D3). An upload has no such input.
/// </summary>
/// <param name="Manifest">The generated Demo's rows, named by Moneydance id.</param>
/// <param name="CardStatementCsv">The card statement the examples phase imports.</param>
/// <param name="AsOf">The Demo's last day: the date every examples step computes from.</param>
public sealed record DemoJobInput(DemoManifest Manifest, string CardStatementCsv, DateOnly AsOf);
