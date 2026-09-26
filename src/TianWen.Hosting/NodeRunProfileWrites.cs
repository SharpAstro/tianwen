using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// What the node writes into a run's profile once the run has ended (P3 part 3 of docs/plans/hardware-in-the-server.md,
/// #930), through the node's one profile writer: the per-focuser backlash the run inferred, mirrored into the profile's
/// focuser URIs (<see cref="ProfileDataExtensions.WithBacklashEstimates"/>) so the next run starts from it. The GUI does the
/// same at a session's end; the node had no counterpart.
/// </summary>
/// <remarks>
/// A run's session is disposed when the next run starts, so the estimates are read as the run ends, and the write goes
/// on without holding it. Registered before the host of the runs, so the host stops after it: a run a stop ended still
/// gets its write, which <see cref="StopAsync"/> awaits.
/// </remarks>
internal sealed class NodeRunProfileWrites(HostedSession hosted, NodeProfiles profiles, ILogger<NodeRunProfileWrites> logger) : IHostedService
{
    private Task _pending = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hosted.RunEnded += OnRunEnded;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        hosted.RunEnded -= OnRunEnded;
        try
        {
            await Volatile.Read(ref _pending).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "The host's shutdown timeout ran out before a run's profile was written back");
        }
    }

    private void OnRunEnded(ISession session, NodeRunRecord run)
    {
        if (session.FocuserBacklashEstimates is not { IsEmpty: false } estimates)
        {
            return;
        }
        // One run at a time, so one write at a time.
        Volatile.Write(ref _pending, MirrorBacklashAsync(run.ProfileId, estimates));
    }

    private async Task MirrorBacklashAsync(Guid profileId, ImmutableDictionary<Uri, BacklashEstimateRecord> estimates)
    {
        try
        {
            var write = await profiles.UpdateAsync(profileId, readAt: null,
                current => current.Data is { } data && data.WithBacklashEstimates(estimates) is (var mirrored, true) ? current.WithData(mirrored) : current,
                CancellationToken.None);
            if (write.Outcome is ProfileWriteOutcome.Written)
            {
                logger.LogInformation("Mirrored the run's backlash estimates for {Count} focuser(s) into profile {ProfileId}", estimates.Count, profileId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mirror the run's backlash estimates into profile {ProfileId}", profileId);
        }
    }
}
