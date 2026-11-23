using Coffer.Api.Provisioning.Demo;
using Coffer.Importer.Moneydance.Json;

namespace Coffer.Api.Tests.Support;

/// <summary>
/// The generated Demo at the tests' fixed date, in the three spans the tests use.
/// Every test that once read the committed demo export reads one of these instead.
/// </summary>
/// <remarks>
/// <para>The product passes the browser's date; a test passes
/// <see cref="DemoTestDate"/>, so a test's export is the same on every run and every
/// day. Each build is generated once per test process and shared.</para>
/// <para><see cref="Full"/> is the five-year Demo; <see cref="Short"/> keeps the
/// trailing twelve months of bank-route rows and <see cref="Tiny"/> three
/// (<see cref="DemoSpan.LastMonths"/>), with the whole investment route, every
/// account, security, price, split and reminder in both.</para>
/// </remarks>
public static class DemoTestExports
{
    /// <summary>The date every test generates the Demo for.</summary>
    public static readonly DateOnly DemoTestDate = new(2026, 11, 18);

    /// <summary>The AsOf of every build here: <see cref="DemoTestDate"/>.</summary>
    public static DateOnly AsOf => DemoTestDate;

    /// <summary>The whole window.</summary>
    public static readonly Lazy<DemoBuild> Full = new(() => Generate(DemoSpan.Full));

    /// <summary>The trailing twelve months of bank-route rows.</summary>
    public static readonly Lazy<DemoBuild> Short = new(() => Generate(DemoSpan.LastMonths(12)));

    /// <summary>The trailing three months of bank-route rows.</summary>
    public static readonly Lazy<DemoBuild> Tiny = new(() => Generate(DemoSpan.LastMonths(3)));

    /// <summary>The <see cref="DemoSpec"/> of a span at <see cref="DemoTestDate"/>.</summary>
    public static DemoSpec Spec(DemoSpan span) => new(DemoSpec.DefaultSeed, DemoTestDate, span);

    /// <summary>The export's bytes (shared: do not write to the array).</summary>
    public static byte[] FullBytes => Full.Value.Json;

    /// <inheritdoc cref="FullBytes"/>
    public static byte[] ShortBytes => Short.Value.Json;

    /// <inheritdoc cref="FullBytes"/>
    public static byte[] TinyBytes => Tiny.Value.Json;

    /// <summary>A freshly parsed export; the caller owns and disposes it.</summary>
    public static MdExport FullExport() => Full.Value.ToExport();

    /// <inheritdoc cref="FullExport"/>
    public static MdExport ShortExport() => Short.Value.ToExport();

    /// <inheritdoc cref="FullExport"/>
    public static MdExport TinyExport() => Tiny.Value.ToExport();

    private static DemoBuild Generate(DemoSpan span) => DemoExportGenerator.Generate(Spec(span));
}
