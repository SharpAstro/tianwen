using System;
using System.Numerics;
using TianWen.Lib.Astrometry.Comets;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// The two pieces of arithmetic a comet run does more than once, kept in one place so they cannot
/// be written two ways: composing a frame's star solution with the body's drift, and evaluating the
/// rate fit at an epoch.
/// </summary>
/// <remarks>
/// <para><b>The compose is canvas-space, after the star solution.</b> The star solution puts a frame
/// on the reference's star grid; subtracting the target's drift since the reference epoch pins the
/// target instead. Composed AFTER the star solution because canvas space is the only basis where the
/// target's motion is separable from dither and field rotation: on 10P the dither was 88.6 px against
/// a 44.7 px track, so a frame-space shift is simply the wrong quantity. <see cref="Matrix3x2"/> is
/// row-vector, so <c>starSolution * translation</c> is the correct order and reversing it silently
/// gives the wrong basis. Pinned by <c>CometComposeTests</c>.</para>
///
/// <para><b>Epochs.</b> The compose takes the difference of two frames' exposure START times, and
/// since every frame in a group shares one exposure length that equals the difference of their
/// mid-times, so the compose is indifferent to the convention. The body's absolute position is not:
/// a frame's light is centred on its MID-exposure, so the ephemeris must be evaluated there. At
/// 245 px/h a 30 s exposure puts start and mid 1.0 px apart along the track, and the model this
/// position drives is sampled bilinearly precisely because a half-pixel offset subtracts a dipole.
/// </para>
/// </remarks>
internal static class CometCompose
{
    /// <summary>
    /// A frame's transform onto the COMET-ALIGNED reference grid: its star solution followed by
    /// the body's drift over <paramref name="driftHours"/>, undone.
    /// </summary>
    /// <param name="starSolution">Source pixels onto the reference's star grid.</param>
    /// <param name="ratePxPerHour">The body's canvas rate on that grid.</param>
    /// <param name="driftHours">This frame's exposure start minus the reference's, in hours.</param>
    public static Matrix3x2 ToCometGrid(Matrix3x2 starSolution, Vector2 ratePxPerHour, double driftHours)
        => starSolution * Matrix3x2.CreateTranslation(
            (float)(-ratePxPerHour.X * driftHours),
            (float)(-ratePxPerHour.Y * driftHours));

    /// <summary>Hours from the reference frame's exposure start to this frame's.</summary>
    public static double DriftHours(ImageMeta frame, ImageMeta reference)
        => (frame.ExposureStartTime - reference.ExposureStartTime).TotalHours;

    /// <summary>
    /// Where the body sits on the comet-aligned reference grid: the rate fit carried from its own
    /// anchor epoch to the reference frame's MID-exposure. NOT the bare anchor, which describes the
    /// first ephemeris sample and can be a whole session away.
    /// </summary>
    public static Vector2 BodyOnGrid(CometRate fit, ImageMeta reference)
    {
        var mid = reference.ExposureStartTime + reference.ExposureDuration / 2;
        return fit.AnchorPx + fit.PxPerHour * (float)(mid - fit.AnchorEpoch).TotalHours;
    }
}
