using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A SER's view is one switch, Frames / Live / Best (#1314 part 2): Best runs the whole capture through the batch stack once, shows its
/// master behind the same sharpening layer, and switches its stops with no Derive, since the run fitted them. Rule 5: what Best shows at a
/// stop is what <c>planetary stack --strength</c> writes at it. Rule 7: switching views and stops stacks and derives nothing.
/// </summary>
[Collection("Viewer")]
public class ViewerBestViewTests
{
    [Theory(Timeout = 300_000)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(1f, true)]
    public async Task TheBestViewAtEachStopIsWhatPlanetaryStackWritesAtThatStrength(float dpi, bool colour)
    {
        // Rule 5: at each stop, every channel on show equals the run's sharpened master at that strength (the same routine, the same
        // capture) to within 1e-4 of its disk's level.
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = await OpenCaptureAsync(e2e, colour, ct);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewBest" }, "the Best view"));
        var best = await ViewerBestStackTests.BestOnShowAsync(e2e, ct);
        e2e.State.WaveletDerived.ShouldBeTrue("the run's own derivation is on the dials, no Derive asked");
        var limb = e2e.State.WaveletLimb.ShouldNotBeNull(e2e.State.WaveletDeriveNote);
        var (cx, cy, r) = (limb.Fit.CenterX, limb.Fit.CenterY, limb.Fit.EquatorialRadius);
        var derivations = e2e.Controller.DerivationsStarted;

        foreach (var stop in PlanetarySharpening.StrengthStops)
        {
            var drawn = best.MastersDrawnOutsideTheLimb;
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: var a } && a == ButtonFor(stop), $"the {stop} button"));
            if (stop != 1)
            {
                await e2e.PumpUntilAsync(() => best.MastersDrawnOutsideTheLimb > drawn && !best.IsBusy, $"the Best view at {stop}", ct);
            }
            var shown = e2e.Controller.ShownDocument.ShouldNotBeNull().UnstretchedImage;
            using var stream = SerFrameStream.Open(capture);
            var options = new PlanetaryBestStackOptions(CatalogIndex.Jupiter, PlanetaryBestStack.PupilFor(254, OpticalDesign.Newtonian)) { Strength = stop };
            var run = await PlanetaryBestStack.RunAsync(stream, options, cancellationToken: ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"stop {stop}: dials {string.Join(", ", e2e.State.WaveletGains.Select(g => g.ToString("0.00")))} (strength {e2e.State.PlanetaryStrength}, derived {e2e.State.WaveletDerived}, stops {e2e.State.DerivedWaveletGains?.Stops.Length}); the run: {run.HowSharpened}");
            for (var c = 0; c < shown.ChannelCount; c++)
            {
                var (inside, outside) = ViewerPlanetaryMasterTests.Difference(Plane(shown, c), Plane(run.Sharpened, c), cx, cy, r);
                TestContext.Current.TestOutputHelper?.WriteLine($"stop {stop}, channel {c}: inside {inside:E2}, outside {outside:E2} of its disk's level");
                inside.ShouldBeLessThan(1e-4, $"channel {c} inside the limb at {stop}");
                outside.ShouldBeLessThan(1e-4, $"channel {c} outside the limb at {stop}");
            }
            run.Stack.Master.Release();
            run.Sharpened.Release();
            run.Layer?.Master.Release();
        }
        e2e.Controller.DerivationsStarted.ShouldBe(derivations, "the stops come from the run, no Derive");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task SwitchingViewsAndStopsStacksAndDerivesNothing(float dpi)
    {
        // Rule 7, counted, not timed: once Best has run, Live to Best and back, and stop to stop, start no best stack, publish no live
        // stack afresh and start no derivation.
        await using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenCaptureAsync(e2e, colour: false, ct);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view"));
        await e2e.PumpUntilAsync(() => e2e.Controller.ViewLayers.Live is { HasMaster: true, IsBusy: false }, "the live stack's first master", ct);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewBest" }, "the Best view"));
        var best = await ViewerBestStackTests.BestOnShowAsync(e2e, ct);
        var live = e2e.Controller.ViewLayers.Live.ShouldNotBeNull();
        var (stacks, liveStacks, derivations) = (e2e.Controller.BestStacksStarted, live.StacksPublished, e2e.Controller.DerivationsStarted);
        stacks.ShouldBe(1);

        for (var round = 0; round < 2; round++)
        {
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewLive" }, "the Live view"));
            await e2e.PumpUntilAsync(() => ReferenceEquals(e2e.Controller.Source, live) && !live.IsBusy, "the live view back", ct);
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "Strength2" }, "the strength 2 button"));
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "ViewBest" }, "the Best view"));
            await e2e.PumpUntilAsync(() => ReferenceEquals(e2e.Controller.Source, best) && !best.IsBusy, "the Best view back", ct);
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "StrengthTruth" }, "the truth's button"));
            await e2e.PumpUntilAsync(() => !best.IsBusy, "the Best view at the truth", ct);
        }

        e2e.Controller.BestStacksStarted.ShouldBe(stacks, "a switch to Best shows the run it has");
        live.StacksPublished.ShouldBe(liveStacks, "the live view at the same frame is not stacked again");
        e2e.Controller.DerivationsStarted.ShouldBe(derivations, "neither view's stops derive again");
        e2e.State.SequencePath.ShouldNotBeNull("the capture stays open throughout");
    }

    // The test capture, Jupiter by its name, and the telescope that derives its sharpening.
    private static async Task<string> OpenCaptureAsync(ViewerE2E e2e, bool colour, System.Threading.CancellationToken ct)
    {
        var capture = ViewerBestStackTests.WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"), dispersed: colour);
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);
        // Held at one frame: a capture opens playing, and a live stack following a moving playhead is stacked again by right.
        e2e.State.IsPlaying = false;
        e2e.Frame();
        return capture;
    }

    private static Image Plane(Image image, int c)
    {
        return new Image([image.GetChannelArray(c)], BitDepth.Float32, 1f, 0f, 0f, image.ImageMeta);
    }

    private static string ButtonFor(double stop)
    {
        return stop == 1 ? "StrengthTruth" : "Strength" + stop.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "");
    }
}
