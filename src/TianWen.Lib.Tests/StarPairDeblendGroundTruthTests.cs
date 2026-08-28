using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// At what separation do two stars stop being two stars?
/// </summary>
/// <remarks>
/// <para>Synthetic and planted, because this is the one question the real fixture cannot answer:
/// on a real frame there is no ground truth for "there are two stars here", so a merge is invisible
/// to every count and every pin. Here the pair positions are numbers chosen before the pixels
/// exist.</para>
/// <para><b>This is NOT the case TODO.md warns about.</b> That warning is that
/// <c>image_file-snr-20_stars-28</c> cannot exercise deblending -- its closest pair is 11.8 px, so it
/// is byte-identical across every radius change. The answer to a fixture with no close pairs is a
/// fixture WITH close pairs, not the real frame, which has no ground truth at all. Both are needed
/// and they answer different questions: this one measures what is recovered, the real frame measures
/// what is invented.</para>
/// <para>The field is deliberately sparse -- one pair per 50 px cell -- so a mis-attributed detection
/// cannot come from a third star. Noise is seeded, so a run is reproducible; the amplitude is well
/// above the SNR floor, so nothing here is a faint-star detection question.</para>
/// </remarks>
[Collection("Imaging")]
public class StarPairDeblendGroundTruthTests(ITestOutputHelper output)
{
    private const int Size = 400;
    private const int Cell = 50;
    private const float Background = 1000f;
    private const float NoiseSigma = 15f;
    /// <summary>
    /// PSF sigma, i.e. FWHM 2.59 px. Matched to the real fixture, whose median HFD is 2.40 px -- not
    /// for realism as such, but because a star's ABOVE-THRESHOLD REACH is what decides whether the
    /// existing box-shrink loop boxes one star or two, so a fatter or brighter synthetic star measures
    /// a detector that is not the one shipping. The premise is checked, not assumed: see
    /// <see cref="TheWidestPlantedPairsAreAlreadyResolved"/>.
    /// </summary>
    private const float Sigma = 1.1f;
    private const float PrimaryAmplitude = 3000f;

    private static ImageMeta Meta() => new ImageMeta(
        "synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    private readonly record struct PlantedPair(float Ax, float Ay, float Bx, float By, float Separation, float Ratio);

    /// <summary>
    /// One pair per cell, walking separation x flux-ratio x position angle so no single combination
    /// can carry the result. The half-pixel offsets keep every planted position off the pixel grid.
    /// </summary>
    private static List<PlantedPair> Plant(float[] separations, float[] ratios)
    {
        var pairs = new List<PlantedPair>();
        var perRow = Size / Cell;
        var slot = 0;
        foreach (var sep in separations)
        {
            foreach (var ratio in ratios)
            {
                // Position angle walks in steps that are not a multiple of 45 degrees, so no pair is
                // axis-aligned or diagonal-aligned -- a deblender that only splits along a row would
                // otherwise pass.
                var angle = (slot * 37f) * MathF.PI / 180f;
                var cx = Cell * (slot % perRow) + Cell * 0.5f + 0.37f;
                var cy = Cell * (slot / perRow) + Cell * 0.5f + 0.62f;
                var hx = 0.5f * sep * MathF.Cos(angle);
                var hy = 0.5f * sep * MathF.Sin(angle);
                pairs.Add(new PlantedPair(cx - hx, cy - hy, cx + hx, cy + hy, sep, ratio));
                slot++;
            }
        }
        return pairs;
    }

    private static Image Render(List<PlantedPair> pairs)
    {
        var data = new float[Size, Size];
        var twoSigmaSq = 2f * Sigma * Sigma;
        // Seeded so the run is reproducible; Box-Muller over a seeded Random is deterministic.
        var rng = new Random(42);

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var v = Background;
                foreach (var p in pairs)
                {
                    var dax = x - p.Ax;
                    var day = y - p.Ay;
                    v += PrimaryAmplitude * MathF.Exp(-(dax * dax + day * day) / twoSigmaSq);
                    var dbx = x - p.Bx;
                    var dby = y - p.By;
                    v += PrimaryAmplitude * p.Ratio * MathF.Exp(-(dbx * dbx + dby * dby) / twoSigmaSq);
                }

                var u1 = 1.0 - rng.NextDouble();
                var u2 = rng.NextDouble();
                v += NoiseSigma * (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
                data[y, x] = v;
            }
        }

        var max = Background + PrimaryAmplitude;
        return new Image([data], BitDepth.Float32, max, Background, 0f, Meta());
    }

