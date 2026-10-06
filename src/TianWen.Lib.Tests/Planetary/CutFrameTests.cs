using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A frame whose planet the frame's edge cuts, or which holds none (<see cref="FrameGrader.IsCutOrEmpty"/>): an untracked Dobsonian
/// lets the planet drift out of its field, and on 2022-10-09's Saturn the stacker kept such frames among the best half and summed the
/// ones it registered wrong into a second, partial Saturn. They score zero while the capture holds enough whole frames, and never when
/// it holds none, as a Moon filling the field does.
/// </summary>
public class CutFrameTests
{
    private const int Width = 96;
    private const int Height = 72;
    private const double Radius = 14;

    // A soft-edged disk centred at (cx, cy) at a level of 0.4 over a sky of 0.03 with a little noise, and a small moon where asked.
    private static float[,] Frame(Random random, double cx, double cy, double radius = Radius, (double X, double Y)? moon = null)
    {
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var inside = Math.Clamp((radius - r + 1.5) / 3, 0, 1);
                if (moon is { } m && Math.Sqrt(((x - m.X) * (x - m.X)) + ((y - m.Y) * (y - m.Y))) < 2.5)
                {
                    inside = Math.Max(inside, 0.6);
                }
                frame[y, x] = (float)Math.Clamp(0.03 + (0.4 * inside) + (0.004 * PhaseScreen.Gaussian(random)), 0, 1);
            }
        }
        return frame;
    }

    // `frame` as PIPP crops a planet the CAMERA cut (#1291): everything on the far side of a line inside the frame is the crop's black, so the
    // cut lies inside the frame, away from its edge. A row cut blacks the rows above `row`; a column cut the columns left of `column`.
    private static float[,] CutInside(float[,] frame, int? row = null, int? column = null)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (y < row || x < column)
                {
                    frame[y, x] = 0f;
                }
            }
        }
        return frame;
    }

    private static float[,] Sky(Random random)
    {
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                frame[y, x] = (float)Math.Clamp(0.03 + (0.004 * PhaseScreen.Gaussian(random)), 0, 1);
            }
        }
        return frame;
    }

    [Fact]
    public void APlanetTheEdgeCutsOrNoPlanetAtAllIsCutAndAMoonAtTheEdgeIsNot()
    {
        var random = new Random(1);
        FrameGrader.IsCutOrEmpty(Image.FromChannel(Frame(random, 47.3, 36.6), 1f, 0f)).ShouldBeFalse("a whole planet");
        FrameGrader.IsCutOrEmpty(Image.FromChannel(Frame(random, 6.2, 36.6), 1f, 0f)).ShouldBeTrue("a planet half out of the frame");
        FrameGrader.IsCutOrEmpty(Image.FromChannel(Sky(random), 1f, 0f)).ShouldBeTrue("no planet");
        FrameGrader.IsCutOrEmpty(Image.FromChannel(Frame(random, 47.3, 36.6, moon: (1.5, 10)), 1f, 0f)).ShouldBeFalse("a whole planet, a moon at the edge");
    }

    [Fact]
    public void APlanetTheCameraCutAndPippMovedAwayFromTheEdgeIsCutAndASmallWholeDiskIsNot()
    {
        // The owner's 2021-08-19 Saturn (#1291): an untracked Dobsonian's planet ran off the sensor, PIPP centred what was left, and the
        // cut, a straight line with the crop's black beyond it, lay inside the frame where the edge test never looked.
        var random = new Random(4);
        FrameGrader.IsCutOrEmpty(Image.FromChannel(CutInside(Frame(random, 47.3, 36.6), row: 33), 1f, 0f)).ShouldBeTrue("cut across its middle");
        FrameGrader.IsCutOrEmpty(Image.FromChannel(CutInside(Frame(random, 47.3, 36.6), column: 40), 1f, 0f)).ShouldBeTrue("cut down its side");
        FrameGrader.IsCutOrEmpty(Image.FromChannel(CutInside(Frame(random, 47.3, 36.6), row: 26), 1f, 0f)).ShouldBeTrue("trimmed near its limb");
        for (var i = 0; i < 20; i++)
        {
            FrameGrader.IsCutOrEmpty(Image.FromChannel(Frame(random, 47.3 + (0.37 * i), 36.6 - (0.21 * i)), 1f, 0f)).ShouldBeFalse("a whole planet at any phase");
            FrameGrader.IsCutOrEmpty(Image.FromChannel(Frame(random, 47.3 + (0.37 * i), 36.6, radius: 3), 1f, 0f)).ShouldBeFalse("a whole disk of three pixels");
        }
    }

    [Fact]
    public void TheGradesOwnBoxIsTheBoundingBoxOnAMonoFrameAndAColourOne()
    {
        // The grade finds the disk's box in its cut test's scan, and the rolling stack registers the frame by that box (#1174): it must
        // be BoundingBox's, a whole planet's, a cut one's, a corner's, an empty frame's, a moon's at the edge, a filled field's and a
        // speck's below the pixels a disk needs.
        var random = new Random(3);
        var frames = new (float[,] Data, bool? Cut)[]
        {
            (Frame(random, 47.3, 36.6), false), (Frame(random, 6.2, 36.6), true), (Frame(random, 93.5, 70.2), true), (Sky(random), true),
            (Frame(random, 47.3, 36.6, moon: (1.5, 10)), false), (Frame(random, 47.3, 36.6, radius: 60), true), (Frame(random, 30.4, 20.7, radius: 1), null),
            (CutInside(Frame(random, 47.3, 36.6), row: 33), true),
        };
        foreach (var (data, cut) in frames)
        {
            var mono = Image.FromChannel(data, 1f, 0f);
            var (box, monoCut, _) = PlanetaryDisk.BoundingBoxAndCut(mono);
            box.ShouldBe(PlanetaryDisk.BoundingBox(mono));
            if (cut is { } expected)
            {
                monoCut.ShouldBe(expected);
            }

            var colour = new Image([data, Scaled(data, 0.8f), Scaled(data, 0.5f)], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
            PlanetaryDisk.BoundingBoxAndCut(colour).Box.ShouldBe(PlanetaryDisk.BoundingBox(colour));
        }

        static float[,] Scaled(float[,] data, float gain)
        {
            var scaled = new float[data.GetLength(0), data.GetLength(1)];
            for (var y = 0; y < data.GetLength(0); y++)
            {
                for (var x = 0; x < data.GetLength(1); x++)
                {
                    scaled[y, x] = data[y, x] * gain;
                }
            }
            return scaled;
        }
    }

    [Fact]
    public async Task ACaptureWithWholeFramesLeavesItsCutOnesOutButOneOfNoneKeepsThemAll()
    {
        var random = new Random(2);
        // A planet drifting out of the field: eight whole frames, then two cut by the edge, two cut inside the frame and two empty.
        var drift = Enumerable.Range(0, 8).Select(i => Frame(random, 47.3 - i, 36.6))
            .Concat([Frame(random, 8, 36.6), Frame(random, 2, 36.6), CutInside(Frame(random, 47.3, 36.6), row: 33), CutInside(Frame(random, 47.3, 36.6), column: 44), Sky(random), Sky(random)])
            .ToArray();
        var grades = await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(new InMemoryFrameStream(drift), cancellationToken: TestContext.Current.CancellationToken);

        grades.Take(8).ShouldAllBe(g => !g.Cut && g.Score > 0, "the whole frames");
        grades.Skip(8).ShouldAllBe(g => g.Cut && g.Score == 0, "the cut and empty frames");
        FrameGrader.SelectBest(grades, 1.0).OrderBy(i => i).ShouldBe(Enumerable.Range(0, 8), "keeping every frame keeps only the whole ones");

        // A Moon filling the field: every frame's blob reaches the edge, none is whole, and none is left out.
        var filling = Enumerable.Range(0, 6).Select(i => Frame(random, 47.3 + i, 36.6, radius: 60)).ToArray();
        var lunar = await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(new InMemoryFrameStream(filling), cancellationToken: TestContext.Current.CancellationToken);
        lunar.ShouldAllBe(g => g.Cut && g.Score > 0, "a field the planet fills");
    }

    [Fact]
    public void TheBestNeverIncludeAFrameScoredZeroWhileOneScoresAbove()
    {
        var grades = new[] { new FrameGrade(0, 0f), new FrameGrade(1, 2f), new FrameGrade(2, 0f), new FrameGrade(3, 1f) }.ToImmutableArray();
        FrameGrader.SelectBest(grades, 1.0).ShouldBe([1, 3]);
        FrameGrader.SelectBest([new FrameGrade(0, 0f), new FrameGrade(1, 0f)], 0.5).Length.ShouldBe(1, "a capture of nothing but zeros still keeps one");
    }

    [Fact]
    public void ALiveStackLeavesOutAFrameTheEdgeCutsOnceItHasSeenWholeOnes()
    {
        FrameGrader.DropsCutFrames(whole: 0, graded: 3).ShouldBeFalse("nothing whole seen yet");
        FrameGrader.DropsCutFrames(whole: 1, graded: 20).ShouldBeTrue("one in twenty is whole");
        FrameGrader.DropsCutFrames(whole: 1, graded: 21).ShouldBeFalse("fewer than one in twenty");
    }
}
