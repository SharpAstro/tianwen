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
/// A live planetary capture as the node's run (P5 part 5 of docs/plans/hardware-in-the-server.md, #934): the GUI's own
/// capture loop, <see cref="PlanetaryCapture"/>, through the node's camera, and the rolling stack the GUI draws from, run
/// here instead (decision 5: only the master and a live frame cross). A client watches both through <c>/frames</c>
/// (<see cref="FrameSources.PlanetaryLive"/>, <see cref="FrameSources.PlanetaryMaster"/>) and turns the knobs through
/// <c>PUT /api/v1/planetary/controls</c>.
/// </summary>
/// <remarks>
/// <para>The refusals come in the device plane's order: what is asked for first, a run going on, the profile, a job
/// working on the camera, then what the capture itself refuses (the OTA, the camera, and the claim, which names whoever
/// holds it).</para>
/// <para>It ends by itself once no client has watched it for the detach grace (<see cref="NodeRunWatch"/>): a live view
/// nobody sees holds a camera for nobody.</para>
/// </remarks>
internal sealed class NodePlanetary(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, NodeFrames frames, IExternal external,
    ITimeProvider timeProvider, ILogger<NodePlanetary> logger)
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = NodeRuns.HandedToTheNode)]
    public async Task<ResponseEnvelope<PlanetaryStateDto>> StartAsync(PlanetaryRequestDto request, CancellationToken cancellationToken)
    {
        if (Invalid(request.ExposureMs, request.Gain, request.RoiWidth, request.RoiHeight) is { } invalid)
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail(invalid);
        }
        if (hosted.RunningKind is { } running)
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail(NodeRuns.AlreadyGoingOn(running), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail("The node has no active profile to capture with", 409);
        }
        if (request.OtaIndex >= 0 && request.OtaIndex < data.OTAs.Length && jobs.TryGetRunningOn(data.OTAs[request.OtaIndex].Camera, out var job))
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail($"The camera is busy: a {job.Kind} of it is running (job {job.Id})", 409);
        }

        // The run, and the camera it claims as it prepares, are this start's until the node owns them, and go back on every
        // other way out, a throw included. Nulled once handed on, the form CA2000 can follow.
        NodePlanetaryRun? run = new NodePlanetaryRun(frames, timeProvider, logger);
        try
        {
            var capture = new PlanetaryCaptureRequest(request.OtaIndex, TimeSpan.FromMilliseconds(request.ExposureMs), request.Gain,
                request.RoiWidth, request.RoiHeight, request.BitDepth, request.HighSpeed);
            if (!run.TryPrepare(capture, data, hub, out var refusal))
            {
                return ResponseEnvelope<PlanetaryStateDto>.Fail(refusal, 409);
            }
            run.Configure(request.Recenter ?? new PlanetaryRecenterDto());

            if (!await hosted.TryStartAsync(run, profileId))
            {
                // Another run won the node between the check above and the start: the claim goes back with this one.
                return ResponseEnvelope<PlanetaryStateDto>.Fail(NodeRuns.AlreadyGoingOn(hosted.RunningKind), 409);
            }

            // The node's from here: it disposes the run when the next one replaces it, or as the host stops.
            var started = run;
            run = null;
            logger.LogInformation("Planetary capture with {Camera} at {Exposure} ms", started.State.Camera, request.ExposureMs);
            return ResponseEnvelope<PlanetaryStateDto>.Accepted(started.State);
        }
        finally
        {
            if (run is not null)
            {
                await run.DisposeAsync();
            }
        }
    }

    /// <summary>The planetary capture going on, or the last one to end until the node's next run replaces it.</summary>
    public ResponseEnvelope<PlanetaryStateDto> State()
        => hosted.CurrentRun is NodePlanetaryRun run
            ? ResponseEnvelope<PlanetaryStateDto>.Ok(run.State)
            : ResponseEnvelope<PlanetaryStateDto>.NotFound("The node has run no planetary capture since its last run");

    /// <summary>Stages a change to the capture going on, which it takes after its next frame.</summary>
    public ResponseEnvelope<PlanetaryStateDto> Controls(PlanetaryControlsDto controls)
    {
        if ((controls.RoiWidth is null) != (controls.RoiHeight is null))
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail("A readout window is resized by its width and its height together");
        }
        if (Invalid(controls.ExposureMs ?? 1, controls.Gain, controls.RoiWidth ?? 1, controls.RoiHeight ?? 1) is { } invalid)
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail(invalid);
        }
        if (hosted.CurrentRun is not NodePlanetaryRun { IsRunning: true } run)
        {
            return ResponseEnvelope<PlanetaryStateDto>.NotFound("No planetary capture is running");
        }

        run.Apply(controls);
        return ResponseEnvelope<PlanetaryStateDto>.Ok(run.State);
    }

    /// <summary>Ends the planetary capture going on, and answers once it has: a capture drains within a frame or two.</summary>
    public async Task<ResponseEnvelope<PlanetaryStateDto>> StopAsync(CancellationToken cancellationToken)
    {
        // Only a planetary capture: a stop meant for one must never abort a run that replaced it.
        if (hosted.CurrentRun is not NodePlanetaryRun { IsRunning: true } run || hosted.TryAbort(run) is not { } ended)
        {
            return ResponseEnvelope<PlanetaryStateDto>.NotFound("No planetary capture is running");
        }

        await ended.WaitAsync(cancellationToken);
        return ResponseEnvelope<PlanetaryStateDto>.Ok(run.State);
    }

    /// <summary>
    /// Starts recording the capture going on to a SER file under the node's image folder
    /// (<see cref="PlanetaryCapture.RecordingPath"/>); it finishes its duration whether or not anyone watches.
    /// </summary>
    public ResponseEnvelope<PlanetaryStateDto> Record(PlanetaryRecordRequestDto request)
    {
        if (!double.IsFinite(request.DurationSeconds) || request.DurationSeconds <= 0)
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail("A recording needs a positive duration");
        }
        if (hosted.CurrentRun is not NodePlanetaryRun { IsRunning: true } run)
        {
            return ResponseEnvelope<PlanetaryStateDto>.NotFound("No planetary capture is running to record");
        }

        var path = PlanetaryCapture.RecordingPath(external, run.OtaIndex, timeProvider.GetUtcNow());
        if (!run.TryStartRecording(path, TimeSpan.FromSeconds(request.DurationSeconds), out var refusal))
        {
            return ResponseEnvelope<PlanetaryStateDto>.Fail(refusal, 409);
        }
        return ResponseEnvelope<PlanetaryStateDto>.Ok(run.State);
    }

    /// <summary>Ends the recording going on sooner than its duration; the capture goes on.</summary>
    public ResponseEnvelope<PlanetaryStateDto> StopRecording()
    {
        if (hosted.CurrentRun is not NodePlanetaryRun { IsRunning: true } run || run.Capture.Recording is not { IsRecording: true })
        {
            return ResponseEnvelope<PlanetaryStateDto>.NotFound("No recording is being made");
        }

        run.Capture.StopRecording();
        return ResponseEnvelope<PlanetaryStateDto>.Ok(run.State);
    }

    // What the capture is asked to stream at, checked before anything is touched: the camera would take none of these.
    private static string? Invalid(double exposureMs, short? gain, int roiWidth, int roiHeight)
        => !double.IsFinite(exposureMs) || exposureMs <= 0 ? "A planetary capture needs a positive exposure"
            : gain is < 0 ? "A gain is 0 or more"
            : roiWidth < 1 || roiHeight < 1 ? "A readout window is at least one pixel each way"
            : null;
}

