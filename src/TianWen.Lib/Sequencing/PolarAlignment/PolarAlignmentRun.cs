using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing.PolarAlignment;

/// <summary>
/// What a polar alignment run is asked to do: capture through OTA <paramref name="OtaIndex"/>'s camera, or through the
/// guider when <paramref name="UseGuider"/>, with <paramref name="Configuration"/>. An index outside the profile's OTAs
/// means the first.
/// </summary>
public readonly record struct PolarAlignmentRequest(int OtaIndex, bool UseGuider, PolarAlignmentConfiguration Configuration);

/// <summary>
/// Where a polar alignment run is, as one snapshot: the phase and its status line, Phase A's result once it has one, and
/// the latest refinement tick. <paramref name="FailureReason"/> is set when it failed, Phase A included, and kept after
/// it ends; <paramref name="Ended"/> once the mount has been restored and the devices given back.
/// </summary>
public sealed record PolarRunState(
    PolarAlignmentPhase Phase,
    string StatusMessage,
    TwoFrameSolveResult? PhaseA = null,
    LiveSolveResult? LastSolve = null,
    string? FailureReason = null,
    bool Ended = false);

/// <summary>
/// One polar alignment, for whichever host runs it: the GUI in its own process and the node for a client (P5 part 4 of
/// docs/plans/hardware-in-the-server.md, #934). <see cref="TryCreate"/> resolves the devices from the profile and CLAIMS
/// them, refusing in words; <see cref="RunAsync"/> runs Phase A and then refines until cancelled, and restores the mount
/// however it ends. The two hosts differ only in where the state and the frames go.
/// </summary>
/// <remarks>
/// <para><b>Cancellation is how it ENDS, not a failure.</b> Refinement runs until the user is done, and Done and Cancel
/// are the same exit: the token. So <see cref="RunAsync"/> returns normally on it, and throws only for a failure, which it
/// records in <see cref="State"/> first.</para>
/// <para><b>The claim goes after the restore</b>, since the restore moves the mount too: <see cref="PolarAlignmentSession"/>
/// releases it once the mount is back, and <see cref="DisposeAsync"/> does the same for a run whose body never ran.</para>
/// </remarks>
public sealed class PolarAlignmentRun : IAsyncDisposable
{
    /// <summary>What the claim on the mount and the capture devices is called, which a refusal names.</summary>
    public const string LeaseOwner = "polar alignment";

    private readonly PolarAlignmentSession _session;
    private readonly IGuider? _guider;
    private readonly ILogger _logger;
    private readonly Action<PlateSolveResult>? _onFrameSolved;
    private volatile PolarRunState _state = new PolarRunState(PolarAlignmentPhase.Idle, "Starting polar alignment\u2026");
    private int _disposed;

    private PolarAlignmentRun(PolarAlignmentSession session, IGuider? guider, int otaIndex, string sourceName, Action<PlateSolveResult>? onFrameSolved, ILogger logger)
    {
        _session = session;
        _guider = guider;
        OtaIndex = otaIndex;
        SourceName = sourceName;
        _onFrameSolved = onFrameSolved;
        _logger = logger;
    }

    /// <summary>The OTA whose camera it captures through, or -1 for the guider.</summary>
    public int OtaIndex { get; }

    /// <summary>What it captures through, in words.</summary>
    public string SourceName { get; }

    /// <summary>Where it is now.</summary>
    public PolarRunState State => _state;

    /// <summary>Raised on the run's own thread whenever <see cref="State"/> changes.</summary>
    public event Action<PolarRunState>? StateChanged;

