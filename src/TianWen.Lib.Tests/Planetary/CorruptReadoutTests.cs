using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A frame the camera corrupted as it read it out (<see cref="FrameGrader.IsCorruptReadout"/>): 2024-12-15's Uranus-C capture
/// carries four in 30,000, their top rows at full scale, and the Laplacian took one for every stack's reference and the capture
/// statistics' (docs/plans/planetary-restoration.md, R5a).
/// </summary>
public class CorruptReadoutTests
{
    private const int Width = 96;
    private const int Height = 72;
    private const double CentreX = 47.3;
    private const double CentreY = 36.6;
    private const double Radius = 18;

    // A soft-edged disk at `level` over a sky of 0.03, with a little noise, every sample clipped to the full scale of 1.
    private static float[,] Disk(Random random, double level = 0.4)
    {
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var r = Math.Sqrt(((x - CentreX) * (x - CentreX)) + ((y - CentreY) * (y - CentreY)));
                var inside = Math.Clamp((Radius - r + 1.5) / 3, 0, 1);
                frame[y, x] = (float)Math.Clamp(0.03 + (level * inside) + (0.004 * PhaseScreen.Gaussian(random)), 0, 1);
            }
        }
        return frame;
    }

    // What the Uranus-C camera did to four frames: its top rows read out at full scale all the way across.
    private static float[,] Glitched(float[,] frame, int rows = 2)
    {
        var copy = (float[,])frame.Clone();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                copy[y, x] = 1f;
            }
        }
        return copy;
    }

    [Fact]
    public void AFrameWhoseTopRowsTheCameraSaturatedIsCorruptAndScoresZero()
    {
        var random = new Random(1);
        var clean = Image.FromChannel(Disk(random), 1f, 0f);
        var glitched = Image.FromChannel(Glitched(Disk(random)), 1f, 0f);
        var estimator = new LaplacianEnergyEstimator();

        FrameGrader.IsCorruptReadout(clean).ShouldBeFalse();
        FrameGrader.IsCorruptReadout(glitched).ShouldBeTrue();
        FrameGrader.Grade(estimator, clean).ShouldBeGreaterThan(0f);
        FrameGrader.Grade(estimator, glitched).ShouldBe(0f);
        // Without the rule the glitch is the sharpest thing in the capture, which is how it became the reference.
        estimator.Score(glitched, PlanetaryDisk.BoundingBox(glitched)).ShouldBeGreaterThan(estimator.Score(clean, PlanetaryDisk.BoundingBox(clean)));
    }

    [Fact]
    public void AnOverexposedDiskIsNotCorrupt()
    {
        // A disk at twice full scale, clipped: its core is at full scale, but no row of the frame is.
        var frame = Image.FromChannel(Disk(new Random(2), level: 2), 1f, 0f);

        FrameGrader.IsCorruptReadout(frame).ShouldBeFalse();
    }

    [Fact]
    public void AnOverexposedMoonFillingTheFrameIsNotCorrupt()
    {
        // Rows 20 to 40 saturated right across, fading through bright rows on either side: a region, not a line.
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            var level = Math.Clamp(1.4 - (0.04 * Math.Abs(y - 30)), 0.6, 1.0);
            for (var x = 0; x < Width; x++)
            {
                frame[y, x] = (float)level;
            }
        }

        FrameGrader.IsCorruptReadout(Image.FromChannel(frame, 1f, 0f)).ShouldBeFalse();
    }

    [Fact]
    public async Task TheStackerNeverTakesACorruptFrameForItsReferenceNorStacksIt()
    {
        var random = new Random(3);
        var frames = new float[8][,];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = i == 3 ? Glitched(Disk(random)) : Disk(random);
        }
        using var stream = new InMemoryFrameStream(frames);

        var grades = await new FrameGrader(new LaplacianEnergyEstimator()).GradeAllAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        grades[3].Score.ShouldBe(0f);
        FrameGrader.Reference(grades).ShouldNotBe(3);

        var stack = await new LuckyImagingStacker().StackGlobalAsync(stream, new PlanetaryStackOptions { KeepFraction = 1.0 }, TestContext.Current.CancellationToken);
        try
        {
            stack.ReferenceIndex.ShouldNotBe(3);
            // Kept at no weight: the glitched rows are the sky's in the stack, not a seventh of full scale.
            var top = stack.Master.GetChannelSpan(0)[..Width].ToArray();
            top.Max().ShouldBeLessThan(0.1f);
        }
        finally
        {
            stack.Master.Release();
        }
    }

    [Fact]
    public async Task TheCaptureStatisticsNeverTakeACorruptFrameForTheirReference()
    {
        var random = new Random(4);
        var frames = new float[12][,];
        var times = new DateTimeOffset[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = i == 5 ? Glitched(Disk(random), rows: 4) : Disk(random);
            times[i] = new DateTimeOffset(2024, 12, 15, 12, 36, 43, TimeSpan.Zero) + TimeSpan.FromMilliseconds(2.25 * i);
        }
        using var stream = new InMemoryFrameStream(frames, times);

        var statistics = await PlanetaryCaptureStatistics.MeasureAsync(stream, new CaptureStatisticsOptions(new LimbFitOptions(1)) { FullScaleAdu = 255, Pairs = 5 },
            cancellationToken: TestContext.Current.CancellationToken);

        statistics.ShouldNotBeNull();
        statistics.ReferenceIndex.ShouldNotBe(5);
        // The glitch's line of full-scale samples pulled the disk's first estimate a quarter of its radius off on the real capture.
        statistics.DiskX.ShouldBe(CentreX, 0.5);
        statistics.DiskY.ShouldBe(CentreY, 0.5);
    }
}
