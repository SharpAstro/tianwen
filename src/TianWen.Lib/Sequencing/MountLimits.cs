using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>Which safety limit a <see cref="MountLimitVerdict"/> is about.</summary>
public enum MountLimitKind
{
    /// <summary>No limit is in play.</summary>
    None,

    /// <summary>
    /// The RA axis has carried the tube too far toward the pier: past the meridian in the pre-flip
    /// pointing state, or (the mirror case) east of it after a flip. A GEM's OTA eventually meets
    /// the pier; this is the mechanical bound on how long an <c>AcrossMeridian</c> observation may run.
    /// </summary>
    Meridian,

    /// <summary>
    /// The pointing has descended too close to the horizon. Trees, roof lines, the tripod itself,
    /// and at the very bottom the ground.
    /// </summary>
    Horizon,

    /// <summary>
    /// The MOUNT stopped tracking on its own: a driver-enforced limit (GSServer, OnStep, an ASCOM
    /// driver with its own limits) or a stall. Not something <see cref="MountLimits.Evaluate"/> decides
    /// -- the session observes it -- and not a device fault: the rig did what it was configured to do,
    /// so the run ends as a limit stop instead of a reconnect storm or a night spent fighting the
    /// driver's stop with <c>EnsureTrackingAsync</c>.
    /// </summary>
    DriverEnforced,
}

/// <summary>
/// What a meridian verdict was computed FROM. The plan's two tiers, labelled so the user knows which
/// one they have: a rig that believes it has a mechanical limit and actually has a sky-coordinate one
/// sets the threshold too tight and gets spurious stops, or too loose and gets none.
/// </summary>
public enum MountLimitBasis
{
    /// <summary>
    /// The hour angle plus the reported pointing state: a sky-coordinate estimate of where the axis is.
    /// Right on a synced mount with a correct clock and site; wrong by exactly whatever they are wrong by.
    /// </summary>
    HourAngle,

    /// <summary>
    /// The RA axis angle read from the mount (<see cref="IMountDriver.GetAxisAngleAsync"/>): the
    /// mechanical fact, independent of clock, site and the pointing state a sync believed. Relative to
    /// the encoders' home, which is the one thing it cannot check.
    /// </summary>
    AxisAngle,
}

/// <summary>
/// What to do when a limit is reached. Deliberately ORDERED by severity, so
/// <c>&gt;=</c> comparisons are meaningful and a caller can escalate.
/// </summary>
/// <remarks>
/// GSServer models this as two independent booleans (<c>LimitTracking</c>, <c>LimitPark</c>), which
/// admits a fourth combination -- both false -- that alarms and does nothing. That state is a
/// misconfiguration wearing the costume of a setting: the user has enabled limits and then asked for
/// no protection. Parking physically implies tracking has stopped, so the three real behaviours form
/// a chain and an ordered enum says so.
/// </remarks>
public enum MountLimitResponse
{
    /// <summary>Raise the alarm and tell the user. Do not touch the mount.</summary>
    Warn,

    /// <summary>Stop tracking where it stands. The rig stays pointed but stops driving into the limit.</summary>
    StopTracking,

    /// <summary>Stop tracking and park. Falls back to <see cref="StopTracking"/> if parking is unavailable.</summary>
    Park,
}

