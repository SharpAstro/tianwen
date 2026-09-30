using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Ending a preview exposure through the node. A preview is a job that holds its camera, and a device a job holds is refused
/// its disconnect, so a preview that never ends (a body that sends no picture) held the window's quit for ever, saying only
/// "please wait". It is a picture for a window about to leave, not a run, so the quit ends it, and so does the Stop beside
/// the progress bar.
/// </summary>
public static class PictureJobs
{
    /// <summary>How long a stopped preview is given to end before the stop stops waiting for it.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Cancels every preview exposure the node is running and waits, up to <see cref="Budget"/>, for each to end. Returns the
    /// previews it asked to stop. Never throws for a refusal: a job that ended by itself meanwhile is the answer wanted.
    /// </summary>
    /// <param name="progress">A one-line status for a window that says what it is waiting for.</param>
    public static async Task<IReadOnlyList<JobDto>> StopPreviewsAsync(TianWenNodeClient client, ITimeProvider timeProvider,
        Action<string>? progress, CancellationToken cancellationToken)
    {
        var listed = await client.GetJobsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.Value is not { } all)
        {
            return [];
        }

        var running = all.Where(static j => j.Kind == JobDto.PreviewKind && j.State == JobState.Running).ToArray();
        foreach (var job in running)
        {
            progress?.Invoke("Stopping the preview exposure");
            await client.CancelJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

            var asked = timeProvider.GetUtcNow();
            while (timeProvider.GetUtcNow() - asked < Budget
                && (await client.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false)).Value is { State: JobState.Running })
            {
                await timeProvider.SleepAsync(RigShutdown.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return running;
    }
}
