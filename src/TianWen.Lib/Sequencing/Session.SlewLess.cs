using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// The pointing routine for a mount that cannot goto (<c>docs/plans/slew-less-mount.md</c>, #1495): RA moved by the mount's
/// own motor, Dec by the operator, a plate solve between every step. One routine for arriving at a target and for centring
/// on it, since both are "solve, compare, correct" with a different correction.
/// </summary>
internal partial record Session
{
    /// <summary>The shortest timed RA move worth making: shorter, and the timing error is a large share of the move.</summary>
    internal static readonly TimeSpan MinTimedRaMove = TimeSpan.FromSeconds(5);

    /// <summary>How often the session solves while it waits for the operator to move Dec.</summary>
    internal static readonly TimeSpan DecWaitSolveInterval = TimeSpan.FromSeconds(10);

    /// <summary>The exposure each of the routine's solves takes, the centring exposure.</summary>
    private static readonly TimeSpan SlewLessSolveExposure = TimeSpan.FromSeconds(5);

    /// <summary>RA moves one arrival may make: a correction that does not converge in this many is a fault, not a step.</summary>
    internal const int MaxRaMovesPerArrival = 6;

    /// <summary>Solves in a row that may fail before an arrival gives up.</summary>
    private const int MaxFailedSolvesInARow = 3;

    /// <summary>How long centring outside an arrival (a recentre) waits for the operator to move Dec.</summary>
    internal static readonly TimeSpan SlewLessCenteringDecWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How close a slew-less arrival must come: <paramref name="requestedArcmin"/>, or a twentieth of the field's short side
    /// when that is wider. Nobody turns a Dec knob by hand to an arcminute, and a wide field frames its target as well at a
    /// twentieth of itself.
    /// </summary>
    internal double SlewLessThresholdArcmin(int telescopeIndex, double requestedArcmin)
    {
        var (widthDeg, heightDeg) = ComputeFovDegrees(Setup.Telescopes[telescopeIndex]);
        var shortSideArcmin = Math.Min(widthDeg, heightDeg) * 60.0;
        return double.IsFinite(shortSideArcmin) && shortSideArcmin > 0
            ? Math.Max(requestedArcmin, shortSideArcmin / 20.0)
            : requestedArcmin;
    }

    /// <summary>
    /// Points the mount at <paramref name="target"/> for <paramref name="observation"/>: a goto on a mount that has one, the
    /// slew-less routine on a mount that does not. The routine finishes before this returns and answers
    /// <see cref="SlewPostCondition.Slewing"/>, so the caller's wait for the slew returns at once and its centring confirms
    /// in one solve; a target it could not reach answers <see cref="SlewPostCondition.SlewNotPossible"/>, so the caller
    /// moves to a spare or the next observation as for a goto that cannot start. Unattended, the Dec wait lasts the
    /// observation's slot: from its start, or from now if it starts late, for its duration.
    /// </summary>
    private async ValueTask<SlewResult> BeginSlewOrPointAsync(ScheduledObservation observation, Target target, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        if (!MountPointsWithoutGoto)
        {
            return await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.BeginSlewToTargetAsync(target, Configuration.MinHeightAboveHorizon, ct),
                ResilientCallOptions.NonIdempotentAction, cancellationToken).ConfigureAwait(false);
        }

        if (await ResilientInvokeAsync(
                mount.Driver,
                ct => mount.Driver.RefuseTargetAsync(target, Configuration.MinHeightAboveHorizon, ct),
                ResilientCallOptions.IdempotentRead, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return new SlewResult(refused, double.NaN);
        }

        var now = await GetMountUtcNowAsync(cancellationToken);
        var giveUpAt = (observation.Start > now ? observation.Start : now) + observation.Duration;
        return await PointWithoutGotoAsync(target, 0, CenteringThresholdArcmin, giveUpAt, cancellationToken) is SlewLessArrival.Arrived
            ? new SlewResult(SlewPostCondition.Slewing,
                await ResilientInvokeAsync(mount.Driver, mount.Driver.GetHourAngleAsync, ResilientCallOptions.IdempotentRead, cancellationToken))
            : new SlewResult(SlewPostCondition.SlewNotPossible, double.NaN);
    }

