using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// Finds the plate scale of a frame that states none (#2249): no <c>PIXSCALE</c>, no <c>FOCALLEN</c> with a pixel size,
/// no solved matrix. It walks a ladder of believed scales, widest field first, and asks <see cref="QuadScaleRecovery"/>
/// at each rung what scale the stars imply.
/// </summary>
/// <remarks>
/// <para><b>It answers a SCALE, never a solution.</b> The solve then runs from that scale as it would from a header's,
/// with its own recovery, seed, parity race and acceptance gate, so a wrong answer here costs one failed solve and
/// never a wrong WCS.</para>
/// <para><b>Why a ladder and not one question.</b> A quad's five ratios are free of scale, but which stars the quads are
/// built from is not: a rung compares the brightest detections with the brightest catalogue stars inside the frame it
/// believes in, and the two sets are the same stars only when that frame is about the real one. Measured on six
/// masters from 1.6 to 6.9 arcsec/px (<c>ScaleLadderProbe</c>, 2026-11-20), one rung answers over a span of believed
/// scales that is 1.57 wide on the narrowest frame and 2.0 on the widest, and lies between 0.5 and 1.25 of the true
/// scale. So the rungs stand <see cref="RungStep"/> apart, which puts at least two inside every span measured.</para>
/// <para><b>Both sides are cut to the same count.</b> The solver's own recovery takes the brightest 500 of each side
/// independently, which compares 500 detections with every catalogue star a narrow frame holds: on a 1.8 degree frame
/// with 288 catalogue stars that never answered at any believed scale (8 candidates at best), and with both sides cut
/// to the smaller count it answers from 0.7 to 1.25 of the true scale with up to 49. The recovery itself is
/// unchanged; the ladder hands it equal spans.</para>
/// <para><b>An answer is believed only when a second question agrees.</b> The second is asked at a believed scale
/// apart from the rung's, at <see cref="ConfirmAtFractions"/> of the implied scale in turn: 0.8 first, where the
/// recovery answers best (42 to 103 candidates on those six frames, against 11 to 33 at the implied scale itself).
/// A rung that itself stood at 0.8 is the common case, since that is where a rung answers most readily, and asking
/// there again would compare the same stars; so the next fractions are asked instead. With the implied scale as the
/// only other question, 7 of the 24 frozen Vela panels were declined on a right first answer (the recovery does not
/// answer at their true scale); with these four all 24 are confirmed (<c>ScaleLadderTests</c>).</para>
/// <para><b>What it does not reach, measured the same day.</b> A field with too few catalogue stars: a 1.6 by 1.1
/// degree frame at high galactic latitude holds 66 Tycho-2 stars and no rung answers. And the two wide-lens masters
/// tried (18 and 26 degrees across): no rung answers even at the true scale, at any catalogue depth tried, so the
/// ladder does not start wider than <see cref="WidestFieldDeg"/>. An external solver stays the fallback for both.</para>
/// </remarks>
internal static class ScaleLadder
{
    /// <summary>
    /// The long side of the frame the first rung believes in, in degrees. A rung answers frames up to about 1.7 times
    /// wider than it believes, so the ladder reaches fields to about 20 degrees, past the 8 degrees the widest
    /// answering master has and up to where the recovery is measured not to answer at all.
    /// </summary>
    internal const double WidestFieldDeg = 12.0;

    /// <summary>The factor between one rung's believed scale and the next, narrower one's.</summary>
    internal const double RungStep = 1.25;

    /// <summary>
    /// The believed scales of the confirming questions, as fractions of the scale the first answer implied, in the order
    /// asked: where the recovery answers best first, the implied scale itself last.
    /// </summary>
    internal static ReadOnlySpan<double> ConfirmAtFractions => [0.8, 0.9, 0.7, 1.0];

    /// <summary>
    /// How far the confirming answer may stand from the first, as a fraction. The recovery's answers on one frame lie
    /// within 0.2 % of each other across rungs, so 1 % refuses chance and nothing real.
    /// </summary>
    internal const double AgreementTolerance = 0.01;

    /// <summary>The loop's own end, far under any scale Tycho-2 can answer: the ladder stops earlier, where a rung's frame holds too few stars.</summary>
    internal const double NarrowestScaleArcsec = 0.05;

    /// <param name="ScaleArcsec">The scale the stars imply, arcsec per pixel.</param>
    /// <param name="RungsAsked">Rungs asked up to and including the one that answered.</param>
    /// <param name="RungScaleArcsec">The believed scale of the rung that answered.</param>
    /// <param name="Candidates">Agreeing quad pairs behind the rung's answer.</param>
    /// <param name="ConfirmCandidates">Agreeing quad pairs behind the confirming answer.</param>
    internal readonly record struct Found(double ScaleArcsec, int RungsAsked, double RungScaleArcsec, int Candidates, int ConfirmCandidates);

    /// <summary>The first rung's believed scale for a frame of this size, arcsec per pixel.</summary>
    internal static double WidestScaleArcsec(int width, int height) => WidestFieldDeg * 3600.0 / Math.Max(width, height);

    /// <summary>
    /// The radius about the hint that holds every catalogue star a rung can ask for: the half diagonal of the first
    /// rung's frame with the projection's margin on each side.
    /// </summary>
    internal static double QueryRadiusDeg(int width, int height)
    {
        var halfDiagonalPx = 0.5 * Math.Sqrt(((double)width * width) + ((double)height * height));
        return (1.0 + (2.0 * ProjectionMargin)) * halfDiagonalPx * WidestScaleArcsec(width, height) / 3600.0;
    }