    /// <summary>
    /// A planted component counts as RECOVERED when a detection lands within <c>0.5 * separation</c>
    /// of it and is closer to it than to its companion, so a single merged detection at the midpoint
    /// can never be credited to either -- which is exactly the failure being measured.
    /// </summary>
    private static (int Recovered, float ErrA, float ErrB) Score(PlantedPair p, ImagedStar[] stars)
    {
        var tol = MathF.Max(0.5f * p.Separation, 0.75f);
        var errA = Nearest(p.Ax, p.Ay, p.Bx, p.By, stars, tol);
        var errB = Nearest(p.Bx, p.By, p.Ax, p.Ay, stars, tol);
        return ((errA >= 0 ? 1 : 0) + (errB >= 0 ? 1 : 0), errA, errB);
    }

    private static float Nearest(float tx, float ty, float ox, float oy, ImagedStar[] stars, float tol)
    {
        var best = -1f;
        foreach (var s in stars)
        {
            var d = MathF.Sqrt((s.XCentroid - tx) * (s.XCentroid - tx) + (s.YCentroid - ty) * (s.YCentroid - ty));
            var dOther = MathF.Sqrt((s.XCentroid - ox) * (s.XCentroid - ox) + (s.YCentroid - oy) * (s.YCentroid - oy));
            if (d <= tol && d < dOther && (best < 0 || d < best))
            {
                best = d;
            }
        }
        return best;
    }

    /// <summary>
    /// The fixture's own premise: at a separation the SHIPPING detector already resolves, this field
    /// must show two components. Without it a recovery curve that reads zero everywhere is
    /// indistinguishable from a fixture whose stars are simply undetectable, and the curve would stay
    /// green forever while measuring nothing.
    /// </summary>
    /// <remarks>
    /// 12 px is comfortably past the 5.10 px closest ACCEPTED pair on
    /// <c>RGGB_frame_bx0_by0_top_down</c>, so nothing about deblending is being asserted here.
    /// </remarks>
    [Fact]
    public async Task TheWidestPlantedPairsAreAlreadyResolved()
    {
        var pairs = Plant([12f], [1.0f, 0.5f, 0.25f]);
        var image = Render(pairs);
        var stars = (await image.FindStarsAsync(
            0, snrMin: 10f, maxStars: 500, cancellationToken: TestContext.Current.CancellationToken)).ToArray();

        foreach (var p in pairs)
        {
            var (n, ea, eb) = Score(p, stars);
            output.WriteLine($"  sep {p.Separation:F1} ratio {p.Ratio:F2}: recovered {n} (errA {ea:F3}, errB {eb:F3})");
            n.ShouldBe(2, $"a pair {p.Separation:F1} px apart at flux ratio {p.Ratio:F2} is already two stars to this detector");
        }
    }

