using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A planetary master opened as a file gets the sharpening layer the stacked view has (#1314 part 1b): the planet and telescope,
/// the dials, Derive and its stops, over the same source (<see cref="LiveStackPreviewSource"/> on a <see cref="FixedMaster"/>), and
/// a Save writes what is on show. The master is <c>PlanetarySharpeningTests</c>' noisy Jupiter stack, written with a header that names
/// the planet and the instant, as <c>planetary-stack</c> writes one.
/// </summary>
[Collection("Viewer")]
public class ViewerPlanetaryMasterTests
{
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(60);

    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task APlanetaryMasterGetsTheSharpeningSectionsAndADeepSkyFrameDoesNot(float dpi)
    {
        // Rule 4: the section shows for a planetary master (OBJECT names a planet) and never for a deep-sky frame.
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;

        await e2e.OpenAsync(e2e.WriteColourFits("m42.fits"), ct);
        e2e.State.IsPlanetaryMaster.ShouldBeFalse();
        HasButton(e2e, "WaveletDerive").ShouldBeFalse("a deep-sky frame has no sharpening layer");
        HasButton(e2e, "StrengthTruth").ShouldBeFalse();

        await OpenMasterAsync(e2e, ct);
        e2e.State.MasterPlanet.ShouldBe(CatalogIndex.Jupiter);
        HasButton(e2e, "WaveletDerive").ShouldBeTrue("a planetary master has Derive");
        HasButton(e2e, "StrengthTruth").ShouldBeTrue("and the strength stops");
        HasButton(e2e, "BestStack").ShouldBeFalse("Best stack is a capture's, never a master's");
        e2e.State.WaveletSharpenEnabled.ShouldBeFalse("a master opens as the file holds it");
        e2e.Controller.Source.ShouldBeOfType<AstroImageDocument>("with its sharpening off the file is on show");

        await e2e.OpenAsync(e2e.WriteColourFits("m42-again.fits", level: 5), ct);
        e2e.State.IsPlanetaryMaster.ShouldBeFalse();
        HasButton(e2e, "WaveletDerive").ShouldBeFalse("the layer goes with the master");
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task DeriveOnAMasterShowsItSharpenedAndAStopSwitchesItAtOnce(float dpi)
    {
        // Rules 2 and 7 on a master: one Derive seeds the dials and puts the layer on show, its planet from the header; a stop after it
        // switches the dials in the frame it is pressed, with no derivation run.
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenMasterAsync(e2e, ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);

        ViewerWaveletDeriveTests.PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct, untilTimeout: true);
        var note = e2e.State.WaveletDeriveNote.ShouldNotBeNull();
        TestContext.Current.TestOutputHelper?.WriteLine(note);
        note.ShouldStartWith("Gains derived for Jupiter");
        e2e.State.WaveletSharpenEnabled.ShouldBeTrue();
        var layer = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>("the layer is on show once the sharpening is on");
        await DrawnAsync(e2e, layer, 0, ct);

        var truth = e2e.State.WaveletGains;
        var drawn = layer.MastersDrawnOutsideTheLimb;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "Strength2" }, "the strength 2 button"));
        e2e.State.WaveletDeriving.ShouldBeFalse("a stop after Derive derives nothing");
        e2e.State.WaveletGains.ToArray().ShouldBe(e2e.State.DerivedWaveletGains.ShouldNotBeNull().GainsAt(2).ToArray());
        e2e.State.WaveletGains.ToArray().ShouldNotBe(truth.ToArray());
        await DrawnAsync(e2e, layer, drawn, ct);

        // Sharpening off shows the file as it is again.
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "WaveletToggle" }, "the sharpen toggle"));
        e2e.State.WaveletSharpenEnabled.ShouldBeFalse();
        e2e.Controller.Source.ShouldBeOfType<AstroImageDocument>();
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AMastersStopShowsWhatPlanetarySharpenWritesAtThatStrength(float dpi)
    {
        // Rule 1: at each stop the planes on show equal planetary-sharpen --strength's for that master (PlanetarySharpening, its own
        // derivation at that strength), to within 1e-4 of the disk's level, inside the limb and outside it. Measured 1.2e-6 inside and
        // 5e-10 outside at every stop when it was written: the dials take the derivation's own gains, and the limb is drawn as the batch
        // draws it (#1201).
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var path = await OpenMasterAsync(e2e, ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);
        ViewerWaveletDeriveTests.PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct, untilTimeout: true);
        var layer = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>(e2e.State.WaveletDeriveNote);
        await DrawnAsync(e2e, layer, 0, ct);
        var limb = e2e.State.WaveletLimb.ShouldNotBeNull();

        Image.TryReadFitsFile(path, out var master).ShouldBeTrue();
        var instant = PlanetaryBestStack.InstantOf(master, epoch: null).ShouldNotBeNull();
        var pupil = PlanetaryBestStack.PupilFor(254, OpticalDesign.Newtonian).ShouldNotBeNull();
        var (cx, cy, r) = (limb.Fit.CenterX, limb.Fit.CenterY, limb.Fit.EquatorialRadius);
        foreach (var stop in PlanetarySharpening.StrengthStops)
        {
            var drawn = layer.MastersDrawnOutsideTheLimb;
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: var a } && a == ButtonFor(stop), $"the {stop} button"));
            if (stop != 1)
            {
                await DrawnAsync(e2e, layer, drawn, ct);
            }
            var shown = e2e.Controller.ShownDocument.ShouldNotBeNull().UnstretchedImage;
            var batch = (await Task.Run(() => PlanetarySharpening.Sharpen(master,
                new PlanetarySharpenOptions(CatalogIndex.Jupiter, instant, pupil) { WavelengthsNm = [550], Strength = stop }), ct)).ShouldNotBeNull();
            var (inside, outside) = Difference(shown, batch.Sharpened, cx, cy, r);
            TestContext.Current.TestOutputHelper?.WriteLine($"stop {stop}: largest difference inside the limb {inside:E2}, outside {outside:E2} of the disk's level; gains {string.Join(", ", e2e.State.WaveletGains.Select(g => g.ToString("0.00")))} against {string.Join(", ", batch.Gains.Select(g => g.ToString("0.00")))}");
            batch.Sharpened.Release();
            inside.ShouldBeLessThan(1e-4, $"inside the limb at {stop}");
            outside.ShouldBeLessThan(1e-4, $"outside the limb at {stop}");
        }
        master.Release();
    }

    [Theory(Timeout = 300_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ASaveWritesWhatIsOnShow(float dpi)
    {
        // Rule 6: a Save writes the master on show, the layer's while its sharpening is on and the file as it is once it is off. Before,
        // a Save always wrote the document under the layer (and, in a SER's stacked view, nothing at all).
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        await OpenMasterAsync(e2e, ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);

        var asFile = await SaveAsync(e2e, "as-file.tif", ct);
        ViewerWaveletDeriveTests.PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct, untilTimeout: true);
        var layer = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>(e2e.State.WaveletDeriveNote);
        var drawn = layer.MastersDrawnOutsideTheLimb;
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "Strength2" }, "the strength 2 button"));
        await DrawnAsync(e2e, layer, drawn, ct);
        e2e.Controller.ShownDocument.ShouldBeSameAs(layer.Document, "what is on show is the layer's master");
        var sharpened = await SaveAsync(e2e, "sharpened.tif", ct);
        e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: "WaveletToggle" }, "the sharpen toggle"));
        var asFileAgain = await SaveAsync(e2e, "as-file-again.tif", ct);

        var (fileVsAgain, fileVsSharpened) = (LargestDifference(asFile, asFileAgain), LargestDifference(asFile, sharpened));
        TestContext.Current.TestOutputHelper?.WriteLine($"the file saved twice: {fileVsAgain:E2} apart; the file against the layer at strength 2: {fileVsSharpened:0.000}");
        fileVsAgain.ShouldBe(0, "with its sharpening off the file is what is saved, as before");
        fileVsSharpened.ShouldBeGreaterThan(0.01, "with it on, the sharpened master on show is");
    }

    [Theory(Timeout = 600_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AColourMastersStopShowsWhatTheBatchWritesInEveryChannel(float dpi)
    {
        // Rule 1 on a colour master: each channel at each stop equals the batch's (colours moved onto green, sharpened per channel, then
        // balanced, as planetary-stack writes it) to within 1e-4 of that channel's disk level. With the first channel's gains on every
        // channel, green read 3.0e-3 and blue 5.5e-3 inside the limb: each derives its own, through its own diffraction.
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var meta = new ImageMeta("e2e", PlanetarySharpeningTests.Night - (Exposure / 2), Exposure, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN) with { ObjectName = "Jupiter" };
        var (_, mono) = PlanetarySharpeningTests.NoisyStack();
        var grey = mono.GetChannelSpan(0).ToArray();
        var (w, h) = (mono.Width, mono.Height);
        mono.Release();
        (float Gain, float Sky)[] camera = [(0.9f, 0.04f), (0.6f, 0.03f), (0.35f, 0.05f)];
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[h, w];
            for (var i = 0; i < grey.Length; i++)
            {
                planes[c][i / w, i % w] = (camera[c].Gain * (grey[i] - 0.05f)) + camera[c].Sky;
            }
        }
        var colour = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta);
        var path = Path.Combine(e2e.Folder, "master_jupiter_colour.fits");
        colour.WriteToFitsFile(path);
        colour.Release();
        await e2e.OpenAsync(path, ct);
        await e2e.PumpUntilAsync(() => e2e.State.IsPlanetaryMaster, "the master's sharpening layer", ct);
        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign) = (254, OpticalDesign.Newtonian);
        ViewerWaveletDeriveTests.PressDerive(e2e);
        await e2e.PumpUntilAsync(() => e2e.State.WaveletDeriveNote is not null && !e2e.State.WaveletDeriving, "the derivation's answer", ct, untilTimeout: true);
        TestContext.Current.TestOutputHelper?.WriteLine(e2e.State.WaveletDeriveNote ?? "");
        var layer = e2e.Controller.Source.ShouldBeOfType<LiveStackPreviewSource>(e2e.State.WaveletDeriveNote);
        await DrawnAsync(e2e, layer, 0, ct);
        var limb = e2e.State.WaveletLimb.ShouldNotBeNull();

        Image.TryReadFitsFile(path, out var master).ShouldBeTrue();
        var instant = PlanetaryBestStack.InstantOf(master, epoch: null).ShouldNotBeNull();
        var pupil = PlanetaryBestStack.PupilFor(254, OpticalDesign.Newtonian).ShouldNotBeNull();
        var (onGreen, _) = PlanetaryChannelAlignment.Align(master, PlanetaryFrameLayout.Rgb, PlanetaryChannelAlignment.LimbOptionsFor(CatalogIndex.Jupiter, instant));
        var balance = PlanetaryColourBalance.For(onGreen, CatalogIndex.Jupiter, instant).Balance.ShouldNotBeNull();
        var (cx, cy, r) = (limb.Fit.CenterX, limb.Fit.CenterY, limb.Fit.EquatorialRadius);
        foreach (var stop in PlanetarySharpening.StrengthStops)
        {
            var drawn = layer.MastersDrawnOutsideTheLimb;
            e2e.Click(e2e.Region(h => h is HitResult.ButtonHit { Action: var a } && a == ButtonFor(stop), $"the {stop} button"));
            if (stop != 1)
            {
                await DrawnAsync(e2e, layer, drawn, ct);
            }
            var shown = e2e.Controller.ShownDocument.ShouldNotBeNull().UnstretchedImage;
            var batch = (await Task.Run(() => PlanetarySharpening.Sharpen(onGreen,
                new PlanetarySharpenOptions(CatalogIndex.Jupiter, instant, pupil) { WavelengthsNm = [610, 530, 460], Strength = stop }), ct)).ShouldNotBeNull();
            var balanced = balance.Apply(batch.Sharpened);
            batch.Sharpened.Release();
            for (var c = 0; c < 3; c++)
            {
                var (inside, outside) = Difference(Plane(shown, c), Plane(balanced, c), cx, cy, r);
                TestContext.Current.TestOutputHelper?.WriteLine($"stop {stop}, channel {c}: inside {inside:E2}, outside {outside:E2} of its disk's level");
                inside.ShouldBeLessThan(1e-4, $"channel {c} inside the limb at {stop}");
                outside.ShouldBeLessThan(1e-4, $"channel {c} outside the limb at {stop}");
            }
            balanced.Release();
        }
        onGreen.Release();
        master.Release();
    }

    private static Image Plane(Image image, int c)
    {
        return new Image([image.GetChannelArray(c)], BitDepth.Float32, 1f, 0f, 0f, image.ImageMeta);
    }

    // A master drawn since `drawn` and nothing left to draw: a sharpen the derivation started may still be in flight when a stop is
    // pressed, and its master is not the stop's.
    private static Task DrawnAsync(ViewerE2E e2e, LiveStackPreviewSource layer, int drawn, CancellationToken ct)
    {
        return e2e.PumpUntilAsync(() => layer.MastersDrawnOutsideTheLimb > drawn && !layer.IsBusy, "the master the dials ask for", ct);
    }

    // A Save as a float TIFF, answered by the substitute dialog, pumped until it is written.
    private static async Task<string> SaveAsync(ViewerE2E e2e, string name, CancellationToken ct)
    {
        var path = Path.Combine(e2e.Folder, name);
        e2e.FileDialog.SaveAsync(default!).ReturnsForAnyArgs(path);
        e2e.Controller.SaveImage(withOverlays: false, PngDepth.SixteenBit, ct);
        await e2e.PumpUntilAsync(() => e2e.State.StatusMessage is { } status && (status == $"Saved {name}" || status.StartsWith("Save failed", StringComparison.Ordinal)),
            $"{name} to be saved", ct, untilTimeout: true);
        e2e.State.StatusMessage.ShouldBe($"Saved {name}");
        return path;
    }

    private static double LargestDifference(string a, string b)
    {
        Image.TryReadTiff(a, out var first).ShouldBeTrue();
        Image.TryReadTiff(b, out var second).ShouldBeTrue();
        var (x, y) = (first.ShouldNotBeNull(), second.ShouldNotBeNull());
        x.ChannelCount.ShouldBe(y.ChannelCount);
        var largest = 0.0;
        for (var c = 0; c < x.ChannelCount; c++)
        {
            var p = x.GetChannelSpan(c);
            var q = y.GetChannelSpan(c);
            p.Length.ShouldBe(q.Length);
            for (var i = 0; i < p.Length; i++)
            {
                largest = Math.Max(largest, Math.Abs(p[i] - q[i]));
            }
        }
        x.Release();
        y.Release();
        return largest;
    }

    // Each plane over its own disk's level (the mean inside 0.8 radii), then the largest difference inside the limb and outside it.
    internal static (double Inside, double Outside) Difference(Image a, Image b, double cx, double cy, double r)
    {
        a.Width.ShouldBe(b.Width);
        a.Height.ShouldBe(b.Height);
        var (pa, pb) = (a.GetChannelSpan(0).ToArray(), b.GetChannelSpan(0).ToArray());
        double Level(float[] plane)
        {
            var (sum, n) = (0.0, 0);
            for (var i = 0; i < plane.Length; i++)
            {
                var (x, y) = (i % a.Width, i / a.Width);
                if (Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) < 0.8 * r)
                {
                    (sum, n) = (sum + plane[i], n + 1);
                }
            }
            return sum / n;
        }
        var (la, lb) = (Level(pa), Level(pb));
        var (inside, outside) = (0.0, 0.0);
        for (var i = 0; i < pa.Length; i++)
        {
            var (x, y) = (i % a.Width, i / a.Width);
            var d = Math.Abs((pa[i] / la) - (pb[i] / lb));
            if (Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) < r)
            {
                inside = Math.Max(inside, d);
            }
            else
            {
                outside = Math.Max(outside, d);
            }
        }
        return (inside, outside);
    }

    private static string ButtonFor(double stop)
    {
        return stop == 1 ? "StrengthTruth" : "Strength" + stop.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "");
    }

    private static bool HasButton(ViewerE2E e2e, string action)
    {
        return e2e.Viewer.PaintedRegions().Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);
    }

    // The noisy Jupiter stack written as planetary-stack writes a master: OBJECT names the planet, DATE-OBS and EXPTIME its span.
    internal static async Task<string> OpenMasterAsync(ViewerE2E e2e, CancellationToken ct)
    {
        var meta = new ImageMeta("e2e", PlanetarySharpeningTests.Night - (Exposure / 2), Exposure, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN) with { ObjectName = "Jupiter" };
        var (_, stack) = PlanetarySharpeningTests.NoisyStack(meta: meta);
        var path = Path.Combine(e2e.Folder, "master_jupiter.fits");
        stack.WriteToFitsFile(path);
        stack.Release();
        await e2e.OpenAsync(path, ct);
        await e2e.PumpUntilAsync(() => e2e.State.IsPlanetaryMaster, "the master's sharpening layer", ct);
        return path;
    }
}
