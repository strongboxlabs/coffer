using Coffer.Api.Provisioning.Demo;

namespace Coffer.Api.Provisioning.DemoSeeding;

/// <summary>
/// What the examples phase of one Demo job works from (ADR-0115 D3): the ledger its own
/// import committed, who owns it, and what the generator said to add.
/// </summary>
/// <param name="LedgerId">The ledger the job's own import returned. Every write names it.</param>
/// <param name="OwnerUserId">The Demo's owner, recorded as the author where a writer asks for one.</param>
/// <param name="AsOf">The Demo's last day. Every date the examples compute comes from it,
/// never from the clock.</param>
/// <param name="Manifest">The generated Demo's rows, named by Moneydance id.</param>
/// <param name="CardStatementCsv">The card statement the examples phase imports.</param>
public sealed record DemoSeedContext(
    Guid LedgerId,
    Guid OwnerUserId,
    DateOnly AsOf,
    DemoManifest Manifest,
    string CardStatementCsv);