    /// <summary>How close the observation loop centres a target, the threshold <c>CenterOnTargetAsync</c> is given there.</summary>
    private const double CenteringThresholdArcmin = 1.0;

    /// <summary>
    /// Whether a positive primary-axis rate moves the pointing west, read off the first move's solve: null until then, when
    /// the driver's word (ASCOM's, and the SkyGuider Pro's: positive is west) is assumed. Measured, never trusted, since an
    /// unattended arrival cannot afford a mount that runs the other way.
    /// </summary>
    private bool? _positiveRaRateMovesWest;

    /// <summary>
    /// A mount the session points without a goto: it cannot slew, but it can be synced and its RA axis moved. Today that
    /// is the iOptron SkyGuider Pro.
    /// </summary>
    internal bool MountPointsWithoutGoto
        => Setup.Mount.Driver is { CanSlewAsync: false, CanSync: true } driver && driver.CanMoveAxis(TelescopeAxis.Primary);

    /// <summary>How a slew-less arrival ended.</summary>
    internal enum SlewLessArrival
    {
        /// <summary>Within the threshold on both axes, solved and synced.</summary>
        Arrived,

        /// <summary>The routine could not get there: its solves failed, or its RA moves did not converge.</summary>
        NotReached,

        /// <summary>The operator skipped the target, or its time ran out while Dec was awaited.</summary>
        Skipped,
    }

    /// <summary>
    /// Points the rig at <paramref name="target"/> on a mount that cannot goto: solve and sync; while RA is off, move it with
    /// the motor (the owner: "we still need to slew RA first"); while Dec is off, wait for the operator, solving as they turn
    /// it, until <paramref name="giveUpAt"/>.
    /// </summary>
    internal async ValueTask<SlewLessArrival> PointWithoutGotoAsync(
        Target target, int telescopeIndex, double thresholdArcmin, DateTimeOffset giveUpAt, CancellationToken cancellationToken)
    {
        thresholdArcmin = SlewLessThresholdArcmin(telescopeIndex, thresholdArcmin);
        var raMoves = 0;
        var failedSolves = 0;
        (double RaBefore, double ExpectedChangeHours, bool PositiveRate)? lastMove = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _currentActivity = $"Plate solving to find {target.Name}…";
                var (solved, solvedRa, solvedDec) = await PlateSolveAndSyncCoreAsync(
                    telescopeIndex, SlewLessSolveExposure, PlateSolveContext.Centering, cancellationToken);
                if (!solved)
                {
                    if (++failedSolves >= MaxFailedSolvesInARow)
                    {
                        _logger.LogWarning("Slew-less pointing: {Count} plate solves in a row failed, giving up on {Target}.",
                            failedSolves, target.Name);
                        return SlewLessArrival.NotReached;
                    }

                    continue;
                }

                failedSolves = 0;

                if (lastMove is { } move)
                {
                    LearnRaRateSign(move.RaBefore, solvedRa, move.ExpectedChangeHours, move.PositiveRate);
                    lastMove = null;
                }

                var raChangeHours = CoordinateUtils.ConditionHA(target.RA - solvedRa);
                var raOffArcmin = Math.Abs(raChangeHours) * 15.0 * 60.0 * Math.Cos(double.DegreesToRadians(solvedDec));
                var decOffArcmin = Math.Abs(target.Dec - solvedDec) * 60.0;
                _logger.LogInformation("Slew-less pointing: {Target} is {RaOff:F1}' off in RA and {DecOff:F1}' in Dec.",
                    target.Name, raOffArcmin, decOffArcmin);

                if (raOffArcmin <= thresholdArcmin && decOffArcmin <= thresholdArcmin)
                {
                    return SlewLessArrival.Arrived;
                }

                if (raOffArcmin > thresholdArcmin)
                {
                    if (++raMoves > MaxRaMovesPerArrival)
                    {
                        _logger.LogWarning("Slew-less pointing: {Count} RA moves did not reach {Target}, giving up.",
                            MaxRaMovesPerArrival, target.Name);
                        return SlewLessArrival.NotReached;
                    }

                    var positiveRate = await MoveRaByAsync(raChangeHours, cancellationToken);
                    lastMove = (solvedRa, raChangeHours, positiveRate);

                    // The next solve searches around what the mount believes, and a timed move on a mount with no encoder
                    // leaves its belief where it was: sync it to where the move should have gone. The solve then syncs
                    // it to the truth.
                    await BelieveAsync(CoordinateUtils.ConditionRA(solvedRa + raChangeHours), solvedDec, cancellationToken);
                    continue;
                }

                if (await WaitForDecByHandAsync(target, telescopeIndex, thresholdArcmin, giveUpAt, cancellationToken) is not DecWait.Reached)
                {
                    return SlewLessArrival.Skipped;
                }
            }

