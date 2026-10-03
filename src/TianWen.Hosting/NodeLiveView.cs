using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Hosting;

/// <summary>
/// A live view as the node's run (P4 of docs/plans/live-session-preview.md, #1111): continuous frames from an OTA's camera
/// for framing, focusing, collimation or checking a flat panel's light, shown in the Preview mode and kept nowhere. The
/// capture loop is the planetary one (<see cref="PlanetaryCapture"/>, <see cref="LiveCaptureKind.LiveView"/>) over the whole
/// sensor, without the stack, the recenter or a recording. A client watches the frames through <c>/frames/live</c>
/// (<see cref="FrameSources.LiveView"/>) and turns exposure and gain through <c>PUT /api/v1/live/controls</c>.
/// </summary>
/// <remarks>
/// <para>The refusals come in the order <see cref="NodePlanetary"/>'s do: what is asked for first, a run going on, the
/// profile, a job working on the camera, then what the capture itself refuses (the OTA, the camera, and the claim, which
/// names whoever holds it). The run claims only the camera, so the focuser and the mount stay free to move while it runs,
/// which is what focusing and framing by eye need.</para>
/// <para>It ends by itself once no client has watched it for the detach grace (<see cref="NodeRunWatch"/>).</para>
/// </remarks>
internal sealed class NodeLiveView(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, NodeFrames frames, IExternal external,
    ITimeProvider timeProvider, ILogger<NodeLiveView> logger)
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = NodeRuns.HandedToTheNode)]
    public async Task<ResponseEnvelope<LiveViewStateDto>> StartAsync(LiveViewRequestDto request, CancellationToken cancellationToken)
    {
        if (Invalid(request.ExposureMs, request.Gain) is { } invalid)
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail(invalid);
        }
        if (request.Binning < 1)
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail("A binning is 1 or more");
        }
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail("The node has no active profile to show a live view of", 409);
        }
        if (request.OtaIndex >= 0 && request.OtaIndex < data.OTAs.Length && jobs.TryGetRunningOn(data.OTAs[request.OtaIndex].Camera, out var job))
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail($"The camera is busy: a {job.Kind} of it is running (job {job.Id})", 409);
        }

        // The run, and the camera it claims as it prepares, are this start's until the node owns them, and go back on every
        // other way out, a throw included. Nulled once handed on, the form CA2000 can follow.
        NodeLiveViewRun? run = new NodeLiveViewRun(frames, timeProvider, logger,
            onFault: failure => NodeRuns.NoteFault(hosted, timeProvider, "The live view", failure));
        try
        {
            // The whole sensor (a zero window) at the Preview's binning: a Canon's Live View unmagnified, any other camera full frame.
            var capture = new PlanetaryCaptureRequest(request.OtaIndex, TimeSpan.FromMilliseconds(request.ExposureMs), request.Gain, 0, 0,
                Bin: request.Binning);
            if (!run.TryPrepare(capture, data, hub, out var refusal))
            {
                return ResponseEnvelope<LiveViewStateDto>.Fail(refusal, 409);
            }

            if (!await hosted.TryStartAsync(run, profileId))
            {
                // Another run won the node between the check above and the start: the claim goes back with this one.
                return ResponseEnvelope<LiveViewStateDto>.Fail(NodeRuns.AlreadyGoingOn(hosted.RunningKind), 409);
            }

            // The node's from here: it disposes the run when the next one replaces it, or as the host stops.
            var started = run;
            run = null;
            logger.LogInformation("Live view of {Camera} at {Exposure} ms", started.State.Camera, request.ExposureMs);
            return ResponseEnvelope<LiveViewStateDto>.Accepted(started.State);
        }
        finally
        {
            if (run is not null)
            {
                await run.DisposeAsync();
            }
        }
    }

    /// <summary>The live view going on, or the last one to end until the node's next run replaces it.</summary>
    public ResponseEnvelope<LiveViewStateDto> State()
        => hosted.CurrentRun is NodeLiveViewRun run
            ? ResponseEnvelope<LiveViewStateDto>.Ok(run.State)
            : ResponseEnvelope<LiveViewStateDto>.NotFound("The node has run no live view since its last run");

    /// <summary>Stages a change to the live view going on, which it takes after its next frame.</summary>
    public ResponseEnvelope<LiveViewStateDto> Controls(LiveViewControlsDto controls)
    {
        if (Invalid(controls.ExposureMs ?? 1, controls.Gain) is { } invalid)
        {
            return ResponseEnvelope<LiveViewStateDto>.Fail(invalid);
        }
        if (hosted.CurrentRun is not NodeLiveViewRun { IsRunning: true } run)
        {
            return ResponseEnvelope<LiveViewStateDto>.NotFound("No live view is running");
        }

        run.Apply(controls);
        return ResponseEnvelope<LiveViewStateDto>.Ok(run.State);
    }

    /// <summary>Ends the live view going on, and answers once it has: the camera is free for a still from then on.</summary>
    public async Task<ResponseEnvelope<LiveViewStateDto>> StopAsync(CancellationToken cancellationToken)
    {
        // Only a live view: a stop meant for one must never abort a run that replaced it.
        if (hosted.CurrentRun is not NodeLiveViewRun { IsRunning: true } run || hosted.TryAbort(run) is not { } ended)
        {
            return ResponseEnvelope<LiveViewStateDto>.NotFound("No live view is running");
        }

        await ended.WaitAsync(cancellationToken);
        return ResponseEnvelope<LiveViewStateDto>.Ok(run.State);
    }

    // What the live view is asked to stream at, checked before anything is touched.
    private static string? Invalid(double exposureMs, short? gain)
        => !double.IsFinite(exposureMs) || exposureMs <= 0 ? "A live view needs a positive exposure"
            : gain is < 0 ? "A gain is 0 or more"
            : null;
}

