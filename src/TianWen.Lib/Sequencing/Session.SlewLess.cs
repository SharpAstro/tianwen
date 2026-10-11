using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// How far the RA axis truly moves per second at each of its rates, as a multiple of the rate it states, read off each
    /// move's solve: a timed move on a mount with no encoder is never trusted, and a half turn 3 % long misses by five
    /// degrees, farther than a solve hinted at the target can look. Written and read only by the routine, which runs one
    /// step at a time on the session's own sequence.
    /// </summary>
    private readonly Dictionary<double, double> _raRateScale = [];

    /// <summary>A move the routine made, kept for what the next solve teaches about the motor.</summary>
    private readonly record struct RaMove(double RaBefore, double ExpectedChangeHours, bool PositiveRate, double Rate, TimeSpan Duration);

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
        RaMove? lastMove = null;

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
                    LearnFromRaMove(move, solvedRa);
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

                    lastMove = await MoveRaByAsync(solvedRa, raChangeHours, cancellationToken);

                    // The next solve searches around what the mount believes, and a timed move on a mount with no encoder
                    // leaves its belief where it was: sync it to where the move should have gone. The solve then syncs
                    // it to the truth.
                    await BelieveAsync(CoordinateUtils.ConditionRA(solvedRa + raChangeHours), solvedDec, cancellationToken);
                    continue;
                }

                if (await WaitForDecByHandAsync(target, telescopeIndex, thresholdArcmin, giveUpAt, overThePole: false, landingRa: solvedRa, cancellationToken) is not DecWait.Reached)
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
    /// Moves the RA the camera sees by <paramref name="raChangeHours"/> (positive is east) from <paramref name="raBefore"/>,
    /// timed at one of the mount's own rates by the speed the routine has MEASURED at that rate; the caller solves
    /// afterwards, since a timed move on a mount with no encoder is never trusted, and hands the solve to
    /// <see cref="LearnFromRaMove"/>.
    /// </summary>
    private async ValueTask<RaMove> MoveRaByAsync(double raBefore, double raChangeHours, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount.Driver;
        var degrees = Math.Abs(raChangeHours) * 15.0;
        var rate = ChooseRaRate(mount.AxisRates(TelescopeAxis.Primary), degrees);
        var scale = _raRateScale.TryGetValue(rate, out var measured) ? measured : 1.0;
        var duration = TimeSpan.FromSeconds(degrees / (rate * scale));

        // A move west lowers the RA the camera sees.
        var west = raChangeHours < 0;
        var positiveRate = west == (_positiveRaRateMovesWest ?? true);
        _currentActivity = $"Moving RA {(west ? "west" : "east")} by {degrees:F1}\u00B0 ({duration.TotalSeconds:F0} s)\u2026";
        _logger.LogInformation("Slew-less pointing: moving RA {Direction} by {Degrees:F2} deg at {Rate:F4} deg/s (measured x{Scale:F4}) for {Seconds:F1} s.",
            west ? "west" : "east", degrees, rate, scale, duration.TotalSeconds);

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

        return new RaMove(raBefore, raChangeHours, positiveRate, rate, duration);
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
    /// What a move's solve teaches about the motor. Its SIGN: the RA moved the wrong way, by more than a quarter of what was
    /// asked, means a positive rate moves the pointing the other way from what was assumed. Its SPEED at that rate: the
    /// degrees the solve saw over the degrees the stated rate gives in the move's time, believed only between half and
    /// double (beyond that it is a bad solve, not a motor) and only off a move long enough for the solve's error not to
    /// matter.
    /// </summary>
    private void LearnFromRaMove(RaMove move, double raAfter)
    {
        var observed = CoordinateUtils.ConditionHA(raAfter - move.RaBefore);
        if (Math.Abs(observed) < 0.25 * Math.Abs(move.ExpectedChangeHours))
        {
            return;
        }

        var movedWest = observed < 0;
        var positiveMovesWest = move.PositiveRate == movedWest;
        if (_positiveRaRateMovesWest != positiveMovesWest)
        {
            _logger.LogInformation("Slew-less pointing: a positive RA rate moves the pointing {Direction}, measured.",
                positiveMovesWest ? "west" : "east");
            _positiveRaRateMovesWest = positiveMovesWest;
        }

        var nominalDegrees = move.Rate * move.Duration.TotalSeconds;
        var observedDegrees = Math.Abs(observed) * 15.0;
        var scale = observedDegrees / nominalDegrees;
        if (nominalDegrees >= MinMeasuringMoveDegrees && scale is >= 0.5 and <= 2.0)
        {
            _logger.LogInformation("Slew-less pointing: the RA axis moves x{Scale:F4} its stated rate at {Rate:F4} deg/s, measured.",
                scale, move.Rate);
            _raRateScale[move.Rate] = scale;
        }
    }

    /// <summary>The shortest move whose solve measures the motor's speed: a solve's error over it is under a thousandth.</summary>
    private const double MinMeasuringMoveDegrees = 0.5;

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
    /// until a solve finds the target's Dec within the threshold and its RA within a quarter turn, the operator skips the
    /// target, or <paramref name="giveUpAt"/> passes. With <paramref name="overThePole"/> it is a flip's second half: the
    /// motor has turned RA a half turn, so the camera looks 12 h away until the operator swings Dec over the pole, and the
    /// quarter-turn test is what tells the swing happened. <paramref name="landingRa"/> is the RA the turn lands at, where a
    /// failed solve looks next: the RA already reached for a plain Dec turn, and for the swing the RA from before the half
    /// turn, since a half turn and a swing over the pole cancel in RA. The prompt's text is fixed (a prompt cannot change
    /// while open); the live distance goes into the activity line.
    /// </summary>
    private async ValueTask<DecWait> WaitForDecByHandAsync(
        Target target, int telescopeIndex, double thresholdArcmin, DateTimeOffset giveUpAt, bool overThePole, double landingRa,
        CancellationToken cancellationToken)
    {
        var decText = CoordinateUtils.DegreesToDMS(target.Dec, degreeSign: '°', precision: SexagesimalPrecision.Minute, arcMinuteSign: '\'');

        // Answering asserts nothing, since the solve decides, so nobody to ask means "keep solving": the default is Continue.
        var prompt = overThePole
            ? RaisePrompt(
                "Swing the Dec axis over the pole",
                $"Meridian flip: the RA axis has turned a half turn. Swing the Dec axis over the pole to {decText} to bring {target.Name} back. "
                    + "The session carries on by itself once a plate solve finds it.",
                continueLabel: "Solve now", cancelLabel: "Skip this target",
                requiresPhysicalPresence: true, defaultIfUnanswerable: true)
            : RaisePrompt(
                "Move the Dec axis",
                $"Turn the Dec axis to {decText} for {target.Name}. The session carries on by itself once a plate solve finds the target.",
                continueLabel: "Solve now", cancelLabel: "Skip this target",
                requiresPhysicalPresence: true, defaultIfUnanswerable: true);

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
                    var decChange = target.Dec - solvedDec;
                    var stillAcrossThePole = Math.Abs(CoordinateUtils.ConditionHA(target.RA - solvedRa)) >= 6.0;
                    if (!stillAcrossThePole && Math.Abs(decChange) * 60.0 <= thresholdArcmin)
                    {
                        return DecWait.Reached;
                    }

                    _currentActivity = stillAcrossThePole
                        ? $"Waiting for the Dec axis to be swung over the pole to {target.Name}"
                        : $"Waiting for Dec to be moved {Math.Abs(decChange):F1}° {(decChange > 0 ? "north" : "south")} to {target.Name}";
                }
                else
                {
                    // The solve searches around what the mount last believed, and the operator may have turned past its
                    // reach: look where the turn lands next time, the target's Dec at the landing RA.
                    await BelieveAsync(landingRa, target.Dec, cancellationToken);
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

    /// <summary>
    /// The meridian flip on a mount that cannot goto (the owner, 2026-10-11): "it should still move RA automatically otherwise
    /// the camera might crash into the tripod legs. We then keep in state of waiting for DEC recentering", the Dec half
    /// "as you have flip the DEC axis which this mount can't do". The motor turns RA a half turn east at once, the operator
    /// swings Dec over the pole, and the session waits, solving, until a solve finds the target on the far side.
    /// <see cref="CompleteMeridianFlipAsync"/> then recentres (this routine's correction) and reads the field's rotation to
    /// confirm the flip, as on any mount whose pointing state is computed.
    /// </summary>
    private async ValueTask<MeridianFlipResult> FlipWithoutGotoAsync(
        ScheduledObservation observation, WCS? preFlipSolution, CancellationToken cancellationToken)
    {
        var target = observation.Target;
        var mount = Setup.Mount;

        // A half turn must go EAST, away from the tripod legs, and land within a solve's reach, so the motor's direction
        // and its top speed are measured before it is trusted with one, if no move has measured them yet.
        await MeasureRaMotorAsync(cancellationToken);

        var before = await ResilientInvokeAsync(
            mount.Driver, ct => mount.Driver.GetRaDecJ2000Async(ct), ResilientCallOptions.IdempotentRead, cancellationToken);
        _logger.LogInformation("Meridian flip on a mount that cannot goto: turning RA a half turn east for {Target}.", target);
        _currentActivity = "Meridian flip: turning RA a half turn…";
        await MoveRaByAsync(before is { } from ? from.RaJ2000 : double.NaN, 12.0, cancellationToken);
        if (before is { } believed)
        {
            await BelieveAsync(CoordinateUtils.ConditionRA(believed.RaJ2000 + 12.0), believed.DecJ2000, cancellationToken);
        }

        var now = await GetMountUtcNowAsync(cancellationToken);
        var slotEnd = observation.Start + observation.Duration;
        var giveUpAt = slotEnd > now ? slotEnd : now + SlewLessCenteringDecWait;
        var threshold = SlewLessThresholdArcmin(0, CenteringThresholdArcmin);
        var landingRa = before is { } beforeHalfTurn ? beforeHalfTurn.RaJ2000 : target.RA;
        if (await WaitForDecByHandAsync(target, 0, threshold, giveUpAt, overThePole: true, landingRa, cancellationToken) is not DecWait.Reached)
        {
            _logger.LogWarning("Meridian flip: Dec was not swung over the pole to {Target}, so the flip did not finish.", target.Name);
            _currentActivity = null;
            return MeridianFlipResult.Failed;
        }

        _currentActivity = null;
        return await CompleteMeridianFlipAsync(observation, preFlipSolution, cancellationToken);
    }

    /// <summary>
    /// Measures the motor before a half turn is trusted to it: which way a positive rate moves the pointing, and how fast
    /// the TOP rate truly is, with one move east at that rate and a solve either side, unless earlier moves already
    /// measured both. Unmeasured, it keeps the driver's word (positive is west, the stated rate) and says so.
    /// </summary>
    private async ValueTask MeasureRaMotorAsync(CancellationToken cancellationToken)
    {
        var topRate = Setup.Mount.Driver.AxisRates(TelescopeAxis.Primary).Max(r => r.Maximum);
        if (_positiveRaRateMovesWest is not null && _raRateScale.ContainsKey(topRate))
        {
            return;
        }

        // Long enough to be timed at the top rate, which is then the rate it is made at.
        var probeHours = topRate * (MinTimedRaMove.TotalSeconds + 1.0) / 15.0;
        var (beforeSolved, raBefore, decBefore) = await PlateSolveAndSyncCoreAsync(0, SlewLessSolveExposure, PlateSolveContext.Centering, cancellationToken);
        var move = await MoveRaByAsync(raBefore, probeHours, cancellationToken);
        if (beforeSolved)
        {
            await BelieveAsync(CoordinateUtils.ConditionRA(raBefore + probeHours), decBefore, cancellationToken);
        }

        var (afterSolved, raAfter, _) = await PlateSolveAndSyncCoreAsync(0, SlewLessSolveExposure, PlateSolveContext.Centering, cancellationToken);
        if (beforeSolved && afterSolved)
        {
            LearnFromRaMove(move, raAfter);
        }
        else
        {
            _logger.LogWarning("Slew-less pointing: could not measure the RA motor (a solve failed); keeping the driver's word.");
        }
    }
}
