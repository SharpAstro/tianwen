using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>One plane's spectra, measured.</summary>
/// <param name="Plane">The plane: <c>mono</c>, or a Bayer capture's <c>R</c>, <c>G</c> (both greens) and <c>B</c>.</param>
/// <param name="ArcsecPerPixel">The plane's own scale: a Bayer plane is sampled at twice the sensor's pitch.</param>
/// <param name="Cutoff">Where the frames' averaged spectrum met its noise floor (<see cref="ApertureCutoff.Measure"/>).</param>
/// <param name="DetailCyclesPerPixel">
/// Where the detail two disjoint half-stacks share ends (<see cref="ApertureCutoff.MeasureCross"/>). Like the cutoff, a lower
/// bound on the pupil's.
/// </param>
public sealed record PlaneCutoff(string Plane, double ArcsecPerPixel, CutoffMeasurement Cutoff, double DetailCyclesPerPixel);

/// <summary>What a capture's frames say about the telescope that took them (<see cref="PlanetaryApertureEvidence.MeasureAsync"/>).</summary>
/// <param name="Limb">The limb fit on the stack of the best frames, which gives the scale.</param>
/// <param name="ArcsecPerPixel">The sensor's scale: the ephemeris' equatorial radius over the fitted one.</param>
/// <param name="FramesGraded">Every frame, graded.</param>
/// <param name="FramesUsed">The best of them, stacked and put through the spectrum.</param>
/// <param name="Planes">Each plane's cutoff. Empty for an already demosaiced capture, whose interpolation shapes the spectrum.</param>
/// <param name="Spider">The stack's halo, or null when no full annulus fits in the frame.</param>
/// <param name="HaloAtBlack">
/// The fraction of the annulus' raw samples, over the frames used, sitting at their frame's lowest value: a sky clipped at the
/// black level, where a spike worth a fraction of an 8-bit step cannot register, so a halo without a spider there is no
/// evidence. Null with no annulus.
/// </param>
/// <param name="Stack">The stack of the best frames the limb and the halo were measured on, the caller's to keep or write.</param>
public sealed record ApertureEvidence(LimbFit Limb, double ArcsecPerPixel, int FramesGraded, int FramesUsed, ImmutableArray<PlaneCutoff> Planes, SpiderMeasurement? Spider, double? HaloAtBlack, Image Stack);

/// <summary>
/// Which telescope took a capture, from its frames (docs/plans/planetary-restoration.md, R1, "which telescope", #1049). The
/// best frames by the Laplacian are stacked and the stack's limb fitted, which with the ephemeris gives the scale. Each raw
/// plane then bounds the aperture from below twice (<see cref="ApertureCutoff"/>): by the frames' averaged power spectrum, and
/// by the detail two half-stacks of the best frames share (the plan's T2), the halves taken alternately down the ranking so
/// both are equally sharp. The stack's halo shows or does not show a spider's spikes (<see cref="SpiderSignature"/>).
/// Deciding between telescopes from that evidence is the caller's, with its own candidates.
/// </summary>
public static class PlanetaryApertureEvidence
{
    /// <summary>
    /// Measures <paramref name="stream"/>, a capture of the planet <paramref name="aspect"/> describes, over its best
    /// <paramref name="bestFrames"/> frames. Null when the stack's disk cannot be fitted, and so has no scale.
    /// </summary>
    public static async Task<ApertureEvidence?> MeasureAsync(IPlanetaryFrameStream stream, PlanetAspect aspect, int bestFrames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bestFrames);

        var grades = await new FrameGrader(new LaplacianEnergyEstimator()).GradeAllAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (grades.IsDefaultOrEmpty)
        {
            return null;
        }
        var fraction = Math.Min(1.0, (double)bestFrames / grades.Length);
        var selected = FrameGrader.SelectBest(grades, fraction);