/// <summary>
/// The outcome of <see cref="MountLimits.Evaluate"/>: which limit (if any) is breached, how far past
/// it the mount is, and what the configuration says to do about it.
/// </summary>
/// <param name="Kind">Which limit, or <see cref="MountLimitKind.None"/>.</param>
/// <param name="Response">What to do. Meaningless when <paramref name="Kind"/> is None.</param>
/// <param name="Basis">
/// For a <see cref="MountLimitKind.Meridian"/> verdict, whether the offset was measured on the RA axis
/// or estimated from the hour angle; <see cref="MountLimitBasis.HourAngle"/> otherwise. Rendered by
/// <see cref="Describe"/>, so the log and the UI always say which tier the user has.
/// </param>
/// <param name="ExceededBy">
/// How far past the ACTION threshold, or -- while only the warning threshold has been crossed -- a
/// negative number giving how far is still left before action. So the sign is the answer to "has it
/// acted yet", and the magnitude is always "how far from the action point".
/// <para>
/// <b>The UNIT depends on <paramref name="Kind"/>:</b> minutes of hour angle for
/// <see cref="MountLimitKind.Meridian"/>, degrees of altitude for
/// <see cref="MountLimitKind.Horizon"/>. Mixed units in one field look like a smell and are the
/// lesser evil: the alternative is two fields of which one is always meaningless, and every
/// consumer already switches on <paramref name="Kind"/> to say anything useful anyway.
/// <see cref="MountLimitVerdict.Describe"/> renders the right unit; do not format this raw.
/// </para>
/// </param>
/// <param name="Latched">
/// True once the limit has ACTED and the mount is still inside it: <see cref="MountLimitVerdict.Response"/>
/// is downgraded to <see cref="MountLimitResponse.Warn"/> so nothing commands the recovery twice, and this
/// flag is what lets <see cref="MountLimitVerdict.Describe"/> say so -- read live, the downgraded verdict
/// announced "Will warn only" about a mount the limit had just stopped.
/// </param>
public readonly record struct MountLimitVerdict(
    MountLimitKind Kind,
    MountLimitResponse Response,
    double ExceededBy,
    MountLimitBasis Basis = MountLimitBasis.HourAngle,
    bool Latched = false)
{
    /// <summary>Nothing is wrong.</summary>
    public static MountLimitVerdict Clear { get; } =
        new MountLimitVerdict(MountLimitKind.None, MountLimitResponse.Warn, 0.0);

    /// <summary>
    /// The mount stopped tracking without being asked to (<see cref="MountLimitKind.DriverEnforced"/>).
    /// <see cref="MountLimitResponse.Warn"/> because there is nothing left to act on: the driver already did.
    /// </summary>
    public static MountLimitVerdict DriverEnforcedStop { get; } =
        new MountLimitVerdict(MountLimitKind.DriverEnforced, MountLimitResponse.Warn, 0.0);

    /// <summary>True when any limit is in play, warning included.</summary>
    public bool IsBreached => Kind is not MountLimitKind.None;

    /// <summary>
    /// True when the mount is between the warning and action thresholds: the user should be told,
    /// but nothing is taken away from them yet.
    /// </summary>
    public bool IsWarningOnly => IsBreached && ExceededBy < 0.0;

    /// <summary>
    /// One user-facing sentence, in the same spirit as <c>DeviceOwnershipGate.Describe()</c>: say
    /// what happened, in what units, and what is about to be done about it.
    /// </summary>
    public string Describe() => Kind switch
    {
        MountLimitKind.None => "Within all configured mount limits.",
        MountLimitKind.DriverEnforced =>
            "The mount stopped tracking on its own: a driver-enforced limit (or a stall), not a fault. Ending the observation rather than fighting it.",
        MountLimitKind.Meridian when IsWarningOnly =>
            $"Approaching the meridian limit: {-ExceededBy:F0} min of hour angle before the mount will {ResponsePhrase()} ({BasisPhrase()}).",
        MountLimitKind.Meridian =>
            $"Meridian limit reached: the tube is {ExceededBy:F0} min of hour angle past the limit ({BasisPhrase()}). {ActionPhrase()}",
        MountLimitKind.Horizon when IsWarningOnly =>
            $"Approaching the horizon limit: {-ExceededBy:F1} deg of altitude before the mount will {ResponsePhrase()}.",
        MountLimitKind.Horizon =>
            $"Horizon limit reached: the pointing is {ExceededBy:F1} deg below the limit. {ActionPhrase()}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "unknown limit kind"),
    };

    private string BasisPhrase() => Basis switch
    {
        MountLimitBasis.AxisAngle => "measured on the RA axis",
        MountLimitBasis.HourAngle => "estimated from the hour angle",
        _ => throw new ArgumentOutOfRangeException(nameof(Basis), Basis, "unknown limit basis"),
    };

    /// <summary>What is about to happen -- or, once latched, what already did and why nothing more will.</summary>
    private string ActionPhrase() => Latched
        ? "The limit has already acted (tracking is stopped); it acts again only once the mount is clear."
        : $"Will {ResponsePhrase()}.";

    private string ResponsePhrase() => Response switch
    {
        MountLimitResponse.Warn => "warn only",
        MountLimitResponse.StopTracking => "stop tracking",
        MountLimitResponse.Park => "stop tracking and park",
        _ => throw new ArgumentOutOfRangeException(nameof(Response), Response, "unknown limit response"),
    };
}

