using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A frame taken while the telescope MOVED, and the edges no frame reached (#1300). A telescope that moves during a frame (a bump, a
/// nudge, a slew) draws the planet out along a line: its long sharp edge is the sharpest thing the gradient sees, so on the owner's
/// 2021-08-01 Saturn one such frame was every stack's reference, and the frames
/// registered against it left the master's left and top edges reached by none. A smeared frame is left out as a cut one is, and the
/// master is cropped to where the frames reached, never into its planet.
/// </summary>
public class SmearedFrameTests
{
    private const int Width = 96;
    private const int Height = 72;

    // A soft-edged disk centred at (cx, cy), 0.4 over a sky of 0.03 with a little noise, drawn out over `smear` pixels along `angleDeg`
    // as a telescope moving during the exposure draws it.
    private static float[,] Frame(Random random, double cx, double cy, double radius = 10, double smear = 0, double angleDeg = 30, double noise = 0.004)
    {
        var (dx, dy) = (Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180));
        var steps = Math.Max(1, (int)Math.Ceiling(smear * 2));
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var inside = 0.0;
                for (var s = 0; s < steps; s++)
                {
                    var t = steps == 1 ? 0 : (-smear / 2) + (smear * s / (steps - 1));
                    var r = Math.Sqrt(((x - cx - (t * dx)) * (x - cx - (t * dx))) + ((y - cy - (t * dy)) * (y - cy - (t * dy))));
                    inside += Math.Clamp((radius - r + 1.5) / 3, 0, 1);
                }
                frame[y, x] = (float)Math.Clamp(0.03 + (0.4 * inside / steps) + (noise * PhaseScreen.Gaussian(random)), 0, 1);
            }
        }
        return frame;
    }

    [Fact]
    public void AMovingTelescopesPlanetLiesLongerThanTheRunsAndAStillOnesDoesNot()
    {
        var random = new Random(1);
        var still = PlanetaryDisk.Elongation(Image.FromChannel(Frame(random, 47.3, 36.6), 1f, 0f));
        var smeared = PlanetaryDisk.Elongation(Image.FromChannel(Frame(random, 47.3, 36.6, smear: 30), 1f, 0f));

        still.ShouldBeInRange(1f, 1.1f, "a round planet lies as long as it is wide");
        FrameGrader.IsSmeared(smeared, still).ShouldBeTrue($"drawn out over three of its radii it lies {smeared:0.00} times as long");
        FrameGrader.IsSmeared(still, still).ShouldBeFalse();
        FrameGrader.IsSmeared(smeared, double.NaN).ShouldBeFalse("a run whose shape is unread leaves out nothing");
    }

    [Fact]
    public void ADiskFillingMuchOfTheFrameIsReadAtItsOwnBrightness()
    {
        // A planet over two fifths of the frame lifts its spread so far that three deviations above the mean lie above the planet itself
        // (the 678MC and 12-inch SCT Jupiters held no pixel there): its elongation, and whether it is whole (#1307), are read from the
        // planet's own brightness instead.
        var frame = Image.FromChannel(Frame(new Random(2), 47.3, 35.6, radius: 30), 1f, 0f);
        var (_, cutOrEmpty, elongation, _, _) = PlanetaryDisk.BoundingBoxAndCut(frame);

        cutOrEmpty.ShouldBeFalse("a whole planet");
        elongation.ShouldBeInRange(1f, 1.1f);
    }

    [Fact]
    public async Task ACaptureLeavesItsSmearedFramesOutAndATrackedOneNone()
    {
        var random = new Random(3);
        // A run of still frames wandering a pixel or two, and two taken as the telescope moved: the sharpest-looking of all by the gradient.
        var still = Enumerable.Range(0, 12).Select(i => Frame(random, 47.3 + (i % 3), 36.6 - (i % 2))).ToArray();
        var grader = new FrameGrader(new GradientEnergyEstimator());
        var run = await grader.GradeAllAsync(new InMemoryFrameStream([.. still, Frame(random, 47.3, 36.6, smear: 30), Frame(random, 46, 37, smear: 24, angleDeg: 40)]),
            cancellationToken: TestContext.Current.CancellationToken);

        run.Take(12).ShouldAllBe(g => !g.Smeared && g.Score > 0, "the still frames");
        run.Skip(12).ShouldAllBe(g => g.Smeared && g.Score == 0, "the frames the telescope's motion drew out");
        run.Skip(12).ShouldAllBe(g => g.Elongation > FrameGrader.SmearRatio * FrameGrader.RunElongation(run));
        FrameGrader.Reference(run).ShouldBeLessThan(12, "a smeared frame is never the reference");

        var tracked = await grader.GradeAllAsync(new InMemoryFrameStream(still), cancellationToken: TestContext.Current.CancellationToken);
        tracked.ShouldAllBe(g => !g.Smeared && g.Score > 0, "a tracked capture leaves out none");
    }

    [Fact]
    public async Task ACaptureLeavesItsDimFramesOutAndATrackedOneNone()
    {
        // #1307: cloud, a bump or defocus lowers a planet's peak, and the grader divides its score by the frame's brightness squared, so a
        // dim frame's noise reads as detail: the blurred last frame of the owner's 2021-08-19 21:54:54 Saturn, at a third of the run's
        // brightness, was every stack's reference once its planet was found at all.
        var random = new Random(4);
        var still = Enumerable.Range(0, 12).Select(i => Frame(random, 47.3 + (i % 3), 36.6 - (i % 2))).ToArray();
        var grader = new FrameGrader(new GradientEnergyEstimator());
        var run = await grader.GradeAllAsync(new InMemoryFrameStream([.. still, Dimmed(Frame(random, 47.3, 36.6, radius: 16), 0.3f)]),
            cancellationToken: TestContext.Current.CancellationToken);

        run.Take(12).ShouldAllBe(g => !g.Dim && g.Score > 0, "the frames at the run's brightness");
        run[12].Cut.ShouldBeFalse("its planet is whole");
        run[12].Dim.ShouldBeTrue("its planet peaks at a third of the run's");
        run[12].Score.ShouldBe(0f);
        FrameGrader.Reference(run).ShouldBeLessThan(12, "a dim frame is never the reference");

        var tracked = await grader.GradeAllAsync(new InMemoryFrameStream(still), cancellationToken: TestContext.Current.CancellationToken);
        tracked.ShouldAllBe(g => !g.Dim && g.Score > 0, "a steady capture leaves out none");

        // A frame dimmed `factor` times over the sky of 0.03, as cloud passing leaves it.
        static float[,] Dimmed(float[,] frame, float factor)
        {
            for (var y = 0; y < frame.GetLength(0); y++)
            {
                for (var x = 0; x < frame.GetLength(1); x++)
                {
                    frame[y, x] = 0.03f + ((frame[y, x] - 0.03f) * factor);
                }
            }
            return frame;
        }
    }

    // A master's weight: the frames reached every pixel but the left `none` columns, which none reached, and the top `half` rows, which
    // half of them did.
    private static float[,] Weight(int none, int half)
    {
        var weight = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                weight[y, x] = x < none ? 0f : y < half ? 5f : 10f;
            }
        }
        return weight;
    }

    [Fact]
    public void AMasterIsCroppedToWhereTheFramesReachedPixelForPixel()
    {
        var master = Image.FromChannel(Frame(new Random(4), 50, 40, noise: 0.0005), 1f, 0f);
        var covered = PlanetaryMaster.CoveredRectangle(Weight(none: 6, half: 4));
        covered.ShouldBe(PixelRect.FromLTRB(6, 4, Width, Height), "the band no frame reached and the rows half of them did are left");

        var (cropped, kept) = PlanetaryMaster.CropToCovered(master, covered, 1);

        kept.ShouldBe(covered);
        (cropped.Width, cropped.Height).ShouldBe((covered.Width, covered.Height));
        for (var y = 0; y < cropped.Height; y++)
        {
            for (var x = 0; x < cropped.Width; x++)
            {
                cropped[0, y, x].ShouldBe(master[0, y + kept.Y, x + kept.X], "the crop moves no pixel");
            }
        }
    }

    [Fact]
    public void ACropNeverCutsThePlanetAndKeepsAFullyCoveredMasterWhole()
    {
        // The planet lies over the band no frame reached: the crop gives way to it rather than cut it, and still takes what lies beyond it.
        // A master, so its noise is a stack's.
        var nearTheEdge = Image.FromChannel(Frame(new Random(5), 30, 36, noise: 0.0005), 1f, 0f);
        var covered = PlanetaryMaster.CoveredRectangle(Weight(none: 26, half: 0));
        var (_, kept) = PlanetaryMaster.CropToCovered(nearTheEdge, covered, 1);
        var light = PlanetaryDisk.Footprint(nearTheEdge);
        (light.Width < Width).ShouldBeTrue($"the planet's light {light} is told from the sky");
        kept.Contains(light).ShouldBeTrue($"the planet's light {light} lies inside what is kept, {kept}");
        kept.Left.ShouldBeGreaterThan(0, "the band beyond the planet's light is still cropped");
        kept.Left.ShouldBeLessThan(26, "the crop gave way to the planet");

        // Every pixel reached by every frame: the master as it is. And light that cannot be told from the sky keeps the whole frame.
        var noisy = Image.FromChannel(Frame(new Random(7), 48, 36, noise: 0.004), 1f, 0f);
        PlanetaryMaster.CropToCovered(noisy, PlanetaryMaster.CoveredRectangle(Weight(none: 20, half: 0)), 1).Kept.IsEmpty
            .ShouldBe(PlanetaryDisk.Footprint(noisy) == new PixelRect(0, 0, Width, Height), "a footprint as wide as the frame keeps it whole");
        var whole = Image.FromChannel(Frame(new Random(6), 48, 36), 1f, 0f);
        var full = PlanetaryMaster.CoveredRectangle(Weight(none: 0, half: 0));
        var (same, none) = PlanetaryMaster.CropToCovered(whole, full, 1);
        none.IsEmpty.ShouldBeTrue();
        same.ShouldBeSameAs(whole);
    }

    [Fact]
    public void CoverageIsTheFramesFootprintsWhateverWeightEachPixelFolded()
    {
        // Two frames of the grid's size, one as the reference lies and one moved by (2, 1): each output pixel is reached by the frames whose
        // sample at (x + dx, y + dy) lies inside them, weighted by the frame's own weight, never by how sharp it was there.
        var tally = new PlanetaryCoverage(10, 8);
        tally.Add(0, 0, 10, 8, 1f);
        tally.Add(2, 1, 10, 8, 3f);
        var plane = tally.Plane();
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var expected = 1f + (x + 2 < 10 && y + 1 < 8 ? 3f : 0f);
                plane[y, x].ShouldBe(expected, $"({x}, {y})");
            }
        }

        // A drizzle canvas: the frame's footprint given in canvas pixels, half-open, clamped to the grid.
        var canvas = new PlanetaryCoverage(6, 6);
        canvas.Add(left: -1.5, top: 0.5, right: 3.2, bottom: 9, 1f);
        canvas.Plane()[0, 0].ShouldBe(0f, "row 0 lies above the footprint's top at 0.5");
        canvas.Plane()[1, 3].ShouldBe(1f);
        canvas.Plane()[1, 4].ShouldBe(0f, "column 4 lies past the right edge at 3.2");
    }

    [Fact]
    public void ASplitStacksRectangleIsScaledToItsMaster()
    {
        // A split Bayer stack's accumulator is its sub-plane grid, half the demosaiced master's in each direction.
        var plane = new float[Height * 2, Width * 2];
        for (var y = 0; y < Height * 2; y++)
        {
            for (var x = 0; x < Width * 2; x++)
            {
                plane[y, x] = Math.Sqrt(((x - Width) * (x - Width)) + ((y - Height) * (y - Height))) < 20 ? 0.4f : 0.03f;
            }
        }
        var master = Image.FromChannel(plane, 1f, 0f);
        var (_, kept) = PlanetaryMaster.CropToCovered(master, PixelRect.FromLTRB(6, 4, Width, Height), 2);
        kept.ShouldBe(PixelRect.FromLTRB(12, 8, Width * 2, Height * 2));
    }
}
