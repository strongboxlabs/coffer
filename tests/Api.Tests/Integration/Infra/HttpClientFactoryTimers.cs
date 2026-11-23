using System.Collections;
using System.Reflection;

namespace Coffer.Api.Tests.Integration.Infra;

/// <summary>
/// The handler-expiry timers of a test host's HTTP client factory, which
/// <see cref="ApiFactory"/> stops when it disposes the host.
/// </summary>
/// <remarks>
/// <para>The factory gives each named client's handler a lifetime and starts a timer for
/// it; when it fires, the handler moves to a cleanup queue that a second timer drains. Both
/// timers call back into the factory, which holds the host's services, so a disposed host
/// stays reachable until its handlers expire and are cleaned up, minutes later. A
/// production process builds one host and keeps it for its life, so nothing is lost there;
/// a test run builds a host per test and keeps the hosts of the last few minutes, which
/// makes every later test slower. Stopping the timers once the host is disposed lets it go.
/// The production configuration is unchanged: the test host runs the same factory and
/// handler lifetime.</para>
/// <para>An entry's timer is stopped the way the factory's own tick does it, under the
/// entry's lock, so a tick already running sees no timer and does nothing. The cleanup
/// timer is only unscheduled, never disposed, so a late expiry can still start it.</para>
/// <para>The members read are the library's own; when one is renamed,
/// <see cref="Stop"/> fails rather than leave the hosts held, and <c>DisposedHostTests</c>
/// fails on a host that is still held.</para>
/// </remarks>
internal static class HttpClientFactoryTimers
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Stops every handler-expiry timer and the cleanup timer of
    /// <paramref name="factory"/>, the factory a disposed host resolved.</summary>
    /// <exception cref="InvalidOperationException">The library's members are not where they were.</exception>
    public static void Stop(IHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var type = factory.GetType();
        if (type.FullName != "Microsoft.Extensions.Http.DefaultHttpClientFactory")
            throw new InvalidOperationException($"The test host's HTTP client factory is a {type}, not the library's default.");

        foreach (var pair in (IEnumerable)Value(factory, type, "_activeHandlers"))
        {
            if (EntryOf(pair) is not { } entry) continue;
            var entryType = entry.GetType();
            lock (Value(entry, entryType, "_lock"))
            {
                (Field(entryType, "_timer").GetValue(entry) as IDisposable)?.Dispose();
                Field(entryType, "_timer").SetValue(entry, null);
            }
        }

        lock (Value(factory, type, "_cleanupTimerLock"))
            (Field(type, "_cleanupTimer").GetValue(factory) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The entry behind one pair of the active-handler table, or null when its
    /// lazy has not been created (no handler, so no timer).</summary>
    private static object? EntryOf(object pair)
    {
        var lazy = Property(pair, "Value");
        return (bool)Property(lazy, "IsValueCreated") ? Property(lazy, "Value") : null;
    }

    private static object Property(object owner, string name) =>
        (owner.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"{owner.GetType()} has no property {name}."))
        .GetValue(owner)
        ?? throw new InvalidOperationException($"{owner.GetType()}.{name} is null.");

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, InstanceFields)
        ?? throw new InvalidOperationException($"{type} has no field {name}.");

    private static object Value(object owner, Type type, string name) =>
        Field(type, name).GetValue(owner)
        ?? throw new InvalidOperationException($"{type}.{name} is null.");
}