/// <summary>
/// Where a mount may not go, as a property of the pier, the tube and the horizon rather than of any
/// one night. Disabled by default: a limit that fires when the user did not ask for one ends a
/// session, and a rig with clear sky in every direction genuinely has none.
/// </summary>
/// <param name="Enabled">Master switch, mirroring GSServer's <c>LimitsOn</c>.</param>
/// <param name="MeridianWarnMinutes">
/// Minutes of tracking PAST the meridian at which to start warning.
/// <para>
/// <b>Minutes, matching <see cref="SessionConfiguration.MeridianFlipLatestMinutesAfter"/>.</b> These
/// two settings bound the same axis and the user reads them together, so they must be directly
/// comparable; this was degrees once and the pair silently invited "5 and 10 mean the same thing in
/// both", which they did not (5 deg is 20 min). 1 min of hour angle = 0.25 deg.
/// </para>
/// </param>
/// <param name="MeridianActionExtraMinutes">
/// FURTHER minutes past <paramref name="MeridianWarnMinutes"/> before
/// <paramref name="MeridianResponse"/> is carried out. Expressed as an extra rather than as a second
/// absolute threshold so that <c>action &gt;= warn</c> holds by construction and cannot be inverted
/// by editing one field.
/// </param>
/// <param name="MeridianResponse">What to do once the action threshold is passed.</param>
/// <param name="HorizonActionDeg">
/// Altitude floor in degrees. At or below this the mount is considered to be in the horizon limit.
/// </param>
/// <param name="HorizonWarnExtraDeg">
/// Degrees ABOVE <paramref name="HorizonActionDeg"/> at which to start warning. An extra again, and
/// note the direction reverses: altitude falls toward its limit while hour angle rises toward its
/// own. Making both an "extra" is what keeps warn-before-action true for both without the caller
/// having to remember which way each quantity runs.
/// </param>
/// <param name="HorizonResponse">What to do once the altitude floor is passed.</param>
/// <remarks>
/// <b>Both responses default to <see cref="MountLimitResponse.StopTracking"/>, and parking is
/// opt-in.</b> Parking is itself MOTION, across a path nothing has checked -- a mount stopped at
/// 8 deg altitude may be a hand's width from a tripod leg, and a park slew from there is the one
/// command most likely to find it. Stopping tracking is the minimal intervention that keeps the
/// situation from getting worse, which is what a safety limit is for; stowing the rig is a
/// different want (an unattended dawn) and belongs to whoever asks for it. Session end already
/// parks via <c>Finalise</c>, so the overnight case is covered without making every limit a slew.
/// </remarks>
public sealed record MountLimitConfiguration(
    bool Enabled = false,
    double MeridianWarnMinutes = 20.0,
    double MeridianActionExtraMinutes = 20.0,
    MountLimitResponse MeridianResponse = MountLimitResponse.StopTracking,
    double HorizonActionDeg = 10.0,
    double HorizonWarnExtraDeg = 5.0,
    MountLimitResponse HorizonResponse = MountLimitResponse.StopTracking)
{
    /// <summary>The absolute hour angle, in minutes past the meridian, at which
    /// <see cref="MeridianResponse"/> happens.</summary>
    public double MeridianActionMinutes => MeridianWarnMinutes + Math.Max(0.0, MeridianActionExtraMinutes);

    /// <summary>The absolute altitude (degrees) at which warning starts.</summary>
    public double HorizonWarnDeg => HorizonActionDeg + Math.Max(0.0, HorizonWarnExtraDeg);

    /// <summary>
    /// The latest a meridian flip may be scheduled on this rig: the configured
    /// <paramref name="requestedLatestMinutesAfter"/>, or the limit's action threshold less
    /// <see cref="FlipClearanceMinutes"/> if that is sooner. Unconstrained when limits are off.
    /// </summary>
    /// <remarks>
    /// <para><b>The limit is the ultimate clamp: the safety bound caps the preference, never the
    /// other way round.</b> How long to keep imaging before flipping is a want; where the tube meets
    /// the pier is a fact. The tempting inverse -- deriving the limit as "flip deadline plus a
    /// margin", which is the same threshold-plus-EXTRA trick this record uses for warn/act -- is
    /// WRONG here: raise the flip deadline to an hour and the mechanical limit would silently follow
    /// it into the pier.</para>
    ///
    /// <para>Without this the two settings simply race, and the limit wins: a flip deadline past the
    /// action threshold means the mount is stopped at the very moment it was about to do the right
    /// thing, ending the night instead of flipping.</para>
    /// </remarks>
    public double ClampFlipLatestMinutes(double requestedLatestMinutesAfter)
        => Enabled
            ? Math.Min(requestedLatestMinutesAfter, MeridianActionMinutes - FlipClearanceMinutes)
            : requestedLatestMinutesAfter;

    /// <summary>
    /// Minutes of headroom left between the last sanctioned flip and the limit acting, so the flip
    /// has somewhere to happen rather than being commanded into the stop.
    /// </summary>
    /// <remarks>
    /// A flip is a slew plus a plate-solve recenter plus a guider restart, all of it while the mount
    /// keeps tracking toward the limit. Five minutes is a deliberately modest allowance -- enough
    /// that the flip is not commanded at the instant of the stop, not so much that it eats a
    /// tightly-configured window.
    /// </remarks>
    public const double FlipClearanceMinutes = 5.0;
}

