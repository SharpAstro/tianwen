using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
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
            // Not de-rotated, so no Derotating stage; the colour balance of a mono run is whatever the options ask.
            progress.Stages().Take(3).ShouldBe([PlanetaryStackStage.Stacking, PlanetaryStackStage.Finishing, PlanetaryStackStage.Sharpening]);
            progress.Stages().ShouldNotContain(PlanetaryStackStage.Derotating);
            progress.Values[^1].Fraction.ShouldBe(1);
            AssertStackingRisesToItsEnd(progress);
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
        }
    }

    [Fact(Timeout = 240_000)]
    public async Task ADerotatedRunCountsItsLimbFitLoadsAndReportsEveryStageOnceInOrder()
    {
        var capture = FrameDerotationCaptures.Capture(frames: 41, minutes: 16, seed: 5);
        var progress = new Recorded();
        // A turn of nought asks for the de-rotation whatever the capture turns, so the limb fits' loads are made.
        var options = new PlanetaryBestStackOptions(CatalogIndex.Jupiter, null)
        {
            Stack = new PlanetaryStackOptions
            {
                AlignmentPatchSize = 16,
                AlignmentPointSpacing = 12,
                CropToCoverage = false,
                Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) { MinimumTurnPx = 0 },
            },
        };
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);

        var result = await PlanetaryBestStack.RunAsync(stream, options, progress, TestContext.Current.CancellationToken);

        try
        {
            result.Stack.Epoch.ShouldNotBeNull();
            // Stacking, the limb fits it counts, Stacking again to its end, then each later stage as it starts: never an earlier one after.
            var stages = progress.Stages();
            stages.Take(5).ShouldBe([PlanetaryStackStage.Stacking, PlanetaryStackStage.Derotating, PlanetaryStackStage.Stacking,
                PlanetaryStackStage.Finishing, PlanetaryStackStage.Sharpening]);
            stages.Skip(5).ShouldAllBe(s => s == PlanetaryStackStage.BalancingColour);
            AssertStackingRisesToItsEnd(progress);
            progress.Values[^1].Fraction.ShouldBe(1);
            // The de-rotation's loads are counted against their own estimate: its fraction climbs to its end, never short of it
            // (the 99 % a stage holds until the next stage starts), and it ends at 1.
            var turning = progress.Values.Where(p => p.Stage == PlanetaryStackStage.Derotating).Select(p => p.Fraction).ToArray();
            turning[0].ShouldBe(0);
            turning[^1].ShouldBe(1);
            turning[^2].ShouldBeGreaterThanOrEqualTo(0.9, "its loads were counted against the estimate, which they reached");
            // The de-rotation's own loads were made, and the stack counted them (more reads than the plain run's grade, reference and fold).
            stream.LoadCount.ShouldBeGreaterThan(41 + Math.Min(options.Stack.ReferenceFrames, 41) + (int)Math.Ceiling(41 * options.Stack.KeepFraction));
        }
        finally
        {
            result.Stack.Master.Release();
            result.Sharpened.Release();
        }
    }

    // Stacking's fraction never falls, across its two spells either side of the de-rotation, and it ends at 1.
    private static void AssertStackingRisesToItsEnd(Recorded progress)
    {
        var stacking = progress.Values.Where(p => p.Stage == PlanetaryStackStage.Stacking).Select(p => p.Fraction).ToArray();
        stacking.Length.ShouldBeGreaterThan(1);
        for (var i = 1; i < stacking.Length; i++)
        {
            stacking[i].ShouldBeGreaterThanOrEqualTo(stacking[i - 1]);
        }
        stacking[^1].ShouldBe(1);
        // Short of its end until the next stage starts.
        stacking[..^1].ShouldAllBe(f => f < 1);
    }

    // Reported values in order, on the reporting thread (Progress<T> would post them to a context the test does not pump).
    private sealed class Recorded : IProgress<PlanetaryStackProgress>
    {
        private readonly System.Threading.Lock _gate = new();
        public List<PlanetaryStackProgress> Values { get; } = [];

        // The stages in the order they were entered, each run of one stage once.
        public List<PlanetaryStackStage> Stages()
        {
            var stages = new List<PlanetaryStackStage>();
            foreach (var value in Values)
            {
                if (stages.Count == 0 || stages[^1] != value.Stage)
                {
                    stages.Add(value.Stage);
                }
            }
            return stages;
        }

        public void Report(PlanetaryStackProgress value)
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
