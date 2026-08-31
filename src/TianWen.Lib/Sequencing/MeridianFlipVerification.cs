using System;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// What a pair of plate solves says about whether the OTA physically changed sides of the pier.
/// </summary>
public enum FlipEvidence
{
    /// <summary>
    /// The two solves have nothing to say: one is missing, or centre-only with no CD matrix, or the
    /// field turned by an angle that is neither a half turn nor nothing. Callers fall back to
    /// whatever the mount reports -- an unreadable witness must never be able to overrule one.
    /// </summary>
    Inconclusive,

    /// <summary>The field is rotated a half turn. The tube went over.</summary>
    Flipped,

    /// <summary>The field is where it was. Whatever the mount says, nothing moved.</summary>
    NotFlipped,
}

/// <summary>
/// <see cref="MeridianFlipVerification.FromSolves"/>'s answer, carrying the measured rotation so a
/// log line can say how far the field actually turned rather than only how it was classified.
/// </summary>
/// <param name="Evidence">The classification.</param>
/// <param name="RotationDeltaDeg">
/// Field rotation between the two solves in (-180, 180], or <see cref="double.NaN"/> when there was
/// no usable pair.
/// </param>
public readonly record struct FlipVerdict(FlipEvidence Evidence, double RotationDeltaDeg)
{
    /// <summary>Nothing to go on.</summary>
    public static readonly FlipVerdict Inconclusive = new(FlipEvidence.Inconclusive, double.NaN);
}

/// <summary>
/// Reads a meridian flip off the IMAGE rather than off the mount, by comparing the field position
/// angle of the last solve before the flip against the first one after it. Pure, so the judgement
/// is testable without devices, time or async -- the same shape as <see cref="MeridianFlipDecision"/>
/// and <see cref="MountLimits"/>, which it sits beside.
/// <para>
/// It exists because the mount is not always a reliable witness to its own flip. A driver whose
/// <see cref="Devices.IMountDriver.PointingStateSource"/> is
/// <see cref="Devices.PointingStateSource.Computed"/> -- the LX200 base driver, SGP -- derives the
/// pier side from the hour angle, so it reports the flipped state the moment the POINTING crosses
/// the meridian, whether or not the tube ever moved. Both the hour-angle check the flip used to
/// succeed on and the pier-side change it treats as an auto-flip are therefore trivially true on
/// such a mount, and a rig that tracked straight through goes on imaging with the field the wrong
/// way up and the guider's Dec sense inverted. The frame is the one witness that does not go through
/// the mount.
/// </para>
/// <para>
/// Design notes in <c>docs/plans/meridian-flip-verification.md</c>. Three things must not be changed
/// without reading it. The test is PARITY-INDEPENDENT by construction: a flip is a rotation, never a
/// reflection, so no mirror-count or handedness term belongs here. Once a rotator exists the caller
/// must gate on "no rotator moved across the flip", or a deliberate framing rotation reads as a flip.
/// And it is only meaningful on a GERMAN equatorial mount, because only there is the field position
/// angle constant except across a flip: a fork (<see cref="Devices.AlignmentMode.Polar"/>) tracks
/// straight through the meridian and never flips, while an ALT-AZ mount has no pier side at all and
/// rotates the field CONTINUOUSLY, so a half turn measured there is elapsed tracking rather than
/// anything mechanical. <c>ImagingLoopAsync</c> gates the whole flip block on
/// <see cref="Devices.AlignmentMode.GermanPolar"/> and that gate is what keeps this sound -- nothing
/// in here re-checks it.
/// </para>
/// </summary>
public static class MeridianFlipVerification
{
    /// <summary>
    /// How far from an exact half turn (or from nothing at all) a measured rotation may sit and still
    /// be read as one. A German flip is a half turn to within the mount's mechanics and a plate
    /// solve recovers the angle to a small fraction of a degree, so this is loose on purpose: the two
    /// answers are 180 degrees apart and the only thing worth being strict about is refusing
    /// everything in between, which is a field that turned for some third reason.
    /// </summary>
    public const double DefaultToleranceDeg = 20.0;

    /// <summary>
    /// Classify the rotation between the solve taken before a flip and the one taken after it.
    /// </summary>
    /// <param name="before">Last successful solve before the flip, from the SAME camera.</param>
    /// <param name="after">First successful solve after it.</param>
    /// <param name="toleranceDeg">See <see cref="DefaultToleranceDeg"/>.</param>
    /// <remarks>
    /// Both solves must come from one camera. On a multi-OTA rig the sensors sit at different rolls
    /// in their focusers, so a pair drawn from two of them differs by that constant and says nothing
    /// about the pier.
    /// </remarks>
    public static FlipVerdict FromSolves(WCS? before, WCS? after, double toleranceDeg = DefaultToleranceDeg)
    {
        if (before is not { HasCDMatrix: true } from || after is not { HasCDMatrix: true } to)
        {
            // A solver that only reports a centre (or a solve that failed) carries no orientation.
            return FlipVerdict.Inconclusive;
        }

        var delta = to.RotationDeltaDeg(from);
        var turn = Math.Abs(delta);

        if (Math.Abs(turn - 180.0) <= toleranceDeg)
        {
            return new FlipVerdict(FlipEvidence.Flipped, delta);
        }

        if (turn <= toleranceDeg)
        {
            return new FlipVerdict(FlipEvidence.NotFlipped, delta);
        }

        // Something rotated the field by an angle a pier flip cannot produce. Report the measurement
        // and decline to draw a conclusion from it, rather than rounding it to the nearer answer.
        return new FlipVerdict(FlipEvidence.Inconclusive, delta);
    }
}