/// <summary>
/// Pure decision function for mount safety limits. Inputs are scalars plus the configuration record,
/// so the tests need no devices, no clock and no async -- the same shape as
/// <see cref="MeridianFlipDecision"/>, which this sits beside and does not replace.
/// </summary>
/// <remarks>
/// <para><b>This is not the meridian FLIP.</b> A flip is a scheduling decision about a target that
/// will keep being imaged from the other side; a meridian LIMIT is a mechanical bound that says the
/// mount must stop whatever the schedule wanted. A rig can have one, both or neither, and the flip
/// window is normally well inside the limit. <see cref="MeridianFlipDecision"/> owns the former.</para>
///
/// <para><b>Departure from GSServer, deliberately: the horizon test keys on HOUR ANGLE, not pier
/// side.</b> GSS gates its GEM horizon check on <c>SideOfPier == pierEast</c>. The intent is right --
/// only act when the pointing is getting WORSE -- but altitude is a SKY quantity: it is maximal at
/// upper transit and falls monotonically until lower transit, so <c>HA &gt; 0</c> IS "descending",
/// exactly and in both hemispheres, with no dependence on a driver convention. Keying on HA makes the
/// rule true for AltAz and fork mounts as well, which have no pier side at all.</para>
///
/// <para><b>The MERIDIAN test is the opposite case, and the pointing state is load-bearing there.</b>
/// It is a test on the RA AXIS, and the same hour angle puts that axis in two different places: a
/// GEM that has not flipped (<see cref="PointingState.ThroughThePole"/>, counterweight down while
/// looking east) is swinging its tube toward the pier as it tracks west, while one that has flipped
/// (<see cref="PointingState.Normal"/>) is 12 h round on the same axis and moving AWAY from it. The
/// first cut read the hour angle alone and so stopped every rig about 30 minutes after a successful
/// flip -- the moment its hour angle reached the action threshold -- which with the flip clamp in
/// place is the one time a rig is guaranteed to be there. A driver that cannot report the state
/// (<see cref="PointingState.Unknown"/>) gets the hour-angle approximation, labelled as the weaker
/// tier; a driver whose report is wrong gets a wrong limit, which no arithmetic here can repair.</para>
/// </remarks>
public static class MountLimits
{
    /// <summary>
    /// The pointing state a mechanical limit may trust: the reported one when the driver MEASURED it,
    /// <see cref="PointingState.Unknown"/> otherwise. A state derived from the hour angle
    /// (<see cref="PointingStateSource.Computed"/>) reads as post-flip west of the meridian whatever the
    /// mount actually did, which would silence the meridian test on exactly the rig tracking into its
    /// pier; Unknown makes <see cref="Evaluate"/> fall back to the hour-angle reading instead.
    /// </summary>
    public static PointingState TrustedPointingState(PointingStateSource source, PointingState reported)
        => source is PointingStateSource.Measured ? reported : PointingState.Unknown;

