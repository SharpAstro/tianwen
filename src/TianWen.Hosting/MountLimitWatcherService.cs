using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// Drives <see cref="MountLimitWatcher"/> (P3 of <c>docs/plans/mount-safety-limits.md</c>) for the
/// life of the process. Node-scoped, not session-scoped: it runs whether or not
/// <see cref="IHostedSession"/> currently has a session, which is the whole point -- a manual slew
/// from the hosted API, or an idle rig connected but nothing running, gets the same mechanical
/// safety net as a run in progress.
/// </summary>
/// <remarks>
/// This is the ASP.NET-hosted half of P3. <see cref="MountLimitWatcher"/> itself lives in
/// <c>TianWen.Lib</c> and takes no dependency on <c>Microsoft.Extensions.Hosting</c>, so a non-ASP.NET
/// host (the GUI) drives it a different way -- a fire-and-forget loop started from its own
/// composition root, not this class.
/// </remarks>
internal sealed class MountLimitWatcherService(MountLimitWatcher watcher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => watcher.RunAsync(stoppingToken);
}
