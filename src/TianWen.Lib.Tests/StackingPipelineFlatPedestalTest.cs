using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end pin of the stack path's flat-pedestal wiring: dark-flat masters are built, pooled
/// with the biases as pedestal candidates, and the exposure-matched dark-flat wins (the DSS
/// calibration model's Flat column). Runs the REAL <see cref="StackingPipeline"/> over a
/// calibration-only input tree; masters are built before the lights check, so no registration or
/// integration runs and the whole test is header + median work.
///
/// <para>The assertion distinguishes WHICH pedestal came off, not merely that one did: the flats
/// carry a thermal term (400 ADU, a warm sensor or a narrowband-length flat) on top of the bias
/// (788 ADU). Subtracting the matched dark-flat (bias + thermal) recovers the true 0.80 corner
/// falloff exactly; subtracting only the bias leaves the thermal in and the falloff reads 0.8020.
/// The tolerance sits between the two.</para>
/// </summary>
[Collection("Imaging")]
public class StackingPipelineFlatPedestalTest(ITestOutputHelper output)
{
    private const int Size = 32;
    private const float Bias = 788f;
    private const float Thermal = 400f;
    private const float Peak = 38912f;
    private const float TrueFalloff = 0.80f;

    [Fact]
    public async Task FlatMaster_PedestalledByTheExposureMatchedDarkFlat_NotTheBias()
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();

        // Two of each so every group clears BuildMastersAsync's >= 2-frame floor.
        for (var i = 0; i < 2; i++)
        {
            WriteUniform(workspace.RootDir, "BIAS", $"bias_{i:D2}.fits", FrameType.Bias, 0, Bias);
            WriteUniform(workspace.RootDir, "DARKFLAT", $"darkflat_{i:D2}.fits", FrameType.DarkFlat, 1, Bias + Thermal);
            WriteFlat(workspace.RootDir, $"flat_{i:D2}.fits");
        }

        var options = new StackingOptions(DataRoot: workspace.RootDir, OutputDir: workspace.OutputDir);
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);
        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }
        results.ShouldBeEmpty("no lights were supplied; only the master-building stage should run");

        // The suffix encodes the pedestal-candidate KINDS ("_ps" = both present), which is what
        // invalidates a stale bias-pedestalled cache when dark-flats appear in an archive.
        var flatMaster = Directory.GetFiles(workspace.OutputDir, "master_flat*.fits", SearchOption.AllDirectories)
            .ShouldHaveSingleItem();
        Path.GetFileNameWithoutExtension(flatMaster).ShouldEndWith("_ps");

        Image.TryReadFitsFile(flatMaster, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        // 0.80000 with the dark-flat pedestal, 0.80204 with the bias one: the tolerance separates
        // the two choices, so a regression to bias-first (or to no pedestal, 0.80417) fails here.
        (master[0, 0, 0] / master[0, Size / 2, Size / 2]).ShouldBe(TrueFalloff, tolerance: 5e-4);
    }

    /// <summary>A flat with the true falloff recorded on top of bias + thermal: the top-left
    /// quadrant sits at 80% of the centre's illumination.</summary>
    private static void WriteFlat(string rootDir, string fileName)
    {
        var pixels = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var signal = y < Size / 2 && x < Size / 2 ? Peak * TrueFalloff : Peak;
                pixels[y, x] = Bias + Thermal + signal;
            }
        }
        Write(rootDir, "FLAT", fileName, FrameType.Flat, expoSec: 1, pixels);
    }

    private static void WriteUniform(string rootDir, string subDir, string fileName, FrameType type, double expoSec, float level)
    {
        var pixels = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                pixels[y, x] = level;
            }
        }
        Write(rootDir, subDir, fileName, type, expoSec, pixels);
    }

    private static void Write(string rootDir, string subDir, string fileName, FrameType type, double expoSec, float[,] pixels)
    {
        var meta = new ImageMeta
        {
            Instrument = "Synth",
            ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero),
            ExposureDuration = TimeSpan.FromSeconds(expoSec),
            FrameType = type,
            PixelSizeX = 3.76f,
            PixelSizeY = 3.76f,
            FocalLength = 1000,
            BinX = 1,
            BinY = 1,
            CCDTemperature = -5f,
            SensorType = SensorType.Monochrome,
            Gain = 100,
            Offset = 25,
        };
        var img = new Image(
            data: [pixels],
            bitDepth: BitDepth.Int16,
            maxValue: pixels.Cast<float>().Max(),
            minValue: pixels.Cast<float>().Min(),
            pedestal: 0f,
            imageMeta: meta);
        var dir = Path.Combine(rootDir, subDir);
        Directory.CreateDirectory(dir);
        img.WriteToFitsFile(Path.Combine(dir, fileName));
    }
}
