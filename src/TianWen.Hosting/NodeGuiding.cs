using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting;

/// <summary>
/// Calibrating and guiding outside a session, off the Guider tab's loop of the guide camera (#1915, docs/plans/live-session-preview.md,
/// P17): a run the node owns, so a client anywhere can do what PHD2's Loop, Calibrate and Guide do. It claims the mount and
/// the guide camera for its life, REPLACES the node's own guide-camera loop (stopped first, its exposure, gain and binning
/// and the star the client chose handed on), and puts the loop back as it ends, whether it ended on a stop, by itself (a
/// calibration), on a fault or because nobody watched.
/// </summary>
/// <remarks>
/// <para>The refusals come in the other runs' order: what is asked for, a run going on (a guide-camera loop is the one run it
/// replaces; any other is named), the profile, the devices, a job working on one of them, then the claim. Everything that can
/// be known is checked BEFORE the loop is stopped, and a refusal after it puts the loop back, so a refused start never costs
/// the picture the user was looking at.</para>
/// <para>The star is the chosen one or none: a guider that would pick its own is not asked to guide on a chosen one
/// (<see cref="IGuider.GuideAsync(double, double, double, GuideStartOptions, CancellationToken)"/> refuses in words), and PHD2
/// is refused here outright, since it owns its camera and its star.</para>
/// </remarks>
internal sealed class NodeGuiding(IDeviceHub hub, NodeJobs jobs, IHostedSession hosted, NodeLiveView live, IExternal external,
    ITimeProvider timeProvider, IHostApplicationLifetime lifetime, ILogger<NodeGuiding> logger)
{
    /// <summary>What the claim on the mount and the guide camera is called, which a refusal names.</summary>
    public const string LeaseOwner = "guiding";

    // What the node remembers of the last run once it has ended and the loop has replaced it: the calibration the guider holds
    // and why the run failed, so the tab still shows the result beside the loop.
    private volatile GuiderCalibrationSummary? _lastCalibration;
    private volatile string? _lastFailure;
    private volatile string _lastCamera = "";

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = NodeRuns.HandedToTheNode)]
    public async Task<ResponseEnvelope<GuidingStateDto>> StartAsync(GuideRunMode mode, GuidingRequestDto request, CancellationToken cancellationToken)
    {
        if (Invalid(mode, request) is { } invalid)
        {
            return ResponseEnvelope<GuidingStateDto>.Fail(invalid);
        }

        // A guide-camera loop is the one run this replaces; any other run, a guiding one included, is named.
        var loop = hosted.CurrentRun is NodeLiveViewRun { IsRunning: true, IsGuideCamera: true } running ? running : null;
        if (loop is null && hosted.RunningKind is { } going)
        {
            return ResponseEnvelope<GuidingStateDto>.Fail(NodeRuns.AlreadyGoingOn(going), 409);
        }
        if (hosted.ActiveProfileId is not { } profileId || await Profile.TryReadDataAsync(external, profileId, cancellationToken) is not { } data)
        {
            return ResponseEnvelope<GuidingStateDto>.Fail("The node has no active profile to guide", 409);
        }
        if (!TryResolve(data, out var guider, out var mount, out var camera, out var cameraUri, out var refusal))
        {
            return ResponseEnvelope<GuidingStateDto>.Fail(refusal, 409);
        }
        if (await ConnectGuiderAsync(data, cancellationToken) is not { } deviceGuider)
        {
            return ResponseEnvelope<GuidingStateDto>.Fail($"{guider.DisplayName} is not a guider this node can run outside a session", 409);
        }
        foreach (var device in (Uri[])[data.Mount, cameraUri])
        {
            if (jobs.TryGetRunningOn(device, out var job))
            {
                var name = hub.TryGetDeviceFromUri(device, out var busy) ? busy.DisplayName : device.ToString();
                return ResponseEnvelope<GuidingStateDto>.Fail($"{name} is busy: a {job.Kind} of it is running (job {job.Id})", 409);
            }
        }

        // What the loop ran at carries on, unless the client says otherwise; none to carry on from, the guider's own.
        var exposure = request.ExposureMs is { } ms ? TimeSpan.FromMilliseconds(ms) : loop?.Exposure;
        var gain = request.Gain ?? loop?.Gain;
        var binning = loop?.Binning ?? 1;
        var restore = loop is null ? null : new LoopSettings((exposure ?? TimeSpan.FromSeconds(1)).TotalMilliseconds, gain, binning);

        // The loop holds the camera: it ends, and answers once it has, before the claim is asked for.
        if (loop is not null && hosted.TryAbort(loop) is { } ended)
        {
            await ended.WaitAsync(cancellationToken);
        }

        DeviceLeaseSet? claim = null;
        NodeGuidingRun? run = null;
        var handedOver = false;
        try
        {
            if (!DeviceLeaseSet.TryAcquire(hub, [data.Mount, cameraUri], LeaseOwner, out claim, out var verdict))
            {
                return await RefusedAsync(restore, verdict.Describe());
            }

            // The guide scope's focal length, as a session sets it (explicit profile value, else the OAG's parent OTA): the guider's
            // pixel scale, and so its rates in arcseconds, are read off it.
            if (data.GuiderFocalLength is > 0)
            {
                camera.FocalLength = data.GuiderFocalLength.Value;
            }
            else if (data.OAG_OTA_Index is { } oag && oag >= 0 && oag < data.OTAs.Length && data.OTAs[oag].FocalLength > 0)
            {
                camera.FocalLength = data.OTAs[oag].FocalLength;
            }

            // The loop's own readout of the sensor, so the chosen star's pixel is the one it was clicked on.
            PlanetaryCapture.ConfigureRoi(camera, 0, 0, binning);
            if (gain is { } wanted)
            {
                await camera.SetGainAsync(wanted, cancellationToken);
            }
            deviceGuider.LinkDevices(mount, camera);

            var star = request.StarX is { } x && request.StarY is { } y ? (x, y) : ((double X, double Y)?)null;
            run = new NodeGuidingRun(mode, guider: deviceGuider, camera.Name, claim, new GuideStartOptions(star, exposure), request, restore, timeProvider, logger,
                onEnded: Ended);
            claim = null;
            if (!await hosted.TryStartAsync(run, profileId))
            {
                // Another run won the node between the check above and the start: the claim goes back with this one.
                await run.DisposeAsync();
                run = null;
                return await RefusedAsync(restore, NodeRuns.AlreadyGoingOn(hosted.RunningKind));
            }

            // The node's from here: it disposes the run when the next one replaces it, or as the host stops.
            var started = run;
            run = null;
            handedOver = true;
            _lastFailure = null;
            logger.LogInformation("Guiding run ({Mode}) of {Camera} at {Exposure}{Star}", mode, started.CameraName,
                exposure is { } e ? $"{e.TotalMilliseconds:0} ms" : "the guider's exposure", star is { } s ? $", on the star at ({s.X:0.0}, {s.Y:0.0})" : "");
            _ = RestoreWhenEndedAsync(started);
            return ResponseEnvelope<GuidingStateDto>.Accepted(started.State(_lastCalibration));
        }
        catch when (!handedOver)
        {
            // Whatever threw, the loop that was stopped for this start goes back.
            claim?.Dispose();
            claim = null;
            if (run is not null)
            {
                await run.DisposeAsync();
                run = null;
            }
            await StartLoopAsync(restore);
            throw;
        }
        finally
        {
            claim?.Dispose();
            if (run is not null)
            {
                await run.DisposeAsync();
            }
        }
    }

    /// <summary>The guiding run going on, or the node's memory of the last one: its calibration, and why it failed when it did.</summary>
    public ResponseEnvelope<GuidingStateDto> State()
        => ResponseEnvelope<GuidingStateDto>.Ok(hosted.CurrentRun is NodeGuidingRun run
            ? run.State(_lastCalibration)
            : new GuidingStateDto
            {
                Camera = _lastCamera,
                FailureReason = _lastFailure,
                Calibration = _lastCalibration is { } calibration ? GuiderCalibrationDto.From(calibration) : null,
            });

    /// <summary>Ends the guiding run going on and answers once it has, with the loop back where there was one.</summary>
    public async Task<ResponseEnvelope<GuidingStateDto>> StopAsync(CancellationToken cancellationToken)
    {
        // Only a guiding run: a stop meant for one must never abort a run that replaced it.
        if (hosted.CurrentRun is not NodeGuidingRun { IsRunning: true } run || hosted.TryAbort(run) is not { } ended)
        {
            return ResponseEnvelope<GuidingStateDto>.NotFound("No guiding run is going on");
        }

        await ended.WaitAsync(cancellationToken);
        await RestoreAsync(run);
        return State();
    }

    private void Ended(NodeGuidingRun run, GuiderCalibrationSummary? calibration, string? failure)
    {
        _lastCalibration = calibration ?? _lastCalibration;
        _lastFailure = failure;
        _lastCamera = run.CameraName;
        NodeRuns.NoteFault(hosted, timeProvider, "Guiding", failure);
    }

    private async Task<ResponseEnvelope<GuidingStateDto>> RefusedAsync(LoopSettings? restore, string refusal)
    {
        // The loop was stopped to make room: it goes back, since the start that stopped it did not happen.
        await StartLoopAsync(restore);
        return ResponseEnvelope<GuidingStateDto>.Fail(refusal, 409);
    }

    // The run ending by itself (a calibration, a fault, nobody watching) puts the loop back as a stop does.
    private async Task RestoreWhenEndedAsync(NodeGuidingRun run)
    {
        try
        {
            await run.Completion;
            await RestoreAsync(run);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Putting the guide camera's loop back after guiding failed");
        }
    }

    private async Task RestoreAsync(NodeGuidingRun run)
    {
        if (!run.TryTakeRestore(out var restore))
        {
            return;
        }

        // The node's body of the run has ended before the loop may claim the camera again: it lets go of the run a moment later.
        for (var waited = 0; waited < 250 && hosted.IsRunning && !lifetime.ApplicationStopping.IsCancellationRequested; waited++)
        {
            await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        }
        await StartLoopAsync(restore);
    }

    private async Task StartLoopAsync(LoopSettings? settings)
    {
        if (settings is null || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        var started = await live.StartAsync(new LiveViewRequestDto
        {
            CameraRole = LiveViewCamera.Guider,
            ExposureMs = settings.ExposureMs,
            Gain = settings.Gain,
            Binning = settings.Binning,
        }, lifetime.ApplicationStopping);
        if (!started.Success)
        {
            // Another run won the node, or the camera is gone: nothing here is owed the loop, but a log says it is not back.
            logger.LogInformation("The guide camera's loop was not put back after guiding: {Reason}", started.Error);
        }
    }

    // What the profile names, connected: the guider (never PHD2), the mount and the guide camera, with the camera's URI.
    private bool TryResolve(ProfileData data, [NotNullWhen(true)] out DeviceBase? guider, [NotNullWhen(true)] out IMountDriver? mount,
        [NotNullWhen(true)] out ICameraDriver? camera, [NotNullWhen(true)] out Uri? cameraUri, [NotNullWhen(false)] out string? refusal)
    {
        guider = null;
        mount = null;
        camera = null;
        cameraUri = null;
        if (data.Guider is not { Scheme: not "none" } guiderUri || guiderUri.Host.Equals(nameof(NoneDevice), StringComparison.OrdinalIgnoreCase))
        {
            refusal = "The profile has no guider";
            return false;
        }
        // A URI's host is lower-cased, so the class name is compared without case.
        if (string.Equals(guiderUri.Host, nameof(OpenPHD2GuiderDevice), StringComparison.OrdinalIgnoreCase))
        {
            refusal = "PHD2 owns the guide camera and chooses its star, so there is nothing to calibrate or guide here: do it in PHD2";
            return false;
        }
        if (!hub.TryGetDeviceFromUri(guiderUri, out guider))
        {
            refusal = "The profile's guider is not one this node can start";
            return false;
        }
        if (data.GuiderCamera is not { Scheme: not "none" } guideCameraUri)
        {
            refusal = "The profile has no guide camera to guide with";
            return false;
        }
        if (!hub.TryGetConnectedDriver<IMountDriver>(data.Mount, out mount))
        {
            refusal = "Connect the mount to calibrate or guide";
            return false;
        }
        if (!hub.TryGetConnectedDriver<ICameraDriver>(guideCameraUri, out camera))
        {
            refusal = "Connect the guide camera to calibrate or guide";
            return false;
        }
        cameraUri = guideCameraUri;
        refusal = null;
        return true;
    }

    private async Task<IDeviceDependentGuider?> ConnectGuiderAsync(ProfileData data, CancellationToken cancellationToken)
        => hub.TryGetDeviceFromUri(data.Guider, out var device) && await hub.ConnectAsync(device, cancellationToken) is IDeviceDependentGuider guider
            ? guider
            : null;

    private static string? Invalid(GuideRunMode mode, GuidingRequestDto request)
    {
        if (!Enum.IsDefined(mode))
        {
            return "A guiding run calibrates or guides";
        }
        if ((request.StarX is null) != (request.StarY is null))
        {
            return "A chosen star needs both its X and its Y";
        }
        if (request.StarX is { } x && request.StarY is { } y && (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0))
        {
            return "A chosen star's position is 0 or more pixels, both ways";
        }
        if (request.ExposureMs is { } exposure && (!double.IsFinite(exposure) || exposure <= 0))
        {
            return "A guide exposure is positive";
        }
        if (request.Gain is < 0)
        {
            return "A gain is 0 or more";
        }
        if (!double.IsFinite(request.SettlePixels) || request.SettlePixels <= 0
            || !double.IsFinite(request.SettleTimeSeconds) || request.SettleTimeSeconds <= 0
            || !double.IsFinite(request.SettleTimeoutSeconds) || request.SettleTimeoutSeconds <= 0)
        {
            return "A settle is a positive number of pixels, seconds and seconds before giving up";
        }
        return null;
    }
}

