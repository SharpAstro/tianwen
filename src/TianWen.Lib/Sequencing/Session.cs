using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal partial record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IPlateSolver PlateSolver,
    IExternal External,
    ScheduledObservationTree Observations
) : ISession
{
    const int UNINITIALIZED_OBSERVATION_INDEX = -1;

    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private readonly ConcurrentDictionary<int, FrameMetrics[]> _baselineByObservation = [];
    private readonly ConcurrentDictionary<int, List<FrameMetrics>[]> _baselineSamples = [];
    private int _activeObservation = UNINITIALIZED_OBSERVATION_INDEX;
    private int _spareIndex;
    private int _totalFramesWritten;
    private long _totalExposureTimeTicks;

    internal int TotalFramesWritten => _totalFramesWritten;
    internal TimeSpan TotalExposureTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalExposureTimeTicks));
    internal int CurrentObservationIndex => _activeObservation;

    /// <summary>
    /// Per-observation, per-telescope baseline metrics for focus drift and environmental anomaly detection.
    /// Keyed by observation index because metrics vary with sky area, altitude, and guiding quality.
    /// </summary>
    internal IReadOnlyDictionary<int, FrameMetrics[]> BaselineByObservation => _baselineByObservation;

    public ScheduledObservation? ActiveObservation => _activeObservation is int active and >= 0 && active < Observations.Count ? Observations[active] : null;

    private int AdvanceObservation()
    {
        _spareIndex = 0;
        return Interlocked.Increment(ref _activeObservation);
    }

    /// <summary>
    /// Advances the observation index for test purposes, allowing tests to set up
    /// the session state before calling ObservationLoopAsync or ImagingLoopAsync directly.
    /// </summary>
    internal int AdvanceObservationForTest() => AdvanceObservation();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                if (!await InitialisationAsync(cancellationToken))
                {
                    External.AppLogger.LogError("Initialization failed, aborting session.");
                    return;
                }
            }
            else if (ActiveObservation is null)
            {
                External.AppLogger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                return;
            }

            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!await InitialRoughFocusAsync(cancellationToken))
            {
                External.AppLogger.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }

            if (!await AutoFocusAllTelescopesAsync(cancellationToken))
            {
                External.AppLogger.LogWarning("Auto-focus did not converge for all telescopes, proceeding with rough focus.");
            }

            await CalibrateGuiderAsync(cancellationToken).ConfigureAwait(false);

            await ObservationLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            await Finalise(cancellationToken).ConfigureAwait(false);
        }
    }


    public async ValueTask DisposeAsync()
    {
        await Setup.DisposeAsync();

        GC.SuppressFinalize(this);
    }

}
