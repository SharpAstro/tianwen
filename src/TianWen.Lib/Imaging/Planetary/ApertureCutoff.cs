using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging.Optics;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Where an averaged power spectrum falls to its noise floor (<see cref="ApertureCutoff.Measure"/>).
/// </summary>
/// <param name="Floor">The noise floor: the corners' mean power, from <see cref="ApertureCutoff.FloorFrom"/> cycles a pixel out.</param>
/// <param name="FloorStandardError">The floor's standard error.</param>
/// <param name="CutoffCyclesPerPixel">
/// The highest frequency at which two neighbouring rings each stand <see cref="ApertureCutoff.DetectionSigmas"/> standard
/// errors above the floor (0 when none does), or <see cref="ApertureCutoff.FloorFrom"/> when the signal runs into the corners.
/// </param>
/// <param name="BeyondCorners">
/// The power still falls through the corners, so the signal reaches them: the cutoff lies past them and
/// <paramref name="CutoffCyclesPerPixel"/> is only a lower bound.
/// </param>
/// <param name="CornerSlopeSigmas">The corners' slope over its standard error: negative is falling.</param>
public readonly record struct CutoffMeasurement(double Floor, double FloorStandardError, double CutoffCyclesPerPixel, bool BeyondCorners, double CornerSlopeSigmas);

/// <summary>
/// A pupil's cutoff read off a capture's averaged power spectrum (<see cref="PlanetaryPowerSpectrum"/>), and the aperture it
/// implies: <c>D = u lambda</c>, with u the cutoff in cycles a radian and lambda the short edge of the plane's passband, where
/// the last power comes from (docs/plans/planetary-restoration.md, R1, "which telescope").
/// <para>
/// A measured cutoff is where the signal SANK INTO THE NOISE, which is a question of the capture's signal to noise ratio
/// before it is one of the pupil: on synthetic captures of 150 frames both a 102 and a 254 mm pupil came back at the same
/// 0.25 cycles a pixel, 72 and 74 % of their cutoffs. So the measured cutoff is only a lower bound on the pupil's, never
/// above it, since past the pupil's cutoff there is nothing to find. Read as a bound, it decides between two candidates of
/// different size where the larger one's power reaches past the smaller one's cutoff (<see cref="RulesOut"/>). Pooling the
/// rings past the smaller cutoff instead was tried and is weaker: the noise-only rings near the corners carry the smallest
/// errors and swamp the few that hold signal.
/// </para>
/// </summary>
public static class ApertureCutoff
{
    /// <summary>The floor is read from this many cycles a pixel outward, which only the corners reach (Nyquist is 0.5).</summary>
    public const double FloorFrom = 0.6;

    /// <summary>How many standard errors above the floor a ring must stand to hold signal.</summary>
    public const double DetectionSigmas = 3;

    /// <summary>How many standard errors of fall through the corners say the signal reaches them.</summary>
    public const double CornerFallSigmas = 3;

    /// <summary>Measures the cutoff, or null when the spectrum has no corners (fewer than two frames, or no ring past <see cref="FloorFrom"/>).</summary>
    public static CutoffMeasurement? Measure(ImmutableArray<SpectrumRing> rings)
    {
        double wSum = 0, wf = 0, wp = 0, wff = 0, wfp = 0;
        var corners = 0;
        foreach (var ring in rings)
        {
            if (ring.CyclesPerPixel < FloorFrom || !(ring.StandardError > 0) || double.IsInfinity(ring.StandardError))
            {
                continue;
            }
            var w = 1 / (ring.StandardError * ring.StandardError);
            wSum += w;
            wf += w * ring.CyclesPerPixel;
            wp += w * ring.Power;
            wff += w * ring.CyclesPerPixel * ring.CyclesPerPixel;
            wfp += w * ring.CyclesPerPixel * ring.Power;
            corners++;
        }
        if (corners < 3)
        {
            return null;
        }

        var floor = wp / wSum;
        var floorError = Math.Sqrt(1 / wSum);
        // The corners' weighted straight line: a slope reliably below zero is signal still falling toward the floor.
        var det = (wSum * wff) - (wf * wf);
        var slope = ((wSum * wfp) - (wf * wp)) / det;
        var slopeError = Math.Sqrt(wSum / det);
        var slopeSigmas = slope / slopeError;
        if (slopeSigmas < -CornerFallSigmas)
        {
            return new CutoffMeasurement(floor, floorError, FloorFrom, BeyondCorners: true, slopeSigmas);
        }

        var cutoff = 0.0;
        var previousAbove = false;
        foreach (var ring in rings)
        {
            if (ring.CyclesPerPixel >= FloorFrom)
            {
                break;
            }
            var z = (ring.Power - floor) / Math.Sqrt((ring.StandardError * ring.StandardError) + (floorError * floorError));
            var above = z > DetectionSigmas;
            if (above && previousAbove)
            {
                cutoff = ring.CyclesPerPixel;
            }
            previousAbove = above;
        }
        return new CutoffMeasurement(floor, floorError, cutoff, BeyondCorners: false, slopeSigmas);
    }