/// <summary>
/// One live planetary capture, as the node runs it: the capture loop, the rolling stack over what it streams, and the
/// frames a client watches. The stack runs on the run's own task, one master at a time, so the stacker has its single
/// writer and nothing reads the capture's stream once the run is done.
/// </summary>
internal sealed class NodePlanetaryRun : INodeRun
{
    /// <summary>
    /// How often the live frame is shown at most: a display's rate, not the camera's. A client streaming it asks for each
    /// frame (P5 part 5c), so a slower view simply takes fewer, and the copy is into planes the sampler recycles.
    /// </summary>
    internal static readonly TimeSpan LiveFrameInterval = TimeSpan.FromMilliseconds(33);

    private readonly NodeFrames _frames;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly RollingWindowOptions _stackOptions;
    private readonly FrameSampler _live;
    private int _otaIndex;
    private string _camera = "";
    private int _roiWidth;
    private int _roiHeight;
    private int _masters;
    private int _stackedFrames;
    private int _ended;

    public NodePlanetaryRun(NodeFrames frames, ITimeProvider timeProvider, ILogger logger, RollingWindowOptions? stackOptions = null)
    {
        _frames = frames;
        _timeProvider = timeProvider;
        _logger = logger;
        _stackOptions = stackOptions ?? new RollingWindowOptions();
        _live = new FrameSampler(timeProvider, LiveFrameInterval, nameof(NodePlanetaryRun) + ".Live");
        Capture = new PlanetaryCapture(timeProvider, logger, _stackOptions, onFrame: Show);
    }

    /// <summary>The capture loop, which the tests step frame by frame.</summary>
    internal PlanetaryCapture Capture { get; }

    public NodeRunKind Kind => NodeRunKind.Planetary;

    /// <summary>
    /// A live view: nobody watching is a camera held for nobody. Not while it RECORDS (P5 part 5d): a recording finishes
    /// its duration unwatched, and the live view left after it has a whole grace of its own (<see cref="NodeRunWatch"/>
    /// starts one when the run becomes interactive again).
    /// </summary>
    public bool EndsUnwatched => Capture.Recording is not { IsRecording: true };

    /// <summary>The OTA whose camera streams.</summary>
    internal int OtaIndex => _otaIndex;

    /// <summary>Starts recording what the capture streams (<see cref="PlanetaryCapture.TryStartRecording"/>).</summary>
    public bool TryStartRecording(string path, TimeSpan duration, [NotNullWhen(false)] out string? refusal)
        => Capture.TryStartRecording(path, duration, out _, out refusal);

