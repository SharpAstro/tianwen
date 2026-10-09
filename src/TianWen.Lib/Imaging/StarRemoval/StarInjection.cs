using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// One star the injector puts into a starless plate (docs/plans/star-remover-training.md, R1).
/// </summary>
/// <param name="X">Centre in the coordinates of the planes it is rendered into, pixel centres at integers.</param>
/// <param name="Y">Centre.</param>
/// <param name="Amplitudes">Each channel's peak above the plate, in the plate's units; for a saturated star the peak its
/// wings extrapolate to, past its clip.</param>
/// <param name="Profiles">Each channel's profile.</param>
/// <param name="Saturated">Rendered as a stack saturates (<see cref="StarInjection"/>).</param>
/// <param name="ClipLevels">Each channel's clip level, the plate plus the star never passing it; positive infinity for a
/// channel the star does not clip; empty for an unsaturated star.</param>
public sealed record InjectedStar(
    double X, double Y, ImmutableArray<double> Amplitudes, ImmutableArray<StarProfile> Profiles, bool Saturated,
    ImmutableArray<double> ClipLevels);

/// <summary>What <see cref="StarInjection.Render"/> returns.</summary>
/// <param name="Planes">The plate plus the stars, noise-free, one row-major plane per channel.</param>
/// <param name="UnclippedFraction">Per channel and pixel, the fraction of a saturated star's virtual subs that did not
/// clip there (1 where nothing clipped): the share of a stack's noise a plateau keeps.</param>
public sealed record InjectionRender(float[][] Planes, float[][] UnclippedFraction);

/// <summary>
/// Renders injected stars into a plate. An unsaturated star is its profile times its amplitude, integrated over the pixel.
/// A saturated star is rendered as a stack makes one: <see cref="VirtualSubs"/> virtual subs, each with its own amplitude
/// and width (a few percent of scatter, the seeing from sub to sub) and its own sub-pixel offset (the dither and the
/// registration), each clipped where the plate plus the star passes the channel's level, and averaged. The masters' own
/// saturated stars are what this reproduces (plateaus of 1 to 4 px at the median, edges 0.5 to 1 px from 90 to 50 percent,
/// one channel clipping first on some), where a hard clip of the plate plus a star would give a sharp-edged flat top no
/// stack has.
/// </summary>
public static class StarInjection
{
    /// <summary>The virtual subs a saturated star is averaged over.</summary>
    public const int VirtualSubs = 8;

    /// <summary>The relative scatter of a virtual sub's amplitude and of its width.</summary>
    public const double SubScatter = 0.05;

    /// <summary>The scatter of a virtual sub's offset, pixels per axis.</summary>
    public const double SubOffsetPx = 0.25;

    /// <summary>The farthest a star is rendered from its centre, pixels.</summary>
    public const int MaxRadiusPx = 128;

    /// <summary>
    /// Renders <paramref name="stars"/> into a copy of <paramref name="plate"/> (row-major planes of
    /// <paramref name="width"/> by <paramref name="height"/>). Pixels in <paramref name="absent"/> are never touched. A star
    /// is rendered out to where its light falls below <paramref name="floors"/> (per channel, the plate's units), and no
    /// pixel past that radius is written. The
    /// unsaturated stars go in first, so a saturated one's clip takes an unsaturated neighbour on its plateau with it, as the
    /// sensor clips the sum (only the plate itself is never darkened); <paramref name="rng"/> draws
    /// the virtual subs only, so the render of an unsaturated field does not consume it.
    /// </summary>
    public static InjectionRender Render(
        IReadOnlyList<float[]> plate, int width, int height, BitMatrix? absent, IReadOnlyList<InjectedStar> stars,
        IReadOnlyList<double> floors, Random rng)
    {
        var channels = plate.Count;
        var planes = new float[channels][];
        var unclipped = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            planes[c] = [.. plate[c]];
            unclipped[c] = new float[width * height];
            Array.Fill(unclipped[c], 1f);
        }

        foreach (var star in stars)
        {
            if (!star.Saturated)
            {
                for (var c = 0; c < channels; c++)
                {
                    AddStar(planes[c], width, height, absent, star.X, star.Y, star.Amplitudes[c], star.Profiles[c], floors[c]);
                }
            }
        }