    /// <summary>
    /// Resolves the mount and the capture source from <paramref name="profile"/>'s connected devices, and claims them,
    /// all or nothing; or says in <paramref name="refusal"/> why it cannot. Nothing is claimed on a refusal.
    /// </summary>
    /// <param name="onFrameCaptured">Given each frame an OTA's camera captures, and its OTA index, and OWNS it from there
    /// (the capture source does not release it). Null releases each frame. The guider's frames never come here.</param>
    /// <param name="onFrameSolved">Given each frame's plate solve, a failed one included (no solution), so a host drawing
    /// the frame never keeps a grid the frame no longer fits; and each refinement tick's solve.</param>
    public static bool TryCreate(in PolarAlignmentRequest request, ProfileData profile, IDeviceHub hub, IExternal external,
        ICelestialObjectDB catalog, IPlateSolver solver, ITimeProvider timeProvider, ILogger logger,
        Action<int, Image>? onFrameCaptured, Action<PlateSolveResult>? onFrameSolved,
        [NotNullWhen(true)] out PolarAlignmentRun? run, [NotNullWhen(false)] out string? refusal)
    {
        run = null;
        if (profile.OTAs.Length == 0)
        {
            refusal = "The profile has no OTA to align with";
            return false;
        }
        if (!hub.TryGetConnectedDriver<IMountDriver>(profile.Mount, out var mount))
        {
            refusal = "Mount not connected: connect a mount first";
            return false;
        }
        if (profile.SiteLatitude is not { } latitude || profile.SiteLongitude is not { } longitude)
        {
            refusal = "Site location not configured for this profile";
            return false;
        }
        if (!TryBuildCaptureSource(request, profile, hub, mount, external, catalog, timeProvider, logger, onFrameCaptured, onFrameSolved,
            out var source, out refusal))
        {
            return false;
        }

        // Claimed before anything is set up, so a start is refused while another run holds the mount or the camera (P0c
        // item 2 of docs/plans/hardware-in-the-server.md): without it nothing stopped a jog or a second run from moving the
        // mount polar was rotating.
        if (!DeviceLeaseSet.TryAcquire(hub, [profile.Mount, .. source.Drives], LeaseOwner, out var claim, out var verdict))
        {
            refusal = verdict.Describe();
            return false;
        }

        var site = BuildSite(profile, hub, latitude, longitude);
        PolarAlignmentSession session;
        try
        {
            session = new PolarAlignmentSession(external, mount, source.Source, solver, timeProvider, logger, site, request.Configuration, claim);
        }
        catch
        {
            claim.Dispose();
            throw;
        }
        run = new PolarAlignmentRun(session, source.Guider, source.OtaIndex, source.Source.DisplayName, onFrameSolved, logger);
        return true;
    }

    /// <summary>
    /// Phase A, then refinement until <paramref name="cancellationToken"/> fires, then the mount restored and the devices
    /// given back, however it ends. Returns normally on the token; throws a failure after recording it in
    /// <see cref="State"/>. A Phase A that cannot solve is a failure recorded, not thrown.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A guider looping, calibrating or guiding from before refuses the loop the capture source starts. Best effort:
            // the first frame says what is wrong in better words than this would.
            if (_guider is not null)
            {
                try
                {
                    await _guider.StopCaptureAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PolarAlignment: guider StopCaptureAsync before run failed");
                }
            }

