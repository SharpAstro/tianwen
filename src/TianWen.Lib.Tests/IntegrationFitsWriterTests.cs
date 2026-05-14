using System;
using System.Collections.Generic;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class IntegrationFitsWriterTests
{
    private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.IntegrationFitsWriter", name ?? "x", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Image MonoFrame(float baseline, FrameType type = FrameType.Light)
    {
        var arr = new float[3, 5];
        for (var h = 0; h < 3; h++)
            for (var w = 0; w < 5; w++)
                arr[h, w] = baseline + 0.01f * (h * 5 + w);
        var meta = new ImageMeta(
            Instrument: "synthetic", ExposureStartTime: DateTimeOffset.UnixEpoch,
            ExposureDuration: TimeSpan.FromSeconds(60), FrameType: type,
            Telescope: "TestScope", PixelSizeX: 3.76f, PixelSizeY: 3.76f, FocalLength: 400, FocusPos: -1,
            Filter: Filter.Luminance, BinX: 1, BinY: 1, CCDTemperature: -10f,
            SensorType: SensorType.Monochrome, BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown, Latitude: float.NaN, Longitude: float.NaN);
        return new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    [Fact]
    public void Write_NoRejections_OnlyMasterFileWritten()
    {
        var dir = CreateTempDir();
        var masterPath = Path.Combine(dir, "master.fits");
        var result = Integrator.Integrate(
            new List<Image> { MonoFrame(0.1f), MonoFrame(0.2f) },
            new IntegrationOptions(ApplyNormalization: false));

        IntegrationFitsWriter.Write(masterPath, result);

        File.Exists(masterPath).ShouldBeTrue();
        File.Exists(IntegrationFitsWriter.RejectionPathFor(masterPath)).ShouldBeFalse();
    }

    [Fact]
    public void Write_WithRejections_BothFilesWritten()
    {
        var dir = CreateTempDir();
        var masterPath = Path.Combine(dir, "master.fits");

        // Five clean + one cosmic-ray hit -> sigma clip will reject -> non-zero map
        var frames = new List<Image>
        {
            MonoFrame(0.50f), MonoFrame(0.51f), MonoFrame(0.49f),
            MonoFrame(0.50f), MonoFrame(0.498f), MonoFrame(99.0f),
        };
        var result = Integrator.Integrate(frames,
            new IntegrationOptions(Rejector: new SigmaClipRejector(), ApplyNormalization: false));
        result.TotalRejections.ShouldBeGreaterThan(0);

        IntegrationFitsWriter.Write(masterPath, result);

        File.Exists(masterPath).ShouldBeTrue();
        File.Exists(IntegrationFitsWriter.RejectionPathFor(masterPath)).ShouldBeTrue();
    }

    [Fact]
    public void Write_StackHeadersStampedOnMaster()
    {
        var dir = CreateTempDir();
        var masterPath = Path.Combine(dir, "stack.fits");
        var frames = new List<Image>
        {
            MonoFrame(0.50f), MonoFrame(0.51f), MonoFrame(0.49f),
            MonoFrame(0.50f), MonoFrame(99.0f),
        };
        var result = Integrator.Integrate(frames,
            new IntegrationOptions(Rejector: new SigmaClipRejector(), ApplyNormalization: false));

        IntegrationFitsWriter.Write(masterPath, result);

        // Read back and inspect headers via FITS.Lib
        using var bf = new nom.tam.util.BufferedFile(masterPath, FileAccess.Read, FileShare.Read, 1024);
        using var fits = new nom.tam.fits.Fits(bf, false);
        var hdu = fits.ReadHDUHeaderOnly();
        hdu.ShouldNotBeNull();
        hdu.Header.GetIntValue("STACK_N", -1).ShouldBe(5);
        hdu.Header.GetLongValue("REJ_TOT", -1L).ShouldBeGreaterThan(0L);
        hdu.Header.GetDoubleValue("REJ_RATE", -1.0).ShouldBeGreaterThan(0.0);
        // Custom SWCREATE overrides the ImageMeta default (which is empty
        // for our synthetic frames, but extras-supplied header wins regardless).
        hdu.Header.GetStringValue("SWCREATE").ShouldContain("Integrator");
    }

    [Fact]
    public void Write_RoundTripsMasterPixels()
    {
        var dir = CreateTempDir();
        var masterPath = Path.Combine(dir, "master.fits");
        var frames = new List<Image> { MonoFrame(0.3f), MonoFrame(0.4f) };
        var result = Integrator.Integrate(frames, new IntegrationOptions(ApplyNormalization: false));

        IntegrationFitsWriter.Write(masterPath, result);

        Image.TryReadFitsFile(masterPath, out var loaded).ShouldBeTrue();
        loaded.ShouldNotBeNull();
        // Pixel (0, 0): (0.3 + 0.4) / 2 = 0.35
        loaded[0, 0, 0].ShouldBe(0.35f, tolerance: 1e-5f);
    }

    [Fact]
    public void RejectionPathFor_AppendsSuffixCorrectly()
    {
        // Use absolute path with native separators to avoid OS-specific
        // Path.GetDirectoryName / Path.Combine normalization differences.
        var inputA = Path.Combine("data", "master.fits");
        IntegrationFitsWriter.RejectionPathFor(inputA)
            .ShouldBe(Path.Combine("data", "master.rejection.fits"));

        var inputB = Path.Combine("user", "m31.fit");
        IntegrationFitsWriter.RejectionPathFor(inputB)
            .ShouldBe(Path.Combine("user", "m31.rejection.fits"));

        // No-dir case: just the filename.
        IntegrationFitsWriter.RejectionPathFor("solo.fits")
            .ShouldBe("solo.rejection.fits");
    }
}