    /// <summary>
    /// As <see cref="TrustedPointingState(PointingStateSource, PointingState)"/>, but for a caller that
    /// has VERIFIED the state independently of the driver -- which today means a
    /// <see cref="Session"/>, whose <c>_verifiedPointingState</c> is latched when a goto lands (the one
    /// instant a computed report is certainly right, because the goto went to the side the destination
    /// called for) and moved thereafter only by a flip
    /// <see cref="MeridianFlipVerification"/> read off the image.
    /// </summary>
    /// <param name="verified">
    /// The caller's own answer, or <see cref="PointingState.Unknown"/> when it does not have one yet.
    /// </param>
    /// <remarks>
    /// <para>Three cases, and the middle one is the point. A <see cref="PointingStateSource.Measured"/>
    /// driver still wins outright: it reads its own mechanics on every poll, whereas a latched value
    /// only moves on a slew edge and would be STALE between them. A
    /// <see cref="PointingStateSource.Computed"/> driver with a verified state gets that state, which is
    /// strictly better than the hour-angle approximation the two-argument overload falls back to.
    /// Without one, nothing has changed and the answer is still <see cref="PointingState.Unknown"/>.</para>
    /// <para><b>A caller with no session must keep using the two-argument overload</b>, and
    /// <c>MountLimitWatcher</c> does: it has no run, therefore no latch and no flip history, so it has
    /// no verified state to offer and would only be passing <see cref="PointingState.Unknown"/>. Do not
    /// unify the two call sites.</para>
    /// </remarks>
    public static PointingState TrustedPointingState(
        PointingStateSource source, PointingState reported, PointingState verified)
        => source is PointingStateSource.Measured
            ? reported
            : verified;

