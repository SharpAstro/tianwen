using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A run's frame-quality curve (#1364, <c>planetary stack --report</c>): read off the grades the keep was chosen on, the sorted curve
/// cuts where the stack cuts, the run's curve is binned a hundred frames a point and a segment a file, frames the stack leaves out are in
/// neither and counted by cause, and the picture marks the keep at its quality. With a grade cache, the grades a report reads before the
/// stack runs are the ones the stack then chooses its keep on.
/// </summary>
public sealed class PlanetaryQualityCurveTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    // Graded frames whose quality over the best is given, by frame, in time order.
    private static ImmutableArray<FrameGrade> Graded(params float[] quality)
        => [.. quality.Select((q, i) => new FrameGrade(i, q))];

    [Fact]
    public void TheSortedCurveIsEachGradedFrameOverTheBestBestFirst()
    {
        var curve = PlanetaryQualityCurve.From(Graded(20f, 80f, 40f, 60f), 0.5);

        curve.Sorted.ToArray().ShouldBe(new[] { 100f, 75f, 50f, 25f });
        (curve.Frames, curve.Graded, curve.LeftOut).ShouldBe((4, 4, 0));
    }

    [Fact]
    public void TheKeepCutsWhereTheStackDoesAndALeftOutFrameIsCountedByItsCause()
    {
        // Ten graded frames and five left out (their score zero): two cut, one smeared, one dim, one the grader could not read.
        ImmutableArray<FrameGrade> leftOut =
        [
            new FrameGrade(10, 0f, Cut: true), new FrameGrade(11, 0f, Cut: true), new FrameGrade(12, 0f, Smeared: true),
            new FrameGrade(13, 0f, Dim: true), new FrameGrade(14, 0f),
        ];
        var grades = Graded(10f, 9f, 8f, 7f, 6f, 5f, 4f, 3f, 2f, 1f).AddRange(leftOut);

        var curve = PlanetaryQualityCurve.From(grades, 0.5);

        (curve.LeftOutCut, curve.LeftOutSmeared, curve.LeftOutDim, curve.LeftOutUnscored).ShouldBe((2, 1, 1, 1));
        curve.Graded.ShouldBe(10);
        // Half of 15 frames is 8 (rounded away from zero, as SelectBest rounds), the eighth best being quality 30.
        curve.KeptFrames.ShouldBe(FrameGrader.SelectBest(grades, 0.5).Length);
        curve.KeptFrames.ShouldBe(8);
        curve.KeepQuality.ShouldBe(30f);
        // A keep past the graded frames stops at them: the stack never keeps a frame it left out.
        PlanetaryQualityCurve.From(grades, 0.9).KeptFrames.ShouldBe(FrameGrader.SelectBest(grades, 0.9).Length);
    }

    [Fact]
    public void TheReferenceCutsReadTheSortedCurveAtTheirSharesOfTheRun()
    {
        var curve = PlanetaryQualityCurve.From(Graded([.. Enumerable.Range(1, 100).Select(i => (float)i)]), 0.5);

        // The 10th best of 100 frames is quality 91, the 35th is 66, the 70th is 31.
        curve.ReferenceCuts.ToArray().ShouldBe(new[]
        {
            new QualityCut(0.10, 91f), new QualityCut(0.20, 81f), new QualityCut(0.35, 66f), new QualityCut(0.50, 51f), new QualityCut(0.70, 31f),
        });
        curve.QualityAt(0.5).ShouldBe(curve.KeepQuality);
    }

    [Fact]
    public void TheRunIsBinnedAHundredFramesAPointAndNeverAcrossAFile()
    {
        // 1,000 frames in two files, the second from frame 650: 0-99, ..., 500-599, 600-649, then 650-749, ..., 950-999.
        var curve = PlanetaryQualityCurve.From(Graded([.. Enumerable.Range(0, 1000).Select(i => 50f + (i % 10))]), 0.5, [0, 650]);

        curve.Bins.Select(bin => (bin.First, bin.Count)).ToArray().ShouldBe(new[]
        {
            (0, 100), (100, 100), (200, 100), (300, 100), (400, 100), (500, 100), (600, 50),
            (650, 100), (750, 100), (850, 100), (950, 50),
        });
        curve.Bins.ShouldAllBe(bin => bin.Graded == bin.Count && bin.P10 <= bin.Median && bin.Median <= bin.P90);
    }

    [Fact]
    public void ABinOfFramesAllLeftOutReadsNothing()
    {
        var grades = Graded([.. Enumerable.Range(0, 200).Select(i => i < 100 ? 0f : 80f)]);

        var curve = PlanetaryQualityCurve.From(grades, 0.5);

        curve.Bins[0].Graded.ShouldBe(0);
        float.IsNaN(curve.Bins[0].Median).ShouldBeTrue();
        curve.Bins[1].Median.ShouldBe(100f);
    }

    [Fact]
    public void TheHeadEndsWhereTheCurveStopsFallingFasterThanTwiceItsMeanSlope()
    {
        // A steep head over the best 5 % (100 down to 70) and a gentle fall after it (70 down to 40 over the rest).
        var quality = Enumerable.Range(0, 1000).Select(i => i <= 50 ? 100f - (0.6f * i) : 70f - (0.0316f * (i - 50))).ToArray();

        var curve = PlanetaryQualityCurve.From(Graded(quality), 0.5);

        curve.HeadEndsAt.ShouldNotBeNull().ShouldBeInRange(0.045, 0.06);
    }

    [Fact]
    public void TheReportMarksTheKeepAtItsQualityOnTheSortedPlot()
    {
        var curve = PlanetaryQualityCurve.From(Graded([.. Enumerable.Range(1, 400).Select(i => (float)i)]), 0.25);
        using var renderer = new RgbaImageRenderer(PlanetaryQualityReport.Width, PlanetaryQualityReport.Height);

        // No face, so no words: the marks alone, which is what is checked.
        var layout = PlanetaryQualityReport.Render(renderer, curve, "test", fontFamily: "");

        var plot = layout.SortedPlot;
        var (plotW, plotH) = (plot.LowerRight.X - plot.UpperLeft.X, plot.LowerRight.Y - plot.UpperLeft.Y);
        layout.KeepX.ShouldBe(plot.UpperLeft.X + (0.25f * plotW), 0.5f);
        layout.KeepY.ShouldBe(plot.LowerRight.Y - ((curve.KeepQuality - layout.QualityFloor) / (100f - layout.QualityFloor) * plotH), 0.5f);
        // The mark is drawn there, in the keep's colour.
        var pixel = renderer.Surface.Pixels.AsSpan((((int)layout.KeepY * (int)PlanetaryQualityReport.Width) + (int)layout.KeepX) * 4, 4);
        (pixel[0], pixel[1], pixel[2]).ShouldBe(((byte)0xff, (byte)0xb0, (byte)0x40));
    }

    [Fact]
    public void TheQualityAxisStartsALittleBelowTheLowestQualityShown()
    {
        // The sorted curve ends at 72 and no bin's 10th percentile is lower: the axis starts at 60, never at a quality shown.
        var curve = PlanetaryQualityCurve.From(Graded([.. Enumerable.Range(0, 300).Select(i => 72f + (28f * i / 299f))]), 0.5);

        PlanetaryQualityReport.QualityFloor(curve).ShouldBe(60f);
        PlanetaryQualityReport.QualityFloor(PlanetaryQualityCurve.From(Graded(2f, 100f), 0.5)).ShouldBe(0f);
    }

    [Fact]
    public void TheCaptionCountsTheFramesLeftOutByCause()
    {
        ImmutableArray<FrameGrade> leftOut = [new FrameGrade(3, 0f, Cut: true), new FrameGrade(4, 0f, Dim: true)];
        var grades = Graded(9f, 8f, 7f).AddRange(leftOut);

        PlanetaryQualityReport.Caption(PlanetaryQualityCurve.From(grades, 0.5))
            .ShouldStartWith("5 frames, 3 graded; 2 left out and in neither panel (1 cut, 0 smeared, 1 dim, 0 unreadable)");
    }

    [Fact(Timeout = 120_000)]
    public async Task ReportOnlyGradesAreTheGradesTheStackChoseItsKeepOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = _folders.Create("quality-report-").FullName;
        var capture = Path.Combine(folder, "capture.ser");
        PlanetarySerFixtures.WriteSer(capture, 64, 64, SerColorId.Mono, Frames(40));
        var options = new PlanetaryStackOptions { GradeCache = Path.Combine(folder, "grades") };

        PlanetaryStackResult stacked;
        using (var stream = SerFrameStream.Open(capture))
        {
            stacked = await new LuckyImagingStacker().StackGlobalAsync(stream, options, ct);
        }
        ImmutableArray<FrameGrade> graded;
        using (var stream = SerFrameStream.Open(capture))
        {
            graded = await LuckyImagingStacker.GradeAsync(stream, options, ct);
        }
        stacked.Master.Release();

        stacked.Grades.Length.ShouldBe(40);
        graded.ToArray().ShouldBe(stacked.Grades.ToArray());
        PlanetaryQualityCurve.From(graded, options.KeepFraction).KeptFrames.ShouldBe(stacked.FramesUsed);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheReportIsWrittenAsAPng()
    {
        var path = Path.Combine(_folders.Create("quality-png-").FullName, "master_capture_quality.png");

        await PlanetaryQualityReport.WritePngAsync(PlanetaryQualityCurve.From(Graded(30f, 90f, 60f), 0.5), "capture: frame quality", path,
            TestContext.Current.CancellationToken);

        File.ReadAllBytes(path).AsSpan(0, 8).ToArray().ShouldBe(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
    }

    // A textured disk blurred more in some frames than others, so the grades differ from frame to frame.
    private static ushort[][] Frames(int count)
    {
        var rng = new Random(11);
        var frames = new ushort[count][];
        for (var f = 0; f < count; f++)
        {
            var blur = rng.NextDouble();
            var frame = new ushort[64 * 64];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    var (dx, dy) = (x - 32.0, y - 32.0);
                    var inside = (dx * dx) + (dy * dy) < 22 * 22;
                    var texture = 0.5 + (0.25 * (1 - blur) * Math.Sin(x * 0.7) * Math.Cos(y * 0.6));
                    frame[(y * 64) + x] = (ushort)((inside ? texture : 0.03) * 60000);
                }
            }
            frames[f] = frame;
        }
        return frames;
    }
}
