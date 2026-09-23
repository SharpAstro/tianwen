using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A satellite trail in ONE frame must not reach a drizzled master (#93).
/// </summary>
/// <remarks>
/// <para>Both drizzle strategies ignored the rejector the pipeline handed them, on the grounds that
/// coverage weight is the natural mask. It is not: coverage says whether any frame covered a cell,
/// rejection says whether THIS frame's sample there is an outlier, and a trail has full coverage.
/// Found on the dataset gallery's Omega Cen 2024-02-16 card (ASI533MC, 91 subs drizzled), which
/// carried frame 00027's airplane through the cluster; frame 00049 holds a satellite trail.</para>
/// <para>The fixture is a flat RGGB sky with stars at fixed sky positions, random sub-pixel dither
/// (so every cell sees many phases of every star), and one frame carrying a bright diagonal line.
/// Each assertion is against the SAME set integrated with no trail at all, so what is measured is
/// the trail's residue, not anything the fixture's own noise or phase sampling puts there.</para>
/// </remarks>
[Collection("Imaging")]
public class DrizzleOutlierRejectionTests
{
    private const int FrameSize = 64;
    private const int FrameCount = 60;
    private const int Margin = 6;
    private const int CanvasSize = FrameSize + Margin * 2;
    private const float Sky = 1000f;
    private const float NoiseSigma = 8f;
    private const float TrailAmplitude = 2000f;
    private const int TrailFrame = 31;

    /// <summary>The stack's own rejector at 60 frames: sigma clip, 3 low, 5 high.</summary>
    private static IntegrationOptions WithStackRejector()
        => new(Rejector: StackingPipeline.BuildRejector(FrameCount), ApplyNormalization: false);

    private static IntegrationOptions WithoutRejector() => new(Rejector: null, ApplyNormalization: false);

    [Fact]
    public async Task WithoutARejector_TheTrailReachesTheMaster()
    {
        // Pinned so the next test is known to be removing something: this is the state the gallery
        // card was built in.
        var ct = TestContext.Current.CancellationToken;
        var clean = await RunAsync(BuildFrames(withTrail: false), WithoutRejector(), ct);
        var trailed = await RunAsync(BuildFrames(withTrail: true), WithoutRejector(), ct);

        var residue = MeanResidueOnTrail(trailed.Master, clean.Master);
        residue.ShouldBeGreaterThan(10f,
            $"the fixture's trail lifts the unrejected master by only {residue:F1} ADU on the trail, so the fix below proves nothing");
    }

    [Fact]
    public async Task WithTheStacksRejector_TheTrailDoesNotReachTheMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        var clean = await RunAsync(BuildFrames(withTrail: false), WithStackRejector(), ct);
        var trailed = await RunAsync(BuildFrames(withTrail: true), WithStackRejector(), ct);

        // One trail sample in ~60 would lift a cell by ~33 ADU unrejected (the test above). Away from
        // the stars what is left is the master's own sampling noise between two integrations that
        // differ by one frame: measured -0.04 ADU along the line.
        var residue = MeanResidueOnTrail(trailed.Master, clean.Master, awayFromStarsPx: 5f);
        MathF.Abs(residue).ShouldBeLessThan(0.5f,
            $"the rejected master still differs from the trail-free one by {residue:F2} ADU along the trail, off the stars");
        trailed.DrizzleRejectedDeposits.ShouldBeGreaterThan(0);

        // Where the line crosses the bright star at (16, 18) the slope allowance keeps some of it:
        // over a steep core a trail sample and a sample taken at an extreme phase look alike (see
        // DrizzleClip). What it keeps must stay small against the star it crosses.
        var (sx, sy) = StarPositions[0];
        var kept = ApertureFlux(trailed.Master, sx + Margin, sy + Margin) / ApertureFlux(clean.Master, sx + Margin, sy + Margin) - 1;
        kept.ShouldBeLessThan(0.01, $"the trail kept over the star it crosses adds {kept:P2} to that star's flux");
    }

    [Fact]
    public async Task TheRejectorRejectsAlmostNothingOnACleanSet_AndKeepsTheStarsFlux()
    {
        var ct = TestContext.Current.CancellationToken;
        var frames = BuildFrames(withTrail: false);
        var plain = await RunAsync(frames, WithoutRejector(), ct);
        var clipped = await RunAsync(frames, WithStackRejector(), ct);

        // Gaussian sky noise at 3 low / 5 high rejects about 0.13% below and nothing above. The total
        // is asserted first: with no clip it is zero, the fraction is NaN, and .NET orders NaN below
        // every number, so the bound alone passed with the clip switched off.
        clipped.DrizzleTotalDeposits.ShouldBeGreaterThan(0, "the clip judged no deposits at all");
        var fraction = (double)clipped.DrizzleRejectedDeposits / clipped.DrizzleTotalDeposits;
        fraction.ShouldBeLessThan(0.005, $"rejected {fraction:P3} of a clean set's deposits");

        foreach (var (x, y) in StarPositions)
        {
            var plainFlux = ApertureFlux(plain.Master, x + Margin, y + Margin);
            var clippedFlux = ApertureFlux(clipped.Master, x + Margin, y + Margin);
            (clippedFlux / plainFlux).ShouldBeInRange(0.99, 1.01,
                $"star at ({x}, {y}): clipped flux {clippedFlux:F0} against unclipped {plainFlux:F0}; a star core must not be clipped");
        }
    }

    private static readonly (int X, int Y)[] StarPositions = [(16, 18), (44, 22), (28, 44), (50, 50)];

    /// <summary>
    /// Faint, SHARP stars, the case the bright ones above cannot see: about four sky sigma per frame at
    /// the peak, as the fainter stars of the Omega Cen master are, and a sigma of 0.9 px, so a
    /// photosite's value depends on where inside it the star fell, which a drizzle deposits as it is
    /// rather than interpolating. On a grid clear of the trail and of the bright stars.
    /// </summary>
    private static readonly (int X, int Y)[] FaintStarPositions =
    [
        (8, 8), (15, 8), (22, 8), (29, 8), (36, 8), (43, 8), (50, 8), (57, 8), (29, 15), (36, 15), (50, 15),
        (57, 15), (8, 22), (36, 22), (57, 22), (8, 29), (15, 29), (50, 29), (57, 29), (8, 36), (15, 36),
        (22, 36), (29, 36), (57, 36), (8, 43), (15, 43), (36, 43), (43, 43), (8, 50), (15, 50), (22, 50),
        (36, 50), (8, 57), (15, 57), (22, 57), (29, 57), (36, 57), (43, 57), (57, 57),
    ];

    private const float FaintStarAmplitude = 30f;

    private const float FaintStarSigma = 0.9f;

    [Fact]
    public async Task OnANoiseFreeStarField_TheClipTakesNoStarsFlux()
    {
        // With no noise and no transient there is nothing to clip, so whatever the clip takes from a
        // star here is the defect itself: a deposit that looks extreme only because of WHERE inside
        // its photosite the star fell that frame. Found on the real master first (Omega Cen
        // 2024-02-16, 91 subs), where the clip without the slope allowance took a median 0.75% of
        // every faint star's flux and none of a bright one's. Measured here, by slope scale: 9111
        // deposits rejected and 0.60% of the worst star's flux at 0; 1076 and 0.077% at 1; 307 and
        // 0.014% at 2, the default. Noise-free is also what makes it deterministic: the same fixture
        // WITH noise moves a 4-sigma star's flux by more than this through the sky clip alone.
        var ct = TestContext.Current.CancellationToken;
        var frames = BuildFrames(withTrail: false, noiseSigma: 0f);
        var plain = await RunAsync(frames, WithoutRejector(), ct);
        var clipped = await RunAsync(frames, WithStackRejector(), ct);

        clipped.DrizzleTotalDeposits.ShouldBeGreaterThan(0, "the clip judged no deposits at all");
        var worst = 0.0;
        foreach (var (x, y) in FaintStarPositions.Concat(StarPositions))
        {
            var change = ApertureFlux(clipped.Master, x + Margin, y + Margin) / ApertureFlux(plain.Master, x + Margin, y + Margin) - 1;
            worst = Math.Max(worst, Math.Abs(change));
        }

        worst.ShouldBeLessThan(0.0005, $"the clip changed a star's flux by {worst:P3} on a field with nothing to clip");
    }

    private static async Task<IntegrationResult> RunAsync(List<RawBayerFrame> frames, IntegrationOptions options, CancellationToken ct)
        => await new DrizzleStrategy().RunAsync(BuildJob(frames, options), ct);

    /// <summary>
    /// ADU per master unit. An unnormalised drizzle divides every sample by the frame's full scale, so
    /// the master is in [0, 1] units; the fixture's sky is <see cref="Sky"/> ADU, so the clean master's
    /// own sky level gives the factor back. Without it a 33 ADU trail reads as 0.0005 and every
    /// threshold below would be meaningless (the first version of this test was, and passed).
    /// </summary>
    private static double AduPerUnit(Image clean)
    {
        var values = new List<float>();
        var plane = clean.GetChannelArray(1);
        for (var y = Margin + 4; y < Margin + FrameSize - 4; y++)
        {
            for (var x = Margin + 4; x < Margin + FrameSize - 4; x++)
            {
                if (float.IsFinite(plane[y, x])) values.Add(plane[y, x]);
            }
        }

        values.Sort();
        return Sky / values[values.Count / 2];
    }

    /// <summary>Mean of trailed minus clean, in ADU, over the canvas cells the trail frame's line lands
    /// on, every channel, far enough inside the frame that the line is fully covered.</summary>
    private static float MeanResidueOnTrail(Image trailed, Image clean, float awayFromStarsPx = 0f)
    {
        var aduPerUnit = AduPerUnit(clean);
        double sum = 0;
        var n = 0;
        for (var c = 0; c < 3; c++)
        {
            var t = trailed.GetChannelArray(c);
            var k = clean.GetChannelArray(c);
            for (var xs = 8; xs < FrameSize - 8; xs++)
            {
                var ys = TrailY(xs);
                // The trail frame's own dither, so the line is read where that frame put it.
                var (dx, dy) = Dither(TrailFrame);
                if (awayFromStarsPx > 0f && NearAStar(xs + dx, ys + dy, awayFromStarsPx))
                {
                    continue;
                }

                var xc = (int)MathF.Round(xs + dx + Margin);
                var yc = (int)MathF.Round(ys + dy + Margin);
                var v = t[yc, xc] - k[yc, xc];
                if (float.IsFinite(v))
                {
                    sum += v;
                    n++;
                }
            }
        }

        return (float)(sum / n * aduPerUnit);
    }

    /// <summary>Whether a SKY position lies within <paramref name="radius"/> of any fixture star.</summary>
    private static bool NearAStar(float x, float y, float radius)
    {
        foreach (var (sx, sy) in StarPositions)
        {
            if (MathF.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)) < radius) return true;
        }

        foreach (var (sx, sy) in FaintStarPositions)
        {
            if (MathF.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy)) < radius) return true;
        }

        return false;
    }

    /// <summary>Flux in a 7x7 box, in master units, above the median of the 9x9 box's border in the
    /// same master and channel. A LOCAL background, because a clip also moves the sky a little (it
    /// cuts three sigma low and five high), and against one global sky level that shift, summed over
    /// the box, is larger than what a 4-sigma star loses.</summary>
    private static double ApertureFlux(Image master, int cx, int cy)
    {
        double flux = 0;
        var border = new List<float>(32);
        for (var c = 0; c < 3; c++)
        {
            var plane = master.GetChannelArray(c);
            border.Clear();
            for (var i = -4; i <= 4; i++)
            {
                border.Add(plane[cy - 4, cx + i]);
                border.Add(plane[cy + 4, cx + i]);
                if (i > -4 && i < 4)
                {
                    border.Add(plane[cy + i, cx - 4]);
                    border.Add(plane[cy + i, cx + 4]);
                }
            }

            border.Sort();
            var background = border[border.Count / 2];
            for (var y = cy - 3; y <= cy + 3; y++)
            {
                for (var x = cx - 3; x <= cx + 3; x++)
                {
                    flux += plane[y, x] - background;
                }
            }
        }

        return flux;
    }

    /// <summary>The trail: a straight line across the frame, in SOURCE coordinates.</summary>
    private static float TrailY(float xs) => 10f + xs * 0.6f;

    /// <summary>Deterministic sub-pixel dither per frame, within the canvas margin.</summary>
    private static (float Dx, float Dy) Dither(int frame)
    {
        var rng = new Random(1000 + frame);
        return ((float)(rng.NextDouble() * 6 - 3), (float)(rng.NextDouble() * 6 - 3));
    }

    /// <summary>Stars straddling the tile strategy's first strip boundary (canvas row
    /// <see cref="TilePipelinedDrizzleStrategy.StripHeight"/>), in the tall frames' source coordinates,
    /// so the cells either side of it have a slope worth reading across the boundary.</summary>
    private static readonly (int X, int Y)[] BoundaryStarPositions =
        [(12, TilePipelinedDrizzleStrategy.StripHeight - Margin - 1), (32, TilePipelinedDrizzleStrategy.StripHeight - Margin), (52, TilePipelinedDrizzleStrategy.StripHeight - Margin + 1)];

    [Fact]
    public async Task TheTiledDrizzleClipsExactlyAsTheFullCanvasOneDoes_AcrossAStripBoundary()
    {
        // The tile strategy keeps each strip's moments to itself, so the slope at a strip's edge rows
        // needs the rows beyond it: a halo row either side. Without it the two strategies read
        // different neighbours at the boundary, judge the same deposit differently, and stack one
        // session to two masters depending on which one the host's memory budget picked.
        var ct = TestContext.Current.CancellationToken;
        const int tallHeight = TilePipelinedDrizzleStrategy.StripHeight + 40;
        const int count = 30;
        var frames = BuildFrames(withTrail: true, height: tallHeight, count: count, extraStars: BoundaryStarPositions);
        var options = new IntegrationOptions(Rejector: StackingPipeline.BuildRejector(count), ApplyNormalization: false);

        var dir = Directory.CreateTempSubdirectory("DrizzleOutlierRejectionTests_");
        try
        {
            var sources = new List<RawLightSource>(count);
            for (var f = 0; f < count; f++)
            {
                var path = Path.Combine(dir.FullName, $"bayer{f}.fits");
                frames[f].RawCfa.WriteToFitsFile(path);
                sources.Add(new RawLightSource(path, frames[f].TransformToCanvas));
            }

            var full = await new DrizzleStrategy(minFrameCount: 1).RunAsync(
                BuildJob(frames, options, CanvasSize, tallHeight + Margin * 2), ct);
            var tiled = await new TilePipelinedDrizzleStrategy(minFrameCount: 1).RunAsync(
                BuildJob(frames, options, CanvasSize, tallHeight + Margin * 2) with { RawBayerFrames = null, RawLightSources = sources, Calibrator = new Calibrator() }, ct);

            tiled.DrizzleTotalDeposits.ShouldBe(full.DrizzleTotalDeposits, "both strategies judge every deposit");
            tiled.DrizzleRejectedDeposits.ShouldBe(full.DrizzleRejectedDeposits, "and reject the same ones");
            full.DrizzleRejectedDeposits.ShouldBeGreaterThan(0, "the fixture's trail was clipped, so the comparison saw a clip");
            for (var c = 0; c < 3; c++)
            {
                var a = full.Master.GetChannelArray(c);
                var b = tiled.Master.GetChannelArray(c);
                for (var y = 0; y < a.GetLength(0); y++)
                {
                    for (var x = 0; x < a.GetLength(1); x++)
                    {
                        if (float.IsNaN(a[y, x]) && float.IsNaN(b[y, x]))
                        {
                            continue;
                        }

                        b[y, x].ShouldBe(a[y, x], 1e-6f, $"channel {c} at ({x}, {y})");
                    }
                }
            }
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static List<RawBayerFrame> BuildFrames(
        bool withTrail, int height = FrameSize, int count = FrameCount, (int X, int Y)[]? extraStars = null, float noiseSigma = NoiseSigma)
    {
        var frames = new List<RawBayerFrame>(count);
        var bright = extraStars is null ? StarPositions : [.. StarPositions, .. extraStars];
        for (var f = 0; f < count; f++)
        {
            var rng = new Random(7 + f);
            var (dx, dy) = Dither(f);
            var plane = new float[height, FrameSize];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < FrameSize; x++)
                {
                    // Stars sit at fixed SKY positions, so in this frame's source coordinates they are
                    // offset by minus its dither (the transform below maps source to canvas by +dither).
                    var v = Sky + NextGaussian(rng) * noiseSigma;
                    foreach (var (sx, sy) in bright)
                    {
                        var rx = x + dx - sx;
                        var ry = y + dy - sy;
                        v += 3000f * MathF.Exp(-(rx * rx + ry * ry) / (2f * 1.2f * 1.2f));
                    }

                    foreach (var (sx, sy) in FaintStarPositions)
                    {
                        var rx = x + dx - sx;
                        var ry = y + dy - sy;
                        v += FaintStarAmplitude * MathF.Exp(-(rx * rx + ry * ry) / (2f * FaintStarSigma * FaintStarSigma));
                    }

                    if (withTrail && f == TrailFrame && MathF.Abs(y - TrailY(x)) < 0.8f)
                    {
                        v += TrailAmplitude;
                    }

                    plane[y, x] = v;
                }
            }

            var meta = new ImageMeta { Instrument = "synth-drizzle-trail", SensorType = SensorType.RGGB };
            var img = new Image([plane], BitDepth.Float32, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta: meta);
            frames.Add(new RawBayerFrame(img, Matrix3x2.CreateTranslation(dx + Margin, dy + Margin)));
        }

        return frames;
    }

    private static IntegrationJob BuildJob(List<RawBayerFrame> frames, IntegrationOptions options,
        int canvasWidth = CanvasSize, int canvasHeight = CanvasSize)
    {
        // Re-enumerable, as the pipeline's producers are: a rejecting drizzle streams the frames twice.
        async IAsyncEnumerable<RawBayerFrame> RawBayerFramesProducer([EnumeratorCancellation] CancellationToken token)
        {
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        static async IAsyncEnumerable<Image> EmptyWarpedFrames([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }

        return new IntegrationJob(
            WarpedFrames: EmptyWarpedFrames,
            ExpectedFrameCount: frames.Count,
            Options: options,
            StagingDir: Path.GetTempPath(),
            StatsRect: PixelRect.Empty,
            RawBayerFrames: RawBayerFramesProducer,
            DrizzleOptions: new DrizzleOptions(),
            CanvasWidth: canvasWidth,
            CanvasHeight: canvasHeight);
    }

    private static float NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
    }
}