    /// <summary>
    /// Decide whether the mount is at a safety limit.
    /// </summary>
    /// <param name="hourAngleHours">
    /// Current hour angle, signed hours: negative = east of the meridian (rising), positive = west
    /// (descending). <see cref="double.NaN"/> disables the meridian test only.
    /// </param>
    /// <param name="pointingState">
    /// The mount's reported pointing state, which decides WHICH WAY the hour angle counts toward the
    /// meridian limit. <see cref="PointingState.ThroughThePole"/> (ASCOM <c>pierWest</c>: counterweight
    /// down while looking east) is where a GEM sits from the slew to a rising target until it flips, and
    /// tracking past the meridian in that state swings the tube toward the pier -- the classic case.
    /// <see cref="PointingState.Normal"/> (<c>pierEast</c>: counterweight down while looking west) is
    /// where it sits AFTER the flip; tracking west from there moves the tube AWAY from the pier however
    /// far it goes, and the hazard is instead pointing EAST of the meridian -- the mirror case, which a
    /// wrong-way goto or a bad sync produces. <see cref="PointingState.Unknown"/> falls back to reading
    /// the hour angle as the offset (right for a mount that has not flipped, wrong after one), because a
    /// driver that cannot say leaves nothing better. Ignored by the horizon test, which is about the sky.
    /// </param>
    /// <param name="primaryAxisAngleDeg">
    /// The RA axis angle from the counterweight-down home, degrees, folded into (-180, 180]
    /// (<see cref="IMountDriver.GetAxisAngleAsync"/>), or <see langword="null"/> when the driver cannot
    /// model its axis. <b>When present it WINS</b>: <c>|angle| - 90</c> is how far the counterweight is
    /// above horizontal whatever the pointing state, read without clock, site or sync, so the hour angle
    /// and pointing state are not consulted for the meridian test at all -- a fallback, never a
    /// cross-check (the plan's invariant: an unsynced mount is the case the limit exists for, so the two
    /// must not be required to agree before acting). NaN is treated as null.
    /// </param>
    /// <param name="altitudeDeg">
    /// Current pointing altitude in degrees. <see cref="double.NaN"/> disables the horizon test only.
    /// </param>
    /// <param name="isTracking">
    /// Whether the mount is tracking. The HORIZON test is gated on this and the meridian test is not,
    /// which looks inconsistent and is not: a parked or idle mount routinely sits below the horizon
    /// limit (pointed at the pole, or stowed), so an ungated horizon test would alarm forever at
    /// exactly the times nothing is at risk. Being past the meridian limit is a mechanical fact about
    /// where the tube is, true whether or not the motors are running.
    /// </param>
    /// <param name="alreadyActed">
    /// Has the caller already carried out this limit's response since the last time the verdict was
    /// clear? GSServer's equivalent is the <c>SlewState != SlewType.SlewPark</c> guard, commented
    /// "only hit this once while in limit": the check runs on a poll loop, so without the latch a
    /// park is re-commanded every tick and the park slew restarts forever, never arriving. Passing
    /// <see langword="true"/> downgrades an action verdict to <see cref="MountLimitResponse.Warn"/>;
    /// the caller clears its latch when <see cref="MountLimitVerdict.IsBreached"/> goes false.
    /// </param>
    /// <param name="config">The rig's limits.</param>
    /// <returns>
    /// The most severe verdict across both limits, ranked on what it would DO: any action outranks
    /// any warning, and among actions the stronger response wins -- <see cref="MountLimitResponse.Park"/>
    /// is a superset of <see cref="MountLimitResponse.StopTracking"/>, so taking it also satisfies the
    /// limit that only wanted tracking stopped. <see cref="MountLimitKind.Meridian"/> breaks an exact
    /// tie, because it is the one that ends with the tube against the pier rather than merely pointed
    /// somewhere useless. <see cref="MountLimitVerdict.Kind"/> therefore names the limit DRIVING the
    /// response, which need not be the only one breached.
    /// </returns>
    public static MountLimitVerdict Evaluate(
        double hourAngleHours,
        PointingState pointingState,
        double? primaryAxisAngleDeg,
        double altitudeDeg,
        bool isTracking,
        bool alreadyActed,
        MountLimitConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!config.Enabled)
        {
            return MountLimitVerdict.Clear;
        }

        var meridian = EvaluateMeridian(hourAngleHours, pointingState, primaryAxisAngleDeg, config);
        var horizon = EvaluateHorizon(hourAngleHours, altitudeDeg, isTracking, config);

        // Most severe wins; the meridian breaks an exact tie. Comparing on Response alone is not
        // enough -- a Park that has only WARNED must not outrank a StopTracking that is actually due.
        var verdict = Rank(meridian) >= Rank(horizon) ? meridian : horizon;

