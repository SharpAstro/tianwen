using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The guide camera's dark library as the Guider tab takes it (#1915): starts the node's dark library on the profile's guide
/// camera at the exposures the loop is likely to run at, watches its progress, and says how it ended. The node does the work
/// (<c>POST /api/v1/darks</c> with the guide camera as the camera role), so a remote client takes the same library.
/// </summary>
public sealed class GuideDarksController(ITimeProvider timeProvider, ILogger logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>The counts the panel steps through: frames per exposure.</summary>
    public static readonly int[] Counts = [5, 10, 20, 30];

    private DarkLibraryStateDto? _state;
    private string? _refusal;
    private int _starting;

    /// <summary>Asks the host for a frame; set once by whoever wires the signals.</summary>
    public Action? RedrawRequested { get; set; }

    /// <summary>Frames per exposure; the panel's stepper moves it along <see cref="Counts"/>.</summary>
    public int Count { get; private set; } = 10;

    /// <summary>The node's state of the dark library going on, or the last to end; null before the first answer.</summary>
    public DarkLibraryStateDto? State => Volatile.Read(ref _state);

    /// <summary>Whether a library of the guide camera's is being taken, or started.</summary>
    public bool IsRunning => State is { CameraRole: LiveViewCamera.Guider, Running: true } || Volatile.Read(ref _starting) == 1;

    /// <summary>Steps <see cref="Count"/> along <see cref="Counts"/>.</summary>
    public void StepCount(int direction)
    {
        var at = Array.IndexOf(Counts, Count);
        Count = Counts[Math.Clamp((at < 0 ? 1 : at) + direction, 0, Counts.Length - 1)];
    }

    /// <summary>
    /// The exposures a library covers for a loop at <paramref name="loopSeconds"/>: that one and the ladder's step either side,
    /// which is where a guide loop is moved to while the star is found, so a dark is at hand for each.
    /// </summary>
    public static double[] ExposuresFor(double loopSeconds)
        => [.. new[] { LiveSessionActions.StepExposure(loopSeconds, -1), loopSeconds, LiveSessionActions.StepExposure(loopSeconds, +1) }
            .Distinct().Order()];

    /// <summary>What the panel says of the library: its progress, how it ended, or why it did not start; empty with nothing to say.</summary>
    public string Status
    {
        get
        {
            if (Volatile.Read(ref _refusal) is { } refusal)
            {
                return refusal;
            }
            if (State is not { CameraRole: LiveViewCamera.Guider } state)
            {
                return "";
            }
            var total = Math.Max(state.Count * Math.Max(state.ExposuresSeconds.Length, 1), 1);
            return state.Running ? $"Darks {state.Frames.Length}/{total}"
                : state.FailureReason is { } failure ? $"Darks failed: {failure}"
                : state.Stopped ? $"Darks stopped at {state.Frames.Length}/{total}"
                : $"Darks done: {state.Frames.Length} frames";
        }
    }

    /// <summary>Starts the node's dark library on the guide camera. Null once it runs, else the node's refusal in its words.</summary>
    public async Task<string?> StartAsync(NodeConnection node, double loopSeconds, int? gain, CancellationToken appToken)
    {
        if (IsRunning)
        {
            return "A dark library is already being taken";
        }

        Volatile.Write(ref _starting, 1);
        try
        {
            var exposures = ExposuresFor(loopSeconds);
            var request = new DarkLibraryRequestDto
            {
                // The node resolves the profile's guide camera for this role.
                DeviceUri = "",
                CameraRole = LiveViewCamera.Guider,
                ExposureSeconds = exposures[0],
                ExposuresSeconds = exposures,
                Count = Count,
                Gain = gain is { } g ? (short)g : null,
            };
            var started = await node.Client.StartDarkLibraryAsync(request, appToken).ConfigureAwait(false);
            if (started is not { IsSuccess: true, Value: { } state })
            {
                var why = started.Error ?? "The node did not start the darks";
                Volatile.Write(ref _refusal, why);
                RedrawRequested?.Invoke();
                return why;
            }

            Volatile.Write(ref _refusal, null);
            Volatile.Write(ref _state, state);
            RedrawRequested?.Invoke();
            _ = Task.Run(() => WatchAsync(node, appToken), CancellationToken.None);
            return null;
        }
        finally
        {
            Volatile.Write(ref _starting, 0);
        }
    }

    /// <summary>Stops the library being taken after the frame in hand. Null once it has, else why not.</summary>
    public async Task<string?> StopAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        var stopped = await node.Client.StopDarkLibraryAsync(cancellationToken).ConfigureAwait(false);
        if (stopped is { IsSuccess: true, Value: { } state })
        {
            Volatile.Write(ref _state, state);
            RedrawRequested?.Invoke();
            return null;
        }
        return stopped.IsNotFound ? null : stopped.Error ?? "The node did not stop the darks";
    }

    private async Task WatchAsync(NodeConnection node, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (await node.Client.GetDarkLibraryAsync(cancellationToken).ConfigureAwait(false) is { IsSuccess: true, Value: { } state })
                {
                    Volatile.Write(ref _state, state);
                    RedrawRequested?.Invoke();
                    if (!state.Running)
                    {
                        if (state.FailureReason is { } failure)
                        {
                            logger.LogWarning("The guide camera's darks ended: {Failure}", failure);
                        }
                        return;
                    }
                }
                await timeProvider.SleepAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is going: the node's run finishes its count by itself.
        }
    }
}
