namespace Coffer.Api.Tests.Support;

/// <summary>
/// A clock a test can place and move. Everything in src/Api reads the time through an
/// injected <see cref="TimeProvider"/> (ClockDisciplineTests), so this is how a test
/// pins "now" instead of racing the wall clock.
/// </summary>
public sealed class TestClock : TimeProvider
{
    public TestClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