        // The latch. Downgrade to Warn rather than to Clear: the mount is still in the limit and the
        // user must keep being told so, we simply must not command the same recovery twice.
        return alreadyActed && verdict.IsBreached && !verdict.IsWarningOnly
            ? verdict with { Response = MountLimitResponse.Warn, Latched = true }
            : verdict;
    }

    /// <summary>
    /// Severity for tie-breaking: an action outranks any warning, and within each the configured
    /// response orders the rest.
    /// </summary>
    private static int Rank(MountLimitVerdict verdict) => verdict switch
    {
        { IsBreached: false } => 0,
        { IsWarningOnly: true } => 1,
        _ => 2 + (int)verdict.Response,
    };

    private static MountLimitVerdict EvaluateMeridian(
        double hourAngleHours, PointingState pointingState, double? primaryAxisAngleDeg, MountLimitConfiguration config)
    {
        double offsetMinutes;
        MountLimitBasis basis;
        if (primaryAxisAngleDeg is { } axisDeg && !double.IsNaN(axisDeg))
        {
            // The mechanical tier. Home is counterweight down, so |angle| - 90 is how far the counterweight
            // is above horizontal -- the same quantity in both pointing states, with the state, the clock
            // and the site all out of the picture. 1 deg of axis is 4 min of hour angle.
            offsetMinutes = (Math.Abs(axisDeg) - 90.0) * 4.0;
            basis = MountLimitBasis.AxisAngle;
        }
        else
        {
            if (double.IsNaN(hourAngleHours))
            {
                return MountLimitVerdict.Clear;
            }

            // The sky-coordinate tier: the offset of the RA axis toward the pier, in hour angle. In the
            // pre-flip (through-the-pole) state it IS the hour angle: west of the meridian the tube swings
            // down toward the pier. After a flip (Normal) the same axis is 12 h round, so west is safe and
            // EAST is the hazard -- the sign flips. Unknown keeps the pre-flip reading, the approximation
            // this limit shipped with. (The zone has an upper edge too -- some 12 h less the limit, where
            // the tube comes back up on the far side -- but that is lower culmination, below the horizon
            // for anything imaged.)
            var offsetHours = pointingState is PointingState.Normal ? -hourAngleHours : hourAngleHours;
            offsetMinutes = offsetHours * 60.0;
            basis = MountLimitBasis.HourAngle;
        }

        // Counterweight-down side only: the tube is rising away from the pier and getting safer.
        if (offsetMinutes < config.MeridianWarnMinutes)
        {
            return MountLimitVerdict.Clear;
        }

        return new MountLimitVerdict(
            MountLimitKind.Meridian,
            config.MeridianResponse,
            offsetMinutes - config.MeridianActionMinutes,
            basis);
    }

    private static MountLimitVerdict EvaluateHorizon(
        double hourAngleHours,
        double altitudeDeg,
        bool isTracking,
        MountLimitConfiguration config)
    {
        if (double.IsNaN(altitudeDeg) || !isTracking)
        {
            return MountLimitVerdict.Clear;
        }

        // Only while descending. A rising target at 8 deg will be at 12 deg shortly and needs no
        // intervention; acting on it would refuse every target that starts low in the east, which is
        // most of a night's early schedule. HA > 0 is exactly "past upper transit", where altitude
        // falls monotonically to lower transit -- see the class remarks on why this and not pier side.
        // An unknown HA cannot establish that, so the horizon test declines rather than guesses.
        if (double.IsNaN(hourAngleHours) || hourAngleHours <= 0.0)
        {
            return MountLimitVerdict.Clear;
        }

        if (altitudeDeg > config.HorizonWarnDeg)
        {
            return MountLimitVerdict.Clear;
        }

        return new MountLimitVerdict(
            MountLimitKind.Horizon,
            config.HorizonResponse,
            config.HorizonActionDeg - altitudeDeg);
    }
}