        var stacked = await new LuckyImagingStacker().StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = fraction }, cancellationToken).ConfigureAwait(false);
        var master = stacked.Master;
        var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
        if (PlanetaryLimbFit.Fit(master, limbOptions) is not { } limb)
        {
            return null;
        }
        var scale = aspect.AngularDiameterArcsec / 2 / limb.EquatorialRadius;

        var luminance = new float[master.Width * master.Height];
        for (var c = 0; c < master.ChannelCount; c++)
        {
            var channel = master.GetChannelSpan(c);
            for (var i = 0; i < luminance.Length; i++)
            {
                luminance[i] += channel[i] / master.ChannelCount;
            }
        }
        var spider = SpiderSignature.Measure(luminance, master.Width, master.Height, limb.CenterX, limb.CenterY, limb.EquatorialRadius, limbOptions.AxisRatio, limb.AxisAngleDeg);

        var halves = await StackHalvesAsync(stream, selected, cancellationToken).ConfigureAwait(false);
        try
        {
            var (planes, atBlack) = await MeasurePlanesAsync(stream, selected, scale, limb, spider, halves, cancellationToken).ConfigureAwait(false);
            return new ApertureEvidence(limb, scale, grades.Length, selected.Length, planes, spider, atBlack, master);
        }
        finally
        {
            halves.A.Release();
            halves.B.Release();
        }
    }

    // Two stacks of the best frames, alternately down the ranking, both aligned to the best frame, in the stream's own planes.
    private static async Task<(Image A, Image B)> StackHalvesAsync(IPlanetaryFrameStream stream, ImmutableArray<int> selected, CancellationToken cancellationToken)
    {
        var a = ImmutableArray.CreateBuilder<int>((selected.Length + 1) / 2);
        var b = ImmutableArray.CreateBuilder<int>(selected.Length / 2);
        for (var i = 0; i < selected.Length; i++)
        {
            (i % 2 == 0 ? a : b).Add(selected[i]);
        }
        var stacker = new LuckyImagingStacker();
        var halfA = await stacker.StackPlanesAsync(stream, a.ToImmutable(), selected[0], cancellationToken).ConfigureAwait(false);
        var halfB = await stacker.StackPlanesAsync(stream, b.ToImmutable(), selected[0], cancellationToken).ConfigureAwait(false);
        return (halfA, halfB);
    }

    private static async Task<(ImmutableArray<PlaneCutoff> Planes, double? AtBlack)> MeasurePlanesAsync(
        IPlanetaryFrameStream stream, ImmutableArray<int> selected, double scale, LimbFit limb, SpiderMeasurement? spider, (Image A, Image B) halves, CancellationToken cancellationToken)
    {
        // Which channels feed which plane: a split Bayer frame is [R, G1, G2, B], both greens one plane.
        (string Name, int[] Channels, double Scale)[] layout = stream.Layout switch
        {
            PlanetaryFrameLayout.SplitCfa => [("R", [0], 2 * scale), ("G", [1, 2], 2 * scale), ("B", [3], 2 * scale)],
            PlanetaryFrameLayout.Mono => [("mono", [0], scale)],
            _ => [],
        };
        // The annulus the spider was measured in, on the frames' own grid: a split Bayer plane is half the sensor's.
        var toPlane = stream.Layout == PlanetaryFrameLayout.SplitCfa ? 0.5 : 1.0;
        long annulusSamples = 0, annulusAtBlack = 0;

        var spectra = new PlanetaryPowerSpectrum[layout.Length];
        for (var p = 0; p < layout.Length; p++)
        {
            spectra[p] = new PlanetaryPowerSpectrum(stream.Width, stream.Height);
        }
        foreach (var index in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await stream.LoadAsync(index, cancellationToken).ConfigureAwait(false);
            try
            {
                for (var p = 0; p < layout.Length; p++)
                {
                    foreach (var channel in layout[p].Channels)
                    {
                        var plane = frame.GetChannelSpan(channel);
                        spectra[p].Add(plane);
                        if (spider is { } halo)
                        {
                            var (samples, atBlack) = CountAtBlack(plane, stream.Width, stream.Height, limb.CenterX * toPlane, limb.CenterY * toPlane, halo.InnerRadius * toPlane, halo.OuterRadius * toPlane);
                            annulusSamples += samples;
                            annulusAtBlack += atBlack;
                        }
                    }
                }
            }
            finally
            {
                frame.Release();
            }
        }

        var builder = ImmutableArray.CreateBuilder<PlaneCutoff>(layout.Length);
        for (var p = 0; p < layout.Length; p++)
        {
            if (ApertureCutoff.Measure(spectra[p].Rings()) is { } cutoff)
            {
                builder.Add(new PlaneCutoff(layout[p].Name, layout[p].Scale, cutoff, SharedDetail(spectra[p], layout[p].Channels, halves)));
            }
        }
        return (builder.ToImmutable(), annulusSamples > 0 ? (double)annulusAtBlack / annulusSamples : null);
    }

    // Where the detail both half-stacks hold ends, for a plane of one or more channels: a plane of several (both greens) has its
    // channels' cross-spectra averaged ring by ring, which a split Bayer frame samples at different positions.
    private static double SharedDetail(PlanetaryPowerSpectrum spectrum, int[] channels, (Image A, Image B) halves)
    {
        ImmutableArray<SpectrumRing> rings = default;
        foreach (var channel in channels)
        {
            var cross = spectrum.Cross(halves.A.GetChannelSpan(channel), halves.B.GetChannelSpan(channel));
            if (rings.IsDefault)
            {
                rings = cross;
                continue;
            }
            var merged = ImmutableArray.CreateBuilder<SpectrumRing>(rings.Length);
            for (var i = 0; i < rings.Length; i++)
            {
                var (r, c) = (rings[i], cross[i]);
                merged.Add(r with
                {
                    Power = (r.Power + c.Power) / 2,
                    StandardError = Math.Sqrt((r.StandardError * r.StandardError) + (c.StandardError * c.StandardError)) / 2,
                });
            }
            rings = merged.MoveToImmutable();
        }
        return rings.IsDefault ? 0 : ApertureCutoff.MeasureCross(rings);
    }

    // How many of the plane's samples in the annulus sit at the plane's lowest value, the black level a clipped sky rests on.
    private static (long Samples, long AtBlack) CountAtBlack(ReadOnlySpan<float> plane, int width, int height, double centerX, double centerY, double inner, double outer)
    {
        var black = float.MaxValue;
        foreach (var v in plane)
        {
            black = Math.Min(black, v);
        }
        long samples = 0, atBlack = 0;
        for (var y = 0; y < height; y++)
        {
            var dy = y - centerY;
            for (var x = 0; x < width; x++)
            {
                var dx = x - centerX;
                var r = Math.Sqrt((dx * dx) + (dy * dy));
                if (r >= inner && r <= outer)
                {
                    samples++;
                    if (plane[(y * width) + x] <= black)
                    {
                        atBlack++;
                    }
                }
            }
        }
        return (samples, atBlack);
    }
}