    [Fact]
    public async Task ReportTheRecoveryCurve()
    {
        float[] separations = [2.0f, 2.5f, 3.0f, 3.5f, 4.0f, 5.0f, 6.0f, 8.0f];
        float[] ratios = [1.0f, 0.5f, 0.25f];
        var pairs = Plant(separations, ratios);
        var image = Render(pairs);
        var stars = (await image.FindStarsAsync(
            0, snrMin: 10f, maxStars: 500, cancellationToken: TestContext.Current.CancellationToken)).ToArray();

        output.WriteLine($"{pairs.Count} planted pairs, {stars.Length} detections");
        output.WriteLine("  sep  ratio  recovered  errA    errB    (recovered = planted components matched, max 2)");
        foreach (var p in pairs)
        {
            var (n, ea, eb) = Score(p, stars);
            output.WriteLine($"  {p.Separation,4:F1}  {p.Ratio,5:F2}      {n}     " +
                             $"{(ea >= 0 ? ea.ToString("F3") : "  -  "),6}  {(eb >= 0 ? eb.ToString("F3") : "  -  "),6}");
        }

        foreach (var sep in separations)
        {
            var group = pairs.Where(p => p.Separation == sep).Select(p => Score(p, stars).Recovered).ToArray();
            output.WriteLine($"  separation {sep:F1}px: {group.Sum()} of {2 * group.Length} components recovered");
        }
    }

    /// <summary>
    /// Every planted pair from 4 px apart is reported as two stars, wherever the flux ratio sits.
    /// </summary>
    /// <remarks>
    /// <para>4 px is where the shipping detector recovered 3 of 6 components and 6 px is where it
    /// recovered NONE -- the merged blob was refused outright, because the azimuthal profile around a
    /// midpoint that no star occupies never rises above half of the brighter core's peak, and
    /// <c>FWHM == 0</c> is a refusal. So this bound fails on the pre-deblend detector at every
    /// separation it names, which is the property that makes it worth having.</para>
    /// <para>It deliberately stops at 4 px. Two Gaussians of width sigma have TWO MAXIMA only beyond
    /// d = 2*sigma, so below about 2.6 px on this field there is no second peak for any peak-based
    /// method to find, and asserting recovery there would be asserting something no threshold can
    /// deliver. <see cref="ReportTheRecoveryCurve"/> reports that band rather than pinning it.</para>
    /// </remarks>
    [Fact]
    public async Task APairFourPixelsApartIsTwoStars()
    {
        float[] separations = [4.0f, 5.0f, 6.0f, 8.0f];
        var pairs = Plant(separations, [1.0f, 0.5f, 0.25f]);
        var image = Render(pairs);
        var stars = (await image.FindStarsAsync(
            0, snrMin: 10f, maxStars: 500, cancellationToken: TestContext.Current.CancellationToken)).ToArray();

        foreach (var p in pairs)
        {
            var (n, ea, eb) = Score(p, stars);
            output.WriteLine($"  sep {p.Separation:F1} ratio {p.Ratio:F2}: recovered {n} (errA {ea:F3}, errB {eb:F3})");
            n.ShouldBe(2, $"a pair {p.Separation:F1} px apart at flux ratio {p.Ratio:F2} is two stars");
        }
    }

    /// <summary>
    /// A deblended component lands on its star, not merely somewhere between the two.
    /// </summary>
    /// <remarks>
    /// A recovery count alone is satisfied by two detections anywhere inside a generous tolerance, and
    /// the tolerance has to be generous (it scales with the separation) or a merged detection at the
    /// centre of mass could not be excluded. So the position is asserted separately and tightly: the
    /// merged detection this replaces sits <c>separation/2</c> to <c>separation/10</c> from a
    /// component, i.e. 0.4 to 2.0 px on this field, so a 0.25 px bound cannot be met by anything that
    /// has not actually resolved the pair.
    /// </remarks>
    [Fact]
    public async Task ADeblendedComponentLandsOnItsStar()
    {
        var pairs = Plant([4.0f, 5.0f, 6.0f], [1.0f, 0.5f]);
        var image = Render(pairs);
        var stars = (await image.FindStarsAsync(
            0, snrMin: 10f, maxStars: 500, cancellationToken: TestContext.Current.CancellationToken)).ToArray();

        var worst = 0f;
        foreach (var p in pairs)
        {
            var (n, ea, eb) = Score(p, stars);
            n.ShouldBe(2);
            worst = MathF.Max(worst, MathF.Max(ea, eb));
        }

        output.WriteLine($"  worst component position error over {pairs.Count} pairs: {worst:F3} px");
        worst.ShouldBeLessThan(0.25f);
    }
}
