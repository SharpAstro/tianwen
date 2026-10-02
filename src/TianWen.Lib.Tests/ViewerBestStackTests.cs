using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The viewer's Best stack (#1159) end to end through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>): a SER opened, Shift+K,
/// and the capture is stacked by the routine <c>planetary-stack</c> runs, both masters written beside it under that verb's names, the
/// sharpened one opened in its place.
/// </summary>
[Collection("Viewer")]
public class ViewerBestStackTests
{
    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ShiftKStacksTheWholeCaptureWritesBothMastersAndOpensTheSharpenedOne(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = WriteCapture(Path.Combine(e2e.Folder, "2024-12-15-1256_7-Jupiter.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        e2e.Key(InputKey.K, InputModifier.Shift);
        await e2e.PumpUntilAsync(() => e2e.IsShowing(Path.Combine(e2e.Folder, "master_2024-12-15-1256_7-Jupiter_sharpened.fits")),
            "the best stack's sharpened master to open", ct);

        // What the run did is said once its master is on screen (the open and its upload each clear the status line first).
        await e2e.PumpUntilAsync(() => e2e.State.StatusMessage?.StartsWith("Best stack:", StringComparison.Ordinal) == true,
            "the best stack's note", ct);

        File.Exists(Path.Combine(e2e.Folder, "master_2024-12-15-1256_7-Jupiter.fits")).ShouldBeTrue();
        e2e.State.BestStackProgress.ShouldBeNull();
        e2e.State.SequencePath.ShouldBeNull();
        // No telescope given, so the sharpening is the preset's, and the note says so.
        e2e.State.StatusMessage.ShouldNotBeNull().ShouldContain("PlanetaryDefault");
        // The master names its planet, and opens in the stretch planetary-stack's preview is rendered with, never the deep-sky
        // auto-stretch: the same uniforms, so the viewer shows what the PNG shows.
        var document = e2e.Controller.Document.ShouldNotBeNull();
        document.UnstretchedImage.ImageMeta.ObjectName.ShouldBe("Jupiter");
        e2e.State.StretchMode.ShouldBe(StretchMode.Planetary);
        var shown = document.ComputeStretchUniforms(e2e.State.StretchMode, e2e.State.StretchParameters);
        var preview = document.UnstretchedImage.ComputePlanetaryStretchUniforms();
        (shown.Mode, shown.Pedestal, shown.Rescale, shown.Midtones).ShouldBe((preview.Mode, preview.Pedestal, preview.Rescale, preview.Midtones));
    }

    [Theory(Timeout = 180_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ACaptureWhoseNameGivesNoPlanetTakesThePlanetChosenInThePanel(float dpi)
    {
        // A twin named "calibrated" gave the run no planet, so its sharpening was the preset's (reported 2026-10-02).
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var capture = WriteCapture(Path.Combine(e2e.Folder, "calibrated.ser"));
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        e2e.State.PlanetaryBody = CatalogIndex.Jupiter;
        e2e.Key(InputKey.K, InputModifier.Shift);
        await e2e.PumpUntilAsync(() => e2e.IsShowing(Path.Combine(e2e.Folder, "master_calibrated_sharpened.fits")),
            "the best stack's sharpened master to open", ct);

        e2e.Controller.Document.ShouldNotBeNull().UnstretchedImage.ImageMeta.ObjectName.ShouldBe("Jupiter");
        e2e.State.StretchMode.ShouldBe(StretchMode.Planetary);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AFrameWhoseObjectNamesAPlanetOpensInThePlanetaryStretchBetweenDeepSkyFrames(float dpi)
    {
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        var deepSky = e2e.WriteColourFits("deep-sky.fits");
        await e2e.OpenAsync(deepSky, ct);
        e2e.State.StretchMode.ShouldNotBe(StretchMode.None, "a deep-sky frame takes the auto-stretch");

        var plane = new float[64, 64];
        plane[32, 32] = 0.8f;
        var planetary = Path.Combine(e2e.Folder, "planetary.fits");
        new Image([plane], BitDepth.Float32, 0.8f, 0f, 0f,
            new ImageMeta("e2e", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN, ObjectName: "Jupiter")).WriteToFitsFile(planetary);
        await e2e.OpenAsync(planetary, ct);
        e2e.State.StretchMode.ShouldBe(StretchMode.Planetary, "a planet's frame opens in the planetary stretch");

        // T to linear and back returns to the planetary stretch, not the deep-sky default.
        e2e.Key(InputKey.T);
        e2e.State.StretchMode.ShouldBe(StretchMode.None);
        e2e.Key(InputKey.T);
        e2e.State.StretchMode.ShouldBe(StretchMode.Planetary);

        // And a deep-sky frame after it takes the auto-stretch again.
        await e2e.OpenAsync(e2e.WriteColourFits("deep-sky-2.fits", level: 7f), ct);
        e2e.State.StretchMode.ShouldBe(ViewerActions.DefaultStretchMode);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ACaptureWhoseHeaderNamesItsTelescopeGivesTheBestStackThatTelescope(float dpi)
    {
        // A TianWen recording says in its header which telescope took it (#1179), and its best stack needs no telescope set by hand.
        using var e2e = ViewerE2E.Start(dpi);
        var ct = TestContext.Current.CancellationToken;
        e2e.State.PlanetaryApertureMm = null;
        e2e.State.PlanetaryDesign = TianWen.Lib.Devices.OpticalDesign.Refractor;
        var capture = WriteCapture(Path.Combine(e2e.Folder, "Jupiter_Red_2024-12-15T12_56_44_OTA1.ser"), telescope: "254 mm f/4.7 Newtonian, Test OTA");
        e2e.Host.HandleDropFile(capture);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == capture, "the capture to open", ct);

        (e2e.State.PlanetaryApertureMm, e2e.State.PlanetaryDesign).ShouldBe(((int?)254, TianWen.Lib.Devices.OpticalDesign.Newtonian));

        // A capture that names none leaves the panel's own.
        var other = WriteCapture(Path.Combine(e2e.Folder, "calibrated.ser"));
        e2e.Host.HandleDropFile(other);
        await e2e.PumpUntilAsync(() => e2e.State.SequencePath == other, "the second capture to open", ct);
        e2e.State.PlanetaryApertureMm.ShouldBe(254);
    }

    // A short Jupiter capture: a textured disk wandering a pixel or two, 8 bits, a frame every 10 ms.
    internal static string WriteCapture(string path, string telescope = "")
    {
        const int n = 96, frames = 32;
        var random = new Random(7);
        var start = new DateTimeOffset(2024, 12, 15, 12, 56, 44, TimeSpan.Zero);
        using (var writer = new SerWriter(path, n, n, SerColorId.Mono, 8, telescope: telescope))
        {
            var frame = new byte[n * n];
            for (var i = 0; i < frames; i++)
            {
                var (cx, cy) = (48 + (random.NextDouble() * 3) - 1.5, 48 + (random.NextDouble() * 3) - 1.5);
                for (var y = 0; y < n; y++)
                {
                    for (var x = 0; x < n; x++)
                    {
                        var (dx, dy) = (x - cx, y - cy);
                        var v = (dx * dx) + (dy * dy) < 26 * 26
                            ? 0.5 + (0.25 * Math.Sin(x * 0.6) * Math.Cos(y * 0.55)) + (0.12 * Math.Sin((x - y) * 0.3))
                            : 0.03;
                        frame[(y * n) + x] = (byte)Math.Clamp(v * 255, 0, 255);
                    }
                }
                writer.AppendFrame(frame, start.AddMilliseconds(10 * i));
            }
        }
        return path;
    }
}