    /// <summary>Until its body has ended, whether or not it has begun: the node releases it at once.</summary>
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public PlanetaryStateDto State
    {
        get
        {
            var (offsetX, offsetY) = Capture.LastComOffset;
            return new PlanetaryStateDto
            {
                OtaIndex = _otaIndex,
                Camera = _camera,
                RoiWidth = Volatile.Read(ref _roiWidth),
                RoiHeight = Volatile.Read(ref _roiHeight),
                Running = IsRunning,
                FramesReceived = Capture.FramesReceived,
                DroppedFrames = Capture.DroppedFrames,
                FramesPerSecond = JsonNumber.OrNull(Capture.MeasuredFps),
                BitDepth = Capture.FrameBitDepth,
                Masters = Volatile.Read(ref _masters),
                StackedFrames = Volatile.Read(ref _stackedFrames),
                OffsetX = JsonNumber.OrNull(offsetX),
                OffsetY = JsonNumber.OrNull(offsetY),
                RecenterActuator = Capture.LastRecenterActuator,
                FailureReason = Capture.FailureReason,
                Recording = Capture.Recording is { } recording ? PlanetaryRecordingDto.From(recording) : null,
            };
        }
    }

    /// <summary>Makes the capture ready on the profile's devices, holding the camera from here (<see cref="PlanetaryCapture.TryPrepare"/>).</summary>
    public bool TryPrepare(in PlanetaryCaptureRequest request, ProfileData profile, IDeviceHub hub, [NotNullWhen(false)] out string? refusal)
    {
        if (!Capture.TryPrepare(request, profile, hub, out var roi, out refusal))
        {
            return false;
        }
        _otaIndex = request.OtaIndex;
        _camera = Capture.Camera?.Name ?? "";
        (_roiWidth, _roiHeight) = roi;
        return true;
    }

    /// <summary>The recenter's settings, given whole.</summary>
    public void Configure(PlanetaryRecenterDto recenter)
        => Capture.ConfigureRecenter(recenter.Auto, recenter.MountJog, recenter.DeadbandPixels, recenter.Gain, recenter.FlipRa, recenter.FlipDec);

    /// <summary>Stages each control the client gave, for the capture to take after its next frame.</summary>
    public void Apply(PlanetaryControlsDto controls)
    {
        if (controls.ExposureMs is { } exposureMs)
        {
            Capture.SetExposure(TimeSpan.FromMilliseconds(exposureMs));
        }
        if (controls.Gain is { } gain)
        {
            Capture.SetGain(gain);
        }
        if (controls is { RoiWidth: { } width, RoiHeight: { } height })
        {
            Capture.SetRoiSize(width, height);
        }
        if (controls.JogX is not null || controls.JogY is not null)
        {
            Capture.JogRoi(controls.JogX ?? 0, controls.JogY ?? 0);
        }
        if (controls.Recenter is { } recenter)
        {
            Configure(recenter);
        }
        if (controls.BitDepth is { } bitDepth)
        {
            Capture.SetBitDepth(bitDepth);
        }
        if (controls.HighSpeed is { } highSpeed)
        {
            Capture.SetHighSpeed(highSpeed);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Capture.StartPrepared(cancellationToken))
            {
                throw new InvalidOperationException("The planetary capture was not made ready before its run");
            }
            await StackAsync(cancellationToken);
        }
        finally
        {
            // The stack has stopped reading the stream, so the capture may stop and let it go: the camera and its claim
            // are given back as the body ends.
            await Capture.DisposeAsync();
            Volatile.Write(ref _ended, 1);
        }
    }

    /// <summary>Gives the camera back for a run whose body never ran; the body does its own.</summary>
    public ValueTask DisposeAsync() => Capture.DisposeAsync();

    // On the capture loop, with each frame BORROWED: the live frame is a copy at display rate, and the window it came at
    // is what the state reports, a live resize included.
    private void Show(Image frame)
    {
        Volatile.Write(ref _roiWidth, frame.Width);
        Volatile.Write(ref _roiHeight, frame.Height);
        if (_live.TrySample(frame, _timeProvider.GetUtcNow(), out var sample))
        {
            _frames.Publish(FrameSources.PlanetaryLive, sample);
        }
    }

    // Stacks the window ending at the newest frame, again and again, until the run is stopped or the capture ends by
    // itself; each master becomes the planetary master. The loop is Lib's, one with the probe that measures it.
    private Task StackAsync(CancellationToken cancellationToken)
    {
        return LiveStackLoop.RunAsync(() => Capture.IsCapturing, () => Capture.Stream, _stackOptions, _timeProvider,
            (master, stacker, _) =>
            {
                Volatile.Write(ref _stackedFrames, stacker.WindowFrameCount);
                Interlocked.Increment(ref _masters);
                _frames.Publish(FrameSources.PlanetaryMaster, master);
            },
            // Dropped, and the next stack starts clean, as the GUI's does: the capture goes on.
            ex => _logger.LogWarning(ex, "A planetary stack failed; the next one starts again"),
            cancellationToken);
    }
}