/// <summary>
/// One live view, as the node runs it: the capture loop, keeping no frames, and the newest frame shown at display rate
/// (<see cref="FrameSources.LiveView"/>).
/// </summary>
internal sealed class NodeLiveViewRun : INodeRun
{
    private readonly NodeFrames _frames;
    private readonly ITimeProvider _timeProvider;
    private readonly FrameSampler _live;
    private int _otaIndex;
    private string _camera = "";
    private int _width;
    private int _height;
    private int _ended;

    private readonly Action<string?>? _onFault;

    /// <param name="onFault">Told how the run ended once it has, with the capture's failure, or null when it ended on a stop.</param>
    public NodeLiveViewRun(NodeFrames frames, ITimeProvider timeProvider, ILogger logger, Action<string?>? onFault = null)
    {
        _onFault = onFault;
        _frames = frames;
        _timeProvider = timeProvider;
        // A display's rate, as the planetary live frame is shown at: a client asks for each frame, so a slower view takes fewer.
        _live = new FrameSampler(timeProvider, NodePlanetaryRun.LiveFrameInterval, nameof(NodeLiveViewRun) + ".Live");
        Capture = new PlanetaryCapture(timeProvider, logger, onFrame: Show, kind: LiveCaptureKind.LiveView);
    }

    /// <summary>The capture loop, which the tests step frame by frame.</summary>
    internal PlanetaryCapture Capture { get; }

    public NodeRunKind Kind => NodeRunKind.LiveView;

    /// <summary>A live view nobody watches is a camera held for nobody.</summary>
    public bool EndsUnwatched => true;

    /// <summary>Until its body has ended, whether or not it has begun: the node releases it at once.</summary>
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public LiveViewStateDto State => new LiveViewStateDto
    {
        OtaIndex = _otaIndex,
        Camera = _camera,
        Width = Volatile.Read(ref _width),
        Height = Volatile.Read(ref _height),
        Running = IsRunning,
        FramesReceived = Capture.FramesReceived,
        DroppedFrames = Capture.DroppedFrames,
        FramesPerSecond = JsonNumber.OrNull(Capture.MeasuredFps),
        BitDepth = Capture.FrameBitDepth,
        FailureReason = Capture.FailureReason,
    };

    /// <summary>Makes the live view ready on the profile's devices, holding the camera from here (<see cref="PlanetaryCapture.TryPrepare"/>).</summary>
    public bool TryPrepare(in PlanetaryCaptureRequest request, ProfileData profile, IDeviceHub hub, [NotNullWhen(false)] out string? refusal)
    {
        if (!Capture.TryPrepare(request, profile, hub, out _, out refusal))
        {
            return false;
        }
        _otaIndex = request.OtaIndex;
        _camera = Capture.Camera?.Name ?? "";
        return true;
    }

    /// <summary>Stages each control the client gave, for the capture to take after its next frame.</summary>
    public void Apply(LiveViewControlsDto controls)
    {
        if (controls.ExposureMs is { } exposureMs)
        {
            Capture.SetExposure(TimeSpan.FromMilliseconds(exposureMs));
        }
        if (controls.Gain is { } gain)
        {
            Capture.SetGain(gain);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Capture.StartPrepared(cancellationToken))
            {
                throw new InvalidOperationException("The live view was not made ready before its run");
            }
            await Capture.Completion;
        }
        finally
        {
            // The camera and its claim are given back as the body ends.
            await Capture.DisposeAsync();
            Volatile.Write(ref _ended, 1);
            _onFault?.Invoke(Capture.FailureReason);
        }
    }

    /// <summary>Gives the camera back for a run whose body never ran; the body does its own.</summary>
    public ValueTask DisposeAsync() => Capture.DisposeAsync();

    // On the capture loop, with each frame BORROWED: shown as a copy at display rate.
    private void Show(Image frame)
    {
        Volatile.Write(ref _width, frame.Width);
        Volatile.Write(ref _height, frame.Height);
        if (_live.TrySample(frame, _timeProvider.GetUtcNow(), out var sample))
        {
            _frames.Publish(FrameSources.LiveView, sample);
        }
    }
}