        foreach (var star in stars)
        {
            if (star.Saturated)
            {
                RenderSaturated(plate, planes, unclipped, width, height, absent, star, floors, rng);
            }
        }
        return new InjectionRender(planes, unclipped);
    }

    private static int RadiusFor(double amplitude, StarProfile profile, double floor)
        => !(amplitude > 0) ? 0 : (int)Math.Ceiling(Math.Min(MaxRadiusPx, profile.RadiusAtFraction(floor > 0 ? floor / amplitude : 1e-6)));

    // A star's reach is a CIRCLE of its radius: the corners of the box around it lie up to 1.4 radii out, where its light is
    // under the floor, and writing them there still moved a pixel (a saturated star's corners were drawn 41 px out of 29).
    private static bool OutOfReach(int x, int y, double cx, double cy, int r)
        => ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) > (double)r * r;

    private static void AddStar(float[] plane, int width, int height, BitMatrix? absent, double cx, double cy, double amplitude, StarProfile profile, double floor)
    {
        var r = RadiusFor(amplitude, profile, floor);
        if (r <= 0)
        {
            return;
        }
        var x0 = (int)Math.Round(cx);
        var y0 = (int)Math.Round(cy);
        for (var y = Math.Max(0, y0 - r); y <= Math.Min(height - 1, y0 + r); y++)
        {
            for (var x = Math.Max(0, x0 - r); x <= Math.Min(width - 1, x0 + r); x++)
            {
                if (OutOfReach(x, y, cx, cy, r) || (absent is { } a && a[y, x]))
                {
                    continue;
                }
                plane[y * width + x] += (float)(amplitude * profile.PixelMean(x, y, cx, cy));
            }
        }
    }

    private static void RenderSaturated(
        IReadOnlyList<float[]> plate, float[][] planes, float[][] unclipped, int width, int height, BitMatrix? absent, InjectedStar star,
        IReadOnlyList<double> floors, Random rng)
    {
        var channels = planes.Length;
        Span<double> ampScale = stackalloc double[VirtualSubs];
        Span<double> widthScale = stackalloc double[VirtualSubs];
        Span<double> offX = stackalloc double[VirtualSubs];
        Span<double> offY = stackalloc double[VirtualSubs];
        for (var k = 0; k < VirtualSubs; k++)
        {
            ampScale[k] = Math.Exp(SubScatter * Gaussian(rng));
            widthScale[k] = Math.Exp(SubScatter * Gaussian(rng));
            offX[k] = SubOffsetPx * Gaussian(rng);
            offY[k] = SubOffsetPx * Gaussian(rng);
        }

        var r = 0;
        for (var c = 0; c < channels; c++)
        {
            r = Math.Max(r, RadiusFor(star.Amplitudes[c] * 1.2, star.Profiles[c].Scaled(1.2), floors[c]) + 1);
        }
        var x0 = (int)Math.Round(star.X);
        var y0 = (int)Math.Round(star.Y);
        var subProfiles = new StarProfile[channels][];
        for (var c = 0; c < channels; c++)
        {
            subProfiles[c] = new StarProfile[VirtualSubs];
            for (var k = 0; k < VirtualSubs; k++)
            {
                subProfiles[c][k] = star.Profiles[c].Scaled(widthScale[k]);
            }
        }
        for (var y = Math.Max(0, y0 - r); y <= Math.Min(height - 1, y0 + r); y++)
        {
            for (var x = Math.Max(0, x0 - r); x <= Math.Min(width - 1, x0 + r); x++)
            {
                if (OutOfReach(x, y, star.X, star.Y, r) || (absent is { } a && a[y, x]))
                {
                    continue;
                }
                var i = y * width + x;
                for (var c = 0; c < channels; c++)
                {
                    var below = planes[c][i];
                    // The sensor clips the SUM, so an injected neighbour on the plateau clips with it (clipped against
                    // everything beneath, one stood above the clip: 3 pixels on Centaurus A's checks); only a plate already
                    // above the level (it should not be) is left as it is. A channel the star did not clip has no level
                    // (positive infinity, InjectionPopulation), so nothing clips there.
                    var clip = Math.Max(star.ClipLevels[c], plate[c][i]);
                    double sum = 0;
                    var clipped = 0;
                    for (var k = 0; k < VirtualSubs; k++)
                    {
                        var total = below + star.Amplitudes[c] * ampScale[k] * subProfiles[c][k].PixelMean(x, y, star.X + offX[k], star.Y + offY[k]);
                        if (total >= clip)
                        {
                            clipped++;
                            total = clip;
                        }
                        sum += total;
                    }
                    planes[c][i] = (float)(sum / VirtualSubs);
                    unclipped[c][i] = Math.Min(unclipped[c][i], 1f - (float)clipped / VirtualSubs);
                }
            }
        }
    }

    /// <summary>A standard normal (Box-Muller).</summary>
    internal static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