            await AlignAsync(cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Polar alignment ended on request");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Polar alignment failed");
            var reason = StatusText.FromException(ex);
            Update(state => state with { Phase = PolarAlignmentPhase.Failed, StatusMessage = $"Polar alignment error: {reason}", FailureReason = reason });
            throw;
        }
        finally
        {
            // ReverseAxisBack, Park or LeaveInPlace, as configured; then the claim. A failure keeps its words on the status
            // line throughout.
            Update(state => state with
            {
                Phase = PolarAlignmentPhase.RestoringMount,
                StatusMessage = state.FailureReason is null ? "Restoring mount\u2026" : state.StatusMessage,
            });
            try
            {
                await DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PolarAlignmentSession dispose failed");
            }
            Update(state => state with
            {
                Phase = PolarAlignmentPhase.Idle,
                StatusMessage = state.FailureReason is null ? "Polar alignment ended" : state.StatusMessage,
                Ended = true,
            });
        }
    }

    /// <summary>Restores the mount and gives the devices back, once, whoever asks first: the body, or a host whose run never ran.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _session.DisposeAsync();
        }
    }

    private async Task AlignAsync(CancellationToken cancellationToken)
    {
        Update(state => state with { Phase = PolarAlignmentPhase.ProbingExposure, StatusMessage = "Probing exposure..." });

        // Reported as they happen, on the run's own thread: a Progress<T> posts to the pool, where a late rung could land
        // after the rotation it preceded.
        var rungs = new Reporter<ProbeProgress>(p => Update(state => state with
        {
            StatusMessage = $"Probing {p.Exposure.TotalMilliseconds:F0}ms (rung {p.RungIndex + 1}/{p.RungCount})",
        }));
        // The rotation, the settle and frame 2 take another 15 to 30 s after the ramp; without these the status line sits on
        // the ramp's last rung and the routine looks hung.
        var phases = new Reporter<PolarPhaseUpdate>(update => Update(state => state with
        {
            Phase = update.Phase,
            StatusMessage = update.Detail ?? update.Phase switch
            {
                PolarAlignmentPhase.Rotating => "Rotating RA axis...",
                PolarAlignmentPhase.Frame2 => "Capturing frame 2...",
                _ => state.StatusMessage,
            },
        }));

        TwoFrameSolveResult phaseA;
        try
        {
            phaseA = await _session.SolveAsync(cancellationToken, rungs, phases);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Update(state => state with { Phase = PolarAlignmentPhase.Idle, StatusMessage = "Cancelled before Phase A completed" });
            throw;
        }

        if (!phaseA.Success)
        {
            var reason = phaseA.FailureReason ?? "Phase A failed";
            _logger.LogWarning("PolarAlignment Phase A failed: {Reason}", reason);
            Update(state => state with { Phase = PolarAlignmentPhase.Failed, StatusMessage = reason, PhaseA = phaseA, FailureReason = reason });
            return;
        }

        Update(state => state with
        {
            Phase = PolarAlignmentPhase.Refining,
            StatusMessage = $"Refining at {phaseA.LockedExposure.TotalMilliseconds:F0}ms",
            PhaseA = phaseA,
        });

        await foreach (var live in _session.RefineAsync(cancellationToken))
        {
            // The refine pose's own WCS, so an overlay drawn over the live frame projects through where the frame IS, not
            // through Phase A's last solve.
            if (live.Wcs is { } liveWcs)
            {
                _onFrameSolved?.Invoke(new PlateSolveResult(liveWcs, TimeSpan.Zero) { MatchedStars = live.StarsMatched });
            }
            Update(state =>
            {
                if (live.IsAligned && live.IsSettled && state.Phase != PolarAlignmentPhase.Aligned)
                {
                    return state with { Phase = PolarAlignmentPhase.Aligned, StatusMessage = "Aligned within target accuracy - click Done", LastSolve = live };
                }
                if (!live.IsAligned && state.Phase == PolarAlignmentPhase.Aligned)
                {
                    // A knob bumped out of alignment: back to refining.
                    return state with { Phase = PolarAlignmentPhase.Refining, StatusMessage = "Refining...", LastSolve = live };
                }
                return state with { LastSolve = live };
            });
        }
    }

    private void Update(Func<PolarRunState, PolarRunState> change)
    {
        // One writer, the run's own body, so a read-modify-write needs no exchange.
        var next = change(_state);
        _state = next;
        StateChanged?.Invoke(next);
    }

    private readonly record struct CaptureSource(ICaptureSource Source, IGuider? Guider, int OtaIndex, Uri[] Drives);

    /// <summary>
    /// The capture source: the connected guider (its camera's pixel size and the profile's guider focal length give the
    /// scale), or the OTA's main camera. What it DRIVES (the guider and its camera, or the OTA's camera) is claimed with
    /// the mount; the focuser and the filter wheel are only read, for the frames' cards, and reads are never claimed.
    /// </summary>
    private static bool TryBuildCaptureSource(in PolarAlignmentRequest request, ProfileData profile, IDeviceHub hub, IMountDriver mount,
        IExternal external, ICelestialObjectDB catalog, ITimeProvider timeProvider, ILogger logger,
        Action<int, Image>? onFrameCaptured, Action<PlateSolveResult>? onFrameSolved,
        out CaptureSource source, [NotNullWhen(false)] out string? refusal)
    {
        source = default;
        if (request.UseGuider)
        {
            if (!hub.TryGetConnectedDriver<IGuider>(profile.Guider, out var guider))
            {
                refusal = "Guider not connected, connect a guider or untoggle Use Guider";
                return false;
            }
            if (profile.GuiderCamera is not { } guideCamUri || !hub.TryGetConnectedDriver<ICameraDriver>(guideCamUri, out var guideCam))
            {
                refusal = "Guider camera not connected, cannot determine pixel scale";
                return false;
            }
            if (profile.GuiderFocalLength is not { } guiderFlMm || guiderFlMm <= 0)
            {
                refusal = "Guider focal length not set in profile, required for plate scale";
                return false;
            }

            // A guide scope's aperture is not recorded; f/4 (a typical 50/200 mini guider) is assumed, used only for ranking
            // heuristics and a hint.
            var guiderSource = new GuiderCaptureSource(
                guider,
                displayName: $"Guider; {guider.Name}",
                focalLengthMm: guiderFlMm,
                apertureMm: guiderFlMm / 4.0,
                pixelSizeMicrons: guideCam.PixelSizeX,
                external,
                logger,
                searchOriginAsync: async token =>
                {
                    var ra = await mount.GetRightAscensionAsync(token).ConfigureAwait(false);
                    var dec = await mount.GetDeclinationAsync(token).ConfigureAwait(false);
                    return (ra, dec);
                });
            source = new CaptureSource(guiderSource, guider, -1, [profile.Guider, guideCamUri]);
            refusal = null;
            return true;
        }

        var otaIndex = request.OtaIndex >= 0 && request.OtaIndex < profile.OTAs.Length ? request.OtaIndex : 0;
        var ota = profile.OTAs[otaIndex];
        if (!hub.TryGetConnectedDriver<ICameraDriver>(ota.Camera, out var camera))
        {
            refusal = $"OTA #{otaIndex + 1} camera not connected";
            return false;
        }

        // The focuser and the filter wheel only stamp the frames' cards (CameraExposureActions.StampDenormAsync, shared
        // with the session and the preview so the three never drift).
        IFocuserDriver? focuser = ota.Focuser is { } focuserUri && hub.TryGetConnectedDriver<IFocuserDriver>(focuserUri, out var focuserDriver)
            ? focuserDriver
            : null;
        IFilterWheelDriver? filterWheel = ota.FilterWheel is { } filterWheelUri && hub.TryGetConnectedDriver<IFilterWheelDriver>(filterWheelUri, out var filterWheelDriver)
            ? filterWheelDriver
            : null;

        var mainSource = new MainCameraCaptureSource(
            camera,
            displayName: $"OTA #{otaIndex + 1}; {ota.Name}",
            focalLengthMm: ota.FocalLength,
            apertureMm: ota.Aperture ?? Math.Max(1, ota.FocalLength / 5),
            otaName: ota.Name,
            focuser: focuser,
            filterWheel: filterWheel,
            mount: mount,
            targetName: "Polar Align",
            catalogDb: catalog,
            timeProvider: timeProvider,
            imageReadyPollInterval: external.ImageReadyPollInterval,
            logger: logger,
            // Each probe and refinement frame goes to the host as it is captured, or the user stares at a black panel for
            // the whole ramp; the capture source skips its release, so a host that keeps none releases it here.
            onFrameCaptured: onFrameCaptured is { } captured ? image => captured(otaIndex, image) : static image => image.Release(),
            onFrameSolved: onFrameSolved);
        source = new CaptureSource(mainSource, null, otaIndex, [ota.Camera]);
        refusal = null;
        return true;
    }

    /// <summary>
    /// The site: coordinates from the profile, refraction inputs from a connected weather device, else the standard
    /// atmosphere (the session's own two-tier <see cref="SiteConditions.Resolve"/>, collapsed to concrete numbers).
    /// </summary>
    private static PolarAlignmentSite BuildSite(ProfileData profile, IDeviceHub hub, double latitude, double longitude)
    {
        IWeatherDriver? weather = null;
        if (profile.Weather is { } weatherUri)
        {
            hub.TryGetConnectedDriver(weatherUri, out weather);
        }
        var conditions = SiteConditions.Resolve(weather);

        return new PolarAlignmentSite(
            LatitudeDeg: latitude,
            LongitudeDeg: longitude,
            ElevationM: profile.SiteElevation ?? 0,
            PressureHPa: conditions.PressureHPa,
            TemperatureC: conditions.TemperatureCelsius);
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports where it is called, in order.</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