    /// <summary>
    /// A measured cutoff must exceed a candidate aperture's by this before it rules the candidate out: a plane passes a little
    /// light below the short edge its cutoff is reckoned at, and a ring is a ring wide.
    /// </summary>
    public const double RuleOutMargin = 1.15;

    /// <summary>
    /// Whether the capture rules out an aperture of <paramref name="candidateApertureM"/>: the measured cutoff (a lower bound on
    /// the pupil's) is past the candidate's own by more than <see cref="RuleOutMargin"/>, so there is power the candidate could
    /// not have passed. A capture that does not rule a candidate out does not confirm it either: the signal may simply have
    /// sunk into the noise short of the cutoff.
    /// </summary>
    public static bool RulesOut(CutoffMeasurement measurement, double candidateApertureM, double arcsecPerPixel, double wavelengthM)
        => RulesOut(measurement.CutoffCyclesPerPixel, candidateApertureM, arcsecPerPixel, wavelengthM);

    /// <summary><see cref="RulesOut(CutoffMeasurement, double, double, double)"/> for a cutoff measured some other way (<see cref="MeasureCross"/>).</summary>
    public static bool RulesOut(double cutoffCyclesPerPixel, double candidateApertureM, double arcsecPerPixel, double wavelengthM)
        => ApertureM(cutoffCyclesPerPixel, arcsecPerPixel, wavelengthM) > candidateApertureM * RuleOutMargin;

    /// <summary>
    /// Where the detail two disjoint half-stacks share ends (<see cref="PlanetaryPowerSpectrum.Cross"/>): the highest frequency
    /// at which two neighbouring rings each stand <see cref="DetectionSigmas"/> standard errors above zero, or 0 when none does.
    /// There is no floor: the halves' noise is independent, so past the detail the cross-spectrum is zero. Like any measured
    /// cutoff it is a lower bound on the pupil's, never above it.
    /// </summary>
    public static double MeasureCross(ImmutableArray<SpectrumRing> rings)
    {
        var cutoff = 0.0;
        var previousAbove = false;
        foreach (var ring in rings)
        {
            var above = ring.StandardError > 0 && ring.Power / ring.StandardError > DetectionSigmas;
            if (above && previousAbove)
            {
                cutoff = ring.CyclesPerPixel;
            }
            previousAbove = above;
        }
        return cutoff;
    }

    /// <summary>The aperture whose cutoff is <paramref name="cyclesPerPixel"/> at <paramref name="arcsecPerPixel"/> and <paramref name="wavelengthM"/>.</summary>
    public static double ApertureM(double cyclesPerPixel, double arcsecPerPixel, double wavelengthM)
        => cyclesPerPixel / arcsecPerPixel * ShortExposurePsf.ArcsecPerRadian * wavelengthM;

    /// <summary>The cutoff, in cycles a pixel, of an aperture of <paramref name="apertureM"/> at <paramref name="arcsecPerPixel"/> and <paramref name="wavelengthM"/>.</summary>
    public static double CyclesPerPixel(double apertureM, double arcsecPerPixel, double wavelengthM)
        => apertureM / wavelengthM / ShortExposurePsf.ArcsecPerRadian * arcsecPerPixel;
}