            return SlewLessArrival.NotReached;
        }
        finally
        {
            _currentActivity = null;
        }
    }

    /// <summary>
    /// Moves the RA the camera sees by <paramref name="raChangeHours"/> (positive is east), timed at one of the mount's own
    /// rates; the caller solves afterwards, since a timed move on a mount with no encoder is never trusted. Returns whether
    /// the rate commanded was positive, for <see cref="LearnRaRateSign"/>.
    /// </summary>
    private async ValueTask<bool> MoveRaByAsync(double raChangeHours, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount.Driver;
        var degrees = Math.Abs(raChangeHours) * 15.0;
        var rate = ChooseRaRate(mount.AxisRates(TelescopeAxis.Primary), degrees);
        var duration = TimeSpan.FromSeconds(degrees / rate);

        // A move west lowers the RA the camera sees.
        var west = raChangeHours < 0;
        var positiveRate = west == (_positiveRaRateMovesWest ?? true);
        _currentActivity = $"Moving RA {(west ? "west" : "east")} by {degrees:F1}° ({duration.TotalSeconds:F0} s)…";
        _logger.LogInformation("Slew-less pointing: moving RA {Direction} by {Degrees:F2} deg at {Rate:F4} deg/s for {Seconds:F1} s.",
            west ? "west" : "east", degrees, rate, duration.TotalSeconds);

        try
        {
            await ResilientInvokeAsync(mount,
                ct => mount.MoveAxisAsync(TelescopeAxis.Primary, positiveRate ? rate : -rate, ct),
                ResilientCallOptions.AbsoluteMove, cancellationToken);
            await _timeProvider.SleepAsync(duration, cancellationToken);
        }
        finally
        {
            // Stopped whatever ended the move, a cancel included: a mount left moving runs into the tripod.
            await ResilientInvokeAsync(mount,
                ct => mount.MoveAxisAsync(TelescopeAxis.Primary, 0.0, ct),
                ResilientCallOptions.AbsoluteMove, CancellationToken.None);
        }

        return positiveRate;
    }

    /// <summary>
    /// The fastest of the axis's rates (degrees per second) whose move over <paramref name="degrees"/> lasts at least
    /// <see cref="MinTimedRaMove"/>; the slowest when none does.
    /// </summary>
    internal static double ChooseRaRate(IReadOnlyList<AxisRate> rates, double degrees)
    {
        var fastestLongEnough = double.NaN;
        var slowest = double.PositiveInfinity;
        foreach (var axisRate in rates)
        {
            var rate = axisRate.Maximum;
            if (rate <= 0)
            {
                continue;
            }

            slowest = Math.Min(slowest, rate);
            if (degrees / rate >= MinTimedRaMove.TotalSeconds && (double.IsNaN(fastestLongEnough) || rate > fastestLongEnough))
            {
                fastestLongEnough = rate;
            }
        }

        return !double.IsNaN(fastestLongEnough)
            ? fastestLongEnough
            : double.IsFinite(slowest) ? slowest : throw new InvalidOperationException("The mount's RA axis states no rate it can move at");
    }

    /// <summary>
    /// Reads the sign of the primary axis off a move's solve: the RA moved the wrong way, by more than a quarter of what was
    /// asked, means a positive rate moves the pointing the other way from what was assumed.
    /// </summary>
    private void LearnRaRateSign(double raBefore, double raAfter, double expectedChangeHours, bool positiveRate)
    {
        var observed = CoordinateUtils.ConditionHA(raAfter - raBefore);
        if (Math.Abs(observed) < 0.25 * Math.Abs(expectedChangeHours))
        {
            return;
        }

        var movedWest = observed < 0;
        var positiveMovesWest = positiveRate == movedWest;
        if (_positiveRaRateMovesWest != positiveMovesWest)
        {
            _logger.LogInformation("Slew-less pointing: a positive RA rate moves the pointing {Direction}, measured.",
                positiveMovesWest ? "west" : "east");
            _positiveRaRateMovesWest = positiveMovesWest;
        }
    }

    /// <summary>
    /// Sets what the mount believes it points at (a sync), so the next plate solve searches there: the routine's estimate
    /// after a move, or the target's Dec while the operator turns toward it. The solve that follows syncs the truth.
    /// </summary>
    private ValueTask BelieveAsync(double raJ2000, double decJ2000, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount.Driver;
        return ResilientInvokeAsync(mount, ct => mount.SyncRaDecJ2000Async(raJ2000, decJ2000, ct),
            ResilientCallOptions.AbsoluteMove, cancellationToken);
    }

    private enum DecWait { Reached, Skipped, TimedOut }

    /// <summary>
    /// The owner's "state of waiting for DEC recentering": one prompt, then a solve every <see cref="DecWaitSolveInterval"/>
    /// until Dec is within the threshold, the operator skips the target, or <paramref name="giveUpAt"/> passes. The prompt's
    /// text is fixed (a prompt cannot change while open); the live distance goes into the activity line.
    /// </summary>
    private async ValueTask<DecWait> WaitForDecByHandAsync(
        Target target, int telescopeIndex, double thresholdArcmin, DateTimeOffset giveUpAt, CancellationToken cancellationToken)
    {
        var decText = CoordinateUtils.DegreesToDMS(target.Dec, degreeSign: '°', precision: SexagesimalPrecision.Minute, arcMinuteSign: '\'');

        // Answering asserts nothing, since the solve decides, so nobody to ask means "keep solving": the default is Continue.
        var prompt = RaisePrompt(
            "Move the Dec axis",
            $"Turn the Dec axis to {decText} for {target.Name}. The session carries on by itself once a plate solve finds the target.",
            continueLabel: "Solve now", cancelLabel: "Skip this target",
            requiresPhysicalPresence: true, defaultIfUnanswerable: true);
        var lastSolvedRa = target.RA;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (prompt is { Task.IsCompletedSuccessfully: true, Task.Result: false })
                {
                    _logger.LogInformation("Slew-less pointing: the operator skipped {Target}.", target.Name);
                    return DecWait.Skipped;
                }

                if (_timeProvider.GetUtcNow() >= giveUpAt)
                {
                    _logger.LogWarning("Slew-less pointing: Dec was not moved to {Target} in its time, moving on.", target.Name);
                    return DecWait.TimedOut;
                }

                var (solved, solvedRa, solvedDec) = await PlateSolveAndSyncCoreAsync(
                    telescopeIndex, SlewLessSolveExposure, PlateSolveContext.Centering, cancellationToken);
                if (solved)
                {
                    lastSolvedRa = solvedRa;
                    var decChange = target.Dec - solvedDec;
                    if (Math.Abs(decChange) * 60.0 <= thresholdArcmin)
                    {
                        return DecWait.Reached;
                    }

                    _currentActivity = $"Waiting for Dec to be moved {Math.Abs(decChange):F1}° {(decChange > 0 ? "north" : "south")} to {target.Name}";
                }
                else
                {
                    // The solve searches around the last solved Dec, and the operator may have turned past its reach: look
                    // where they are heading next time.
                    await BelieveAsync(lastSolvedRa, target.Dec, cancellationToken);
                }

                await _timeProvider.SleepAsync(DecWaitSolveInterval, cancellationToken);
            }

            return DecWait.TimedOut;
        }
        finally
        {
            // Withdrawn however the wait ended, so no client keeps offering a question nobody waits on.
            prompt?.TrySetCanceled(CancellationToken.None);
        }
    }
}
