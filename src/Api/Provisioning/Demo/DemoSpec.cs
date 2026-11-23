namespace Coffer.Api.Provisioning.Demo;

/// <summary>
/// What to generate: the seed, the one "now" the generator knows, and how much of
/// the window the export carries.
/// </summary>
/// <remarks>
/// <see cref="AsOf"/> is the only date the generator reads (ADR-0115 D1). It never reads a clock,
/// so the same spec always yields the same bytes. The product always uses
/// <see cref="DefaultSeed"/>, and so do the tests; a different seed exists only to
/// prove the output depends on it.
/// </remarks>
/// <param name="Seed">Keys every random stream and every generated id.</param>
/// <param name="AsOf">The last day of the history, and the export date.</param>
/// <param name="Span">The full window, or only its trailing months.</param>
public sealed record DemoSpec(ulong Seed, DateOnly AsOf, DemoSpan Span)
{
    /// <summary>The seed every product Demo is generated from.</summary>
    public const ulong DefaultSeed = 0xC0FF_EE5E_ED00_0115UL;

    /// <summary>What the product generates: the default seed and the whole window.</summary>
    public static DemoSpec Full(DateOnly asOf) => new(DefaultSeed, asOf, DemoSpan.Full);
}