    /// <summary>The margin <see cref="CatalogPlateSolver.ProjectCatalogStars"/> keeps by default, which the rungs use as the solver's recovery does.</summary>
    private const float ProjectionMargin = 0.1f;

    /// <summary>
    /// Walks the ladder and returns the scale the stars imply, or null when no rung's answer is confirmed.
    /// </summary>
    /// <param name="detected">Detected centroids, brightest first.</param>
    /// <param name="catalogue">Catalogue stars about the hint out to <see cref="QueryRadiusDeg"/>, brightest first.</param>
    /// <param name="origin">The hint: where the frame is believed to be centred.</param>
    internal static Found? TryFind(
        ReadOnlySpan<Vector2> detected,
        List<(double RA, double Dec, double VMag)> catalogue,
        WCS origin,
        int width,
        int height,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (detected.Length < QuadScaleRecovery.MinStars || catalogue.Count < QuadScaleRecovery.MinStars)
        {
            return null;
        }

        var widest = WidestScaleArcsec(width, height);

        // Each rung's frame lies inside the one before it, so a rung projects only the stars the last one kept.
        var within = catalogue;
        var rungs = 0;
        for (var scale = widest; scale >= NarrowestScaleArcsec; scale /= RungStep)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rungs++;

            // One line per rung asked, whatever it answers: a rung that is asked, does not answer and still holds
            // enough stars says nothing else, and without this a silent log could not tell "asked and got nothing"
            // from "never asked".
            logger?.LogDebug("ScaleLadder: asking rung {Rung} at {Scale:F3}\"/px", rungs, scale);
            var answer = Ask(detected, within, origin, width, height, scale, out var inFrame);
            if (inFrame.Count < QuadScaleRecovery.MinStars)
            {
                // A narrower frame holds fewer still, so no rung below this one can answer.
                logger?.LogDebug("ScaleLadder: rung {Rung} at {Scale:F3}\"/px holds {Stars} catalogue stars, under the {Min} a question needs; the ladder ends",
                    rungs, scale, inFrame.Count, QuadScaleRecovery.MinStars);
                break;
            }

            within = inFrame;
            if (answer is not { } first)
            {
                continue;
            }

            var implied = scale / first.Ratio;
            logger?.LogDebug("ScaleLadder: rung {Rung} at {Scale:F3}\"/px implies {Implied:F4}\"/px from {Candidates} candidates",
                rungs, scale, implied, first.Candidates);

            // A confirming question goes to the whole catalogue: its frame may be wider than this rung's.
            foreach (var fraction in ConfirmAtFractions)
            {
                var believed = Math.Min(fraction * implied, widest);
                if (Math.Abs(believed - scale) < ConfirmApartFromRung * scale)
                {
                    continue;
                }

                if (Ask(detected, catalogue, origin, width, height, believed, out _) is not { } second)
                {
                    logger?.LogDebug("ScaleLadder: asked again at {Believed:F3}\"/px, no answer", believed);
                    continue;
                }

                var confirmed = believed / second.Ratio;
                if (Math.Abs(confirmed - implied) <= AgreementTolerance * implied)
                {
                    logger?.LogDebug("ScaleLadder: confirmed at {Scale:F4}\"/px by {Candidates} candidates, asked at {Believed:F3}\"/px",
                        confirmed, second.Candidates, believed);
                    return new Found(confirmed, rungs, scale, first.Candidates, second.Candidates);
                }

                logger?.LogDebug("ScaleLadder: asked again at {Believed:F3}\"/px, which implies {Other:F4}\"/px and does not agree", believed, confirmed);
            }

            logger?.LogDebug("ScaleLadder: no second question agreed with {Implied:F4}\"/px; the ladder goes on", implied);
        }

        return null;
    }

    /// <summary>
    /// How far a confirming question's believed scale must stand from the answering rung's, as a fraction. A question
    /// asked where the rung already asked compares the same stars and agrees with itself, which confirms nothing. The
    /// confirming scales stand at least 10 % apart, so at most one of them is this near the rung.
    /// </summary>
    internal const double ConfirmApartFromRung = 0.05;

    /// <summary>
    /// One question: the catalogue stars inside the frame a believed scale gives, against the detections, both cut to
    /// the same count. <paramref name="inFrame"/> comes back in the catalogue's order, for the next, narrower rung.
    /// </summary>
    private static QuadScaleRecovery.Recovery? Ask(
        ReadOnlySpan<Vector2> detected,
        List<(double RA, double Dec, double VMag)> catalogue,
        WCS origin,
        int width,
        int height,
        double believedArcsec,
        out List<(double RA, double Dec, double VMag)> inFrame)
    {
        var dim = new ImageDim(believedArcsec, width, height);
        var projected = CatalogPlateSolver.ProjectCatalogStars(
            catalogue, origin, double.DegreesToRadians(believedArcsec / 3600.0), width / 2.0, height / 2.0, dim, xSign: 1.0, ProjectionMargin);

        // The magnitude is not read again: the list's order is the ranking.
        inFrame = new List<(double RA, double Dec, double VMag)>(projected.Count);
        foreach (var star in projected)
        {
            inFrame.Add((star.RA, star.Dec, 0.0));
        }

        var count = Math.Min(Math.Min(detected.Length, projected.Count), QuadScaleRecovery.MaxStars);
        if (count < QuadScaleRecovery.MinStars)
        {
            return null;
        }

        var points = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = new Vector2(projected[i].Pixel.XCentroid, projected[i].Pixel.YCentroid);
        }

        return QuadScaleRecovery.TryRecover(detected[..count], points);
    }
}