/// <summary>What the guide camera's loop ran at, to put it back as it was.</summary>
internal sealed record LoopSettings(double ExposureMs, short? Gain, short Binning);

/// <summary>
/// One guiding run, as the node runs it (#1915): a calibration on demand, or guiding until stopped, on the guider its
/// profile names, with the guide camera and the mount claimed from the start.
/// </summary>
internal sealed class NodeGuidingRun : INodeRun
{
    // The steps the Guider tab's graph holds: a session's own ring is as long.
    private const int SampleCapacity = ISessionTelemetry.GuideSampleCapacity;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // What a poll of the guider read: its asynchronous answers, kept as one value so a state never pairs two polls.
    private sealed record Polled(GuidingPhase Phase, string? AppState, GuideStats? Stats, SettleProgress? Settle, TimeSpan Exposure);

    private readonly GuideRunMode _mode;
    private readonly IGuider _guider;
    private readonly DeviceLeaseSet _claim;
    private readonly GuideStartOptions _start;
    private readonly GuidingRequestDto _request;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Action<NodeGuidingRun, GuiderCalibrationSummary?, string?> _onEnded;
    private readonly Node.Lib.CircularBuffer<GuideErrorSample> _samples = new Node.Lib.CircularBuffer<GuideErrorSample>(SampleCapacity);
    private readonly TaskCompletionSource _completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private LoopSettings? _restore;
    private volatile Polled _polled = new Polled(GuidingPhase.Starting, null, null, null, TimeSpan.Zero);
    private volatile string? _failure;
    private int _ended;
    private int _disposed;

