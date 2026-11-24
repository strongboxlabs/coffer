using System.Diagnostics;

using Coffer.Api.Ingest.Qif;
using Coffer.Api.Tests.Unit.Ingest;

namespace Coffer.Api.Tests.Integration.Stress;

/// <summary>
/// Splitting a QIF security name of many parentheses takes linear time (FU-092). A time
/// follows the machine, so it is measured here, on demand; the every-build test
/// (<c>QifSecurityNameTests.A_name_of_many_parentheses_is_the_whole_name</c>) holds the
/// answer on the same input.
/// </summary>
public sealed class QifSecurityNameCostTests
{
    private readonly ITestOutputHelper _output;

    public QifSecurityNameCostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void A_name_of_many_parentheses_takes_linear_time()
    {
        var hostile = QifSecurityNameTests.HostileName();
        var watch = Stopwatch.StartNew();
        Assert.Equal((hostile, (string?)null), QifFileProvider.SplitSecurity(hostile));
        _output.WriteLine($"qif-name: {watch.Elapsed.TotalMilliseconds:0} ms for {hostile.Length} characters");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }
}
