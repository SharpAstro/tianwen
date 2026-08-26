using System;
using System.Numerics;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Excludes the moving body from a frame so a STAR-aligned integration of the same session can be
/// built without it. The comet's own layer is registered on the body; this is the other half of the
/// pair, and the two are meant to be combined.
///
/// <para><b>Why exclusion rather than rejection.</b> The obvious approach is to let kappa-sigma
/// clipping treat the comet as an outlier, and it half works: measured on 10P, rejection took the
/// compact nucleus trail from 35.28% of sky down to 9.85%. It cannot take the DIFFUSE coma, because
/// at a pixel the comet crosses, the coma is present in a large fraction of the frames -- a third of
/// them on C/2025 R2 (SWAN) -- and a third of the samples being elevated is not an outlier
/// population, it is enough to inflate the very sigma meant to detect it. The C/2025 R2 rejection
/// map shows exactly that: 0.086 rejection along the track against 0.036 baseline, about five points
/// extra, leaving a 1.76 sigma ridge in the master.
///
/// The exclusion has no such limit because it does not have to DETECT anything. The ephemeris
/// already says where the body is in every frame, and it is the same rate the comet compose is
/// driven by, so this costs one extra quantity (the anchor) and no new astrometry.</para>
///
/// <para><b>What it costs, and why the cost is smaller than it first looks.</b> A pixel is blanked
/// only while the body is within <see cref="RadiusPx"/> of it, so a pixel at perpendicular distance
/// p from the track loses <c>2*sqrt(R^2 - p^2) / travel</c> of the session, NOT <c>2R/travel</c>.
/// Averaged across the band that is <c>pi*R/(2*travel)</c>, and it tapers to zero at the band edge.
/// On SWAN, R=80 px against 357 px of travel is 35% of frames over a band covering 0.9% of the
/// canvas: about 1.24x the noise there, in exchange for deleting a smooth 1.76 sigma ridge. A smooth
/// ridge is far more damaging than pixel noise, because it is what subtracts as a negative ghost
/// when the layers are combined.</para>
///
/// <para><b>Applied in SOURCE space, before debayer and warp, which is what lets one implementation
/// serve both integration paths.</b> The standard path consumes warped canvas frames and the drizzle
/// path consumes raw CFA in source space, so a canvas-space mask would need writing twice. Both
/// paths already skip NaN samples correctly and identically -- <c>MeanCombiner</c> recomputes the
/// effective count from <c>!isNaN(v) * keep</c> rather than trusting the mask, and
/// <c>DrizzleKernel.DepositOne</c> returns before depositing flux OR weight -- so coverage
/// normalisation needs nothing added for either. Debayer and warp then spread the NaN by their own
/// kernel radius, which enlarges the hole by a pixel or two; that is the harmless direction.</para>
/// </summary>
/// <param name="AnchorRefPx">Where the body is in the REFERENCE FRAME's pixel basis at
/// <paramref name="AnchorEpoch"/>, straight off <c>CometRate.AnchorPx</c>.
///
/// <para>Reference space rather than canvas space, and that is not a detail. The canvas shift is a
/// pure translation and DIFFERS PER LAYER, because each layer's union bounding box is computed from
/// its own transforms -- so a canvas anchor built for one layer is wrong for the other. A rate is
/// immune (a translation cannot change a difference), which is exactly why the compose can state one
/// in canvas pixels and ignore the origin; an absolute position is not immune. Staying in reference
/// space means neither the anchor nor this type ever has to know which layer is being built.</para></param>
/// <param name="RatePxPerHour">Pixels per hour. The same vector the comet compose uses, and the same
/// number in either basis for the reason above.</param>
/// <param name="AnchorEpoch">The instant <paramref name="AnchorRefPx"/> describes.</param>
/// <param name="RadiusPx">Reference-pixel radius of the excluded disk. Round is sufficient for a comet
/// with no measurable tail, which was the case for both bodies this was built against; a body with a
/// real tail needs an elongated region and this is where that would go.</param>
internal readonly record struct CometMask(
    Vector2 AnchorRefPx,
    Vector2 RatePxPerHour,
    DateTimeOffset AnchorEpoch,
    float RadiusPx)
{
    /// <summary>Where the body sits in reference-frame pixels at <paramref name="when"/>.</summary>
    public Vector2 ReferencePositionAt(DateTimeOffset when)
    {
        var hours = (float)(when - AnchorEpoch).TotalHours;
        return AnchorRefPx + RatePxPerHour * hours;
    }

    /// <summary>
    /// The same position expressed in one frame's own pixels, ready to punch before debayer.
    /// </summary>
    /// <param name="sourceToReference">That frame's registration solution, WITHOUT the canvas shift
    /// and without the comet compose: the plain star solution taking its pixels onto the reference's.</param>
    /// <param name="when">The frame's exposure start, the same stamp registration composes against.</param>
    /// <returns><c>null</c> when the affine is singular, which no real registration produces.</returns>
    public Vector2? SourcePositionAt(Matrix3x2 sourceToReference, DateTimeOffset when)
    {
        if (!Matrix3x2.Invert(sourceToReference, out var referenceToSource))
        {
            return null;
        }
        return Vector2.Transform(ReferencePositionAt(when), referenceToSource);
    }

    /// <summary>
    /// The radius restated in the frame's own pixels. Registration scale sits within 0.028% of unity
    /// on real data, so this is very nearly <see cref="RadiusPx"/>; it is derived rather than assumed
    /// because a resampled or binned input would make the difference real, and a mask that is
    /// silently too small leaves exactly the residue it exists to remove.
    /// </summary>
    public float SourceRadius(Matrix3x2 sourceToReference)
    {
        // |det| is the area scale, so its square root is the linear scale for a similarity.
        var scale = MathF.Sqrt(MathF.Abs(sourceToReference.GetDeterminant()));
        return scale > 1e-6f ? RadiusPx / scale : RadiusPx;
    }

    /// <summary>
    /// Writes NaN over a disk in every channel, in place. Returns how many pixels of one channel
    /// were blanked, which is 0 when the disk misses the frame entirely.
    /// </summary>
    /// <remarks>
    /// The count is not decoration. A mask placed in the wrong basis -- frame pixels where canvas
    /// pixels were meant, say -- lands somewhere arbitrary, and the resulting master looks entirely
    /// plausible while containing both an untouched comet and a hole somewhere else. Zero blanked
    /// pixels across every frame is the signature of that mistake, so the caller checks for it.
    /// </remarks>
    public static int Punch(Image frame, Vector2 centre, float radius)
    {
        if (radius <= 0f || !float.IsFinite(centre.X) || !float.IsFinite(centre.Y))
        {
            return 0;
        }

        var x0 = Math.Max(0, (int)MathF.Floor(centre.X - radius));
        var x1 = Math.Min(frame.Width - 1, (int)MathF.Ceiling(centre.X + radius));
        var y0 = Math.Max(0, (int)MathF.Floor(centre.Y - radius));
        var y1 = Math.Min(frame.Height - 1, (int)MathF.Ceiling(centre.Y + radius));
        if (x1 < x0 || y1 < y0)
        {
            return 0;
        }

        var r2 = radius * radius;
        var blanked = 0;
        for (var c = 0; c < frame.ChannelCount; c++)
        {
            var plane = frame.GetChannelArray(c);
            var countThis = 0;
            for (var y = y0; y <= y1; y++)
            {
                var dy = y - centre.Y;
                var dy2 = dy * dy;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x - centre.X;
                    if (dx * dx + dy2 <= r2)
                    {
                        plane[y, x] = float.NaN;
                        countThis++;
                    }
                }
            }
            blanked = countThis;
        }
        return blanked;
    }
}
