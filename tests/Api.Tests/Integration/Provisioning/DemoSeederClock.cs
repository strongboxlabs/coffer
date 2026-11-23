using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Coffer.Api.Db;
using Coffer.Api.Ingest;
using Coffer.Api.Provisioning.DemoSeeding;
using Coffer.Api.Sync.SimpleFin;

namespace Coffer.Api.Tests.Integration.Provisioning;

/// <summary>
/// The clock the Demo tests give the examples phase, and only it: the real time moved
/// <see cref="Ahead"/> forward. A Demo is added for a fixed day, so its last day and the
/// server's date can be the same day, and a date a step took from the clock would then
/// match the one it should take from that last day. Ahead of it, every date the clock
/// could give is past the next month's end, the next quarter's end and the next due dates
/// after the last day, so such a date is seen by the facts that compare dates.
/// </summary>
/// <remarks>
/// Its timers are the real ones, so the run's own deadline still runs. A test can also
/// fire every timer made on it at once (<see cref="FireTimers"/>), which is how the deadline
/// is reached without waiting for it. The host's own clock is not this one: the endpoint
/// still checks the day it is sent against the real date.
/// </remarks>
internal sealed class DemoSeederClock : TimeProvider
{
    /// <summary>How far ahead of the real time the examples phase runs.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(45);

    private readonly List<(TimerCallback Callback, object? State)> _timers = [];

    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + Ahead;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers) _timers.Add((callback, state));
        return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Run the callback of every timer made on this clock now, as if each were due.</summary>
    public void FireTimers()
    {
        List<(TimerCallback Callback, object? State)> due;
        lock (_timers) due = _timers.ToList();
        foreach (var (callback, state) in due) callback(state);
    }

    /// <summary>Build the app's seeder over this clock, which the services also hold, so a
    /// test can reach it.</summary>
    public static void UseFor(IServiceCollection services)
    {
        services.AddSingleton<DemoSeederClock>();
        services.RemoveAll<DemoSeeder>();
        services.AddSingleton(sp => new DemoSeeder(
            sp.GetRequiredService<ServiceDbContextFactory>(),
            sp.GetRequiredService<DemoSeederClock>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<IEnumerable<IFileProvider>>(),
            sp.GetRequiredService<SyncConnectionLock>()));
    }
}
