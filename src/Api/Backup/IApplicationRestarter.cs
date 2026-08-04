using Microsoft.Extensions.Hosting;

namespace Coffer.Api.Backup;

/// <summary>
/// Abstraction over "stop the process so the container restarts it" — used by
/// the bootstrap restore (ADR-0061), which stages a backup then needs the next
/// boot to apply it. Injectable so tests can assert a restart was requested
/// without actually tearing down the test host.
/// </summary>
public interface IApplicationRestarter
{
    /// <summary>Request a graceful shutdown (Docker's restart policy brings the
    /// container back). Returns immediately; the stop fires shortly after so the
    /// in-flight HTTP response can flush first.</summary>
    void RequestRestart();
}

/// <summary>Real implementation: a delayed <see cref="IHostApplicationLifetime.StopApplication"/>.
/// With the compose <c>restart: unless-stopped</c> policy, the exited container
/// is restarted onto the (now restore-staged) data.</summary>
public sealed class HostApplicationRestarter : IApplicationRestarter
{
    private readonly IHostApplicationLifetime _lifetime;

    public HostApplicationRestarter(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public void RequestRestart() =>
        _ = Task.Run(async () =>
        {
            // Let the 200 response flush before the host stops.
            await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            _lifetime.StopApplication();
        });
}
