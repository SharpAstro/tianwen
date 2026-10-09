using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhanced pipeline's best stack as one routine (<see cref="PlanetaryBestStack"/>, #1159): what <c>planetary stack</c> and the
/// GUI's "Best stack" both run. The telescope a profile describes becomes the pupil the derived sharpening needs; a capture whose
/// planet is unknown is sharpened by the preset and says so; a run reports its progress to the end.
/// </summary>
public class PlanetaryBestStackTests
{
    [Fact]
    public void AProfilesApertureAndDesignGiveThePupilTheSharpeningNeeds()
    {
        PlanetaryBestStack.PupilFor(254, OpticalDesign.Newtonian).ShouldBe(new Imaging.Optics.Pupil(0.254, ObstructionRatio: 0.25, Vanes: 4, VaneWidthM: 0.001));
        PlanetaryBestStack.PupilFor(203, OpticalDesign.SCT)?.ObstructionRatio.ShouldBe(0.33);
        PlanetaryBestStack.PupilFor(102, OpticalDesign.Cassegrain)?.ObstructionRatio.ShouldBe(0.33);
        PlanetaryBestStack.PupilFor(80, OpticalDesign.Refractor).ShouldBe(new Imaging.Optics.Pupil(0.08));
        PlanetaryBestStack.PupilFor(150, OpticalDesign.Unknown)?.ObstructionRatio.ShouldBe(0);
        PlanetaryBestStack.PupilFor(null, OpticalDesign.Newtonian).ShouldBeNull();
        PlanetaryBestStack.PupilFor(0, OpticalDesign.Newtonian).ShouldBeNull();
    }

    [Fact(Timeout = 120_000)]
    public async Task ARunReportsItsProgressToTheEndAndSaysHowItWasSharpened()
    {
        const int n = 96, frames = 24;
        var random = new Random(3);
        var planes = new float[frames][,];
        for (var i = 0; i < frames; i++)
        {
            planes[i] = TexturedDisk(n, 48 + (random.NextDouble() * 3) - 1.5, 48 + (random.NextDouble() * 3) - 1.5, 26);
        }
        var progress = new Recorded();
        // No planet named: the stack is not de-rotated and the sharpening cannot be derived, so it is the preset's.
        var options = new PlanetaryBestStackOptions(null, null) { Stack = new PlanetaryStackOptions { AlignmentPatchSize = 16 } };

        var result = await PlanetaryBestStack.RunAsync(new InMemoryFrameStream(planes), options, progress, TestContext.Current.CancellationToken);

        try
        {
            result.Stack.FramesGraded.ShouldBe(frames);
            result.Stack.Epoch.ShouldBeNull();
            result.Sharpened.Width.ShouldBe(result.Stack.Master.Width);
            result.HowSharpened.ShouldStartWith("PlanetaryDefault");
            progress.Values.Count.ShouldBeGreaterThan(2);
            progress.Values[^1].ShouldBe(1);
            for (var i = 1; i < progress.Values.Count; i++)
            {
                progress.Values[i].ShouldBeGreaterThanOrEqualTo(progress.Values[i - 1]);
            }
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
        }
    }

    // Reported values in order, on the reporting thread (Progress<T> would post them to a context the test does not pump).
    private sealed class Recorded : IProgress<double>
    {
        private readonly System.Threading.Lock _gate = new();
        public List<double> Values { get; } = [];

        public void Report(double value)
        {
            // The stack loads frames from parallel passes, so reports can arrive together; this is a test probe, read once at the end.
            lock (_gate)
            {
                Values.Add(value);
            }
        }
    }

    private static float[,] TexturedDisk(int n, double cx, double cy, double radius)
    {
        var a = new float[n, n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                a[y, x] = (dx * dx) + (dy * dy) < radius * radius
                    ? (float)(0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3)))
                    : 0.03f;
            }
        }
        return a;
    }
}