    public NodeGuidingRun(GuideRunMode mode, IGuider guider, string cameraName, DeviceLeaseSet claim, GuideStartOptions start, GuidingRequestDto request,
        LoopSettings? restore, ITimeProvider timeProvider, ILogger logger, Action<NodeGuidingRun, GuiderCalibrationSummary?, string?> onEnded)
    {
        _mode = mode;
        _guider = guider;
        CameraName = cameraName;
        _claim = claim;
        _start = start;
        _request = request;
        _restore = restore;
        _timeProvider = timeProvider;
        _logger = logger;
        _onEnded = onEnded;
    }

    public NodeRunKind Kind => NodeRunKind.Guiding;

    /// <summary>Interactive: the Guider tab is where it is watched, and a window that went away must not leave the mount pulsing for nobody.</summary>
    public bool EndsUnwatched => true;

    /// <summary>Until its body has ended, whether or not it has begun: the node releases it at once.</summary>
    public bool IsRunning => Volatile.Read(ref _ended) == 0;

    public string CameraName { get; }

    /// <summary>Completes once the body has ended, or the run was disposed without one.</summary>
    public Task Completion => _completed.Task;

    /// <summary>The guider's newest frame while the run goes on (the guider's source, <see cref="NodeFrames.Guider"/>); null once it has ended.</summary>
    public Lib.Imaging.Image? LastGuideFrame => IsRunning ? _guider.LastGuideFrame : null;

    /// <summary>Takes the loop to put back, once: whichever of a stop and the run's own end asks first.</summary>
    public bool TryTakeRestore(out LoopSettings? restore)
    {
        restore = Interlocked.Exchange(ref _restore, null);
        return restore is not null;
    }

    public GuidingStateDto State(GuiderCalibrationSummary? remembered)
    {
        var polled = _polled;
        var running = IsRunning;
        var phase = !running ? GuidingPhase.Idle : _mode is GuideRunMode.Calibrate ? GuidingPhase.Calibrating : polled.Phase;
        var calibration = _guider.LastCalibration ?? remembered;
        return new GuidingStateDto
        {
            Running = running,
            Mode = _mode,
            Phase = phase,
            Camera = CameraName,
            StarX = _start.Star is { } star ? JsonNumber.OrNull(star.X) : null,
            StarY = _start.Star is { } at ? JsonNumber.OrNull(at.Y) : null,
            FailureReason = _failure,
            Calibration = calibration is { } summary ? GuiderCalibrationDto.From(summary) : null,
            Guider = GuiderStateDto.FromGuider(_guider, _mode is GuideRunMode.Calibrate && running ? "Calibrating" : polled.AppState,
                polled.Stats, polled.Settle, _samples.Snapshot, polled.Exposure > TimeSpan.Zero ? polled.Exposure : _start.Exposure ?? TimeSpan.Zero),
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        void OnCorrection(object? sender, GuideCorrectionEventArgs e)
        {
            // A correction with no measured error appends nothing: null is not zero (#821).
            if (e is { RaError: { } ra, DecError: { } dec })
            {
                _samples.Add(new GuideErrorSample(e.FrameTime, ra, dec, e.RaCorrectionMs, e.DecCorrectionMs, IsDither: false,
                    IsSettling: _polled.Phase is GuidingPhase.Settling));
            }
        }
        void OnError(object? sender, GuidingErrorEventArgs e) => _failure ??= e.Message;

        GuiderCalibrationSummary? calibration = null;
        _guider.GuideCorrectionEvent += OnCorrection;
        _guider.GuidingErrorEvent += OnError;
        try
        {
            if (_mode is GuideRunMode.Calibrate)
            {
                calibration = await _guider.CalibrateAsync(_start, cancellationToken);
                _failure ??= calibration is null
                    ? "Calibration found no usable guide star, or measured nothing the quality gates accept (cloud, poor seeing, or too little star movement)"
                    : null;
            }
            else
            {
                await _guider.GuideAsync(_request.SettlePixels, _request.SettleTimeSeconds, _request.SettleTimeoutSeconds, _start, cancellationToken);
                await GuideUntilStoppedAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A stop, the detach grace or the host stopping: how a run ends, never a failure.
        }
        catch (GuiderException ex)
        {
            _failure ??= ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guiding failed");
            _failure ??= ex.Message;
        }
        finally
        {
            _guider.GuideCorrectionEvent -= OnCorrection;
            _guider.GuidingErrorEvent -= OnError;
            try
            {
                // Waits for the guide loop to be gone before the camera is given back: two consumers of one camera is the bug.
                await _guider.StopCaptureAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping the guider failed");
            }

            calibration ??= _guider.LastCalibration;
            Volatile.Write(ref _ended, 1);
            _claim.Dispose();
            _onEnded(this, calibration, _failure);
            _completed.TrySetResult();
        }
    }

    // The guider drives its own loop; what this does is read it, since its settle completes only as it is asked.
    private async Task GuideUntilStoppedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (appState, _) = await _guider.GetStatusAsync(cancellationToken);
            var stats = await _guider.GetStatsAsync(cancellationToken);
            var settle = await _guider.GetSettleProgressAsync(cancellationToken);
            var exposure = await _guider.ExposureTimeAsync(cancellationToken);
            var phase = appState switch
            {
                "Calibrating" => GuidingPhase.Calibrating,
                "Settling" => GuidingPhase.Settling,
                "Guiding" => GuidingPhase.Guiding,
                "LostLock" => GuidingPhase.LostLock,
                _ => GuidingPhase.Starting,
            };
            _polled = new Polled(phase, appState, stats, settle, exposure);

            // The guider reports Stopped once its loop has ended on its own: it never ran, or it failed, and says why through its error event.
            if (appState is "Stopped")
            {
                _failure ??= "The guider stopped";
                return;
            }

            await _timeProvider.SleepAsync(PollInterval, cancellationToken);
        }
    }

    /// <summary>Gives the claim back for a run whose body never ran; the body does its own.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _claim.Dispose();
            _completed.TrySetResult();
        }
        return ValueTask.CompletedTask;
    }
}
