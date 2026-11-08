using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stacker places red and blue on green per frame (#1993): synthetic RGGB lights whose red and blue are shifted OPPOSITELY from
/// green by an amount that changes frame to frame (as dispersion does with altitude), stacked through the whole pipeline, then the
/// master's own stars read with <see cref="Image.ChannelOffsets"/>. With alignment on, each colour's centroid sits on green's to
/// 0.03 px on the debayered paths and on the Bayer drizzle paths; with it off it sits where the average shift puts it, well outside.
/// </summary>
/// <remarks>
/// Synthetic because the truth has to be exact: every colour's star is rendered at the sky position plus the frame's dither plus that
/// colour's own shift, and the photosite then takes the colour its cell gives it, so the shift lives in the SOURCE pixels as the
/// optics put it. Each strategy runs the same lights; the bound is the same for all of them.
/// </remarks>
[Collection("Imaging")]
public class ColourAlignmentStackingTests(ITestOutputHelper output)
{
    private const int Size = 256;
    private const int FrameCount = 20;
    private const int StarCount = 48;
    private const float Sigma = 2.0f;
    private const float Noise = 4f;
    private const double Tolerance = 0.03;

    // The shifts of one unit of dispersion; each frame scales them (below). Blue opposite to red, as dispersion does.
    private static readonly (double Dx, double Dy) RedUnit = (0.28, 0.15);
    private static readonly (double Dx, double Dy) BlueUnit = (-0.15, -0.08);

    // (level, gain) per colour R G B, as a sensor's differ.
    private static readonly (float Level, float Gain)[] Colours = [(1100f, 1.00f), (1500f, 0.85f), (900f, 0.55f)];

    /// <summary>The dispersion at frame <paramref name="f"/>, as a multiple of the unit: it grows across the session, as a target sinks.</summary>
    private static double Dispersion(int f) => 0.35 + (0.04 * f);

    /// <summary>Stars scattered with a minimum spacing (a regular grid defeats the quad match), each its own brightness.</summary>
    private static List<(double X, double Y, double Amplitude)> StarField()
    {
        var rnd = new Random(1993);
        var stars = new List<(double X, double Y, double Amplitude)>();
        while (stars.Count < StarCount)
        {
            var x = 28 + (rnd.NextDouble() * (Size - 56));
            var y = 28 + (rnd.NextDouble() * (Size - 56));
            if (stars.TrueForAll(s => ((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y)) > 22d * 22d))
            {
                stars.Add((x, y, 5000d + (rnd.NextDouble() * 8000d)));
            }
        }

        return stars;
    }

    private static double Gaussian(Random rnd)
        => Math.Sqrt(-2d * Math.Log(1d - rnd.NextDouble())) * Math.Cos(2d * Math.PI * rnd.NextDouble());

    private static void WriteLights(string lightsDir)
    {
        var stars = StarField();
        var dither = new Random(7);
        var pattern = SensorType.RGGB.GetBayerPatternMatrix(0, 0);
        for (var f = 0; f < FrameCount; f++)
        {
            var (ditherX, ditherY) = f == 0 ? (0d, 0d) : ((dither.NextDouble() * 6d) - 3d, (dither.NextDouble() * 6d) - 3d);
            var dispersion = Dispersion(f);
            var rnd = new Random(100 + f);
            var data = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var colour = pattern[y & 1, x & 1];
                    var (shiftX, shiftY) = colour switch
                    {
                        0 => (RedUnit.Dx * dispersion, RedUnit.Dy * dispersion),
                        2 => (BlueUnit.Dx * dispersion, BlueUnit.Dy * dispersion),
                        _ => (0d, 0d),
                    };
                    var (level, gain) = Colours[colour];
                    var value = (double)level;
                    foreach (var (sx, sy, amplitude) in stars)
                    {
                        var dx = x - (sx + ditherX + shiftX);
                        var dy = y - (sy + ditherY + shiftY);
                        value += amplitude * gain * Math.Exp(-((dx * dx) + (dy * dy)) / (2d * Sigma * Sigma));
                    }

                    data[y, x] = (float)Math.Round(value + (Noise * Gaussian(rnd)));
                }
            }

            var meta = new ImageMeta
            {
                Instrument = "SynthColourAlign",
                ExposureStartTime = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.Zero).AddSeconds(f),
                ExposureDuration = TimeSpan.FromSeconds(1),
                FrameType = FrameType.Light,
                PixelSizeX = 3.76f,
                PixelSizeY = 3.76f,
                FocalLength = 1000,
                BinX = 1,
                BinY = 1,
                CCDTemperature = -5f,
                SensorType = SensorType.RGGB,
                ObjectName = "SynthColourAlign",
                Gain = 100,
                Offset = 25,
            };
            new Image([data], BitDepth.Int16, 16383f, 0f, 0f, meta).WriteToFitsFile(Path.Combine(lightsDir, $"light_{f:D2}.fits"));
        }
    }

    /// <summary>Stacks the lights and reads the master's colours on its own stars (green's detection, every colour read on them).</summary>
    private async Task<ChannelOffsetsResult> StackAndReadAsync(IntegrationStrategyKind? forced, bool disableDrizzle, bool alignColours)
    {
        var ct = TestContext.Current.CancellationToken;
        using var workspace = new TempStackingWorkspace();
        WriteLights(workspace.LightsDir);

        var options = new StackingOptions(
            DataRoot: workspace.RootDir,
            OutputDir: workspace.OutputDir,
            ForcedStrategy: forced,
            DisableBayerDrizzle: disableDrizzle,
            DrizzleOptions: new DrizzleOptions(MinFrameCount: 6),
            RejectCloud: false,
            AlignColours: alignColours);
        var pipeline = new StackingPipeline(options, new XunitLogger(output), catalogDb: null);

        var results = new List<GroupResult>();
        await foreach (var r in pipeline.RunAsync(ct))
        {
            results.Add(r);
        }

        results.Count.ShouldBe(1);
        var result = results[0];
        result.SkipReason.ShouldBeEmpty();
        result.FramesMatched.ShouldBeGreaterThanOrEqualTo(FrameCount - 2);
        result.MasterFitsPath.ShouldNotBeNull();

        // The autocrop: the covered part of the canvas, with no NaN margin for the detection to trip over.
        var autocrop = Path.Combine(
            Path.GetDirectoryName(result.MasterFitsPath) ?? workspace.OutputDir,
            Path.GetFileNameWithoutExtension(result.MasterFitsPath) + "_autocrop.fits");
        Image.TryReadFitsFile(File.Exists(autocrop) ? autocrop : result.MasterFitsPath, out var master).ShouldBeTrue();
        master.ShouldNotBeNull();
        master.ChannelCount.ShouldBe(3);

        var stars = await master.FindStarsAsync(channel: 1, snrMin: 20f, minStars: 15, cancellationToken: ct);
        var read = master.ChannelOffsets(stars);
        output.WriteLine($"master stars {stars.Count}, used {read.StarsUsed}; red ({read.Red.Dx:+0.000;-0.000},{read.Red.Dy:+0.000;-0.000}) blue ({read.Blue.Dx:+0.000;-0.000},{read.Blue.Dy:+0.000;-0.000})");
        read.IsMeasured.ShouldBeTrue();
        read.StarsUsed.ShouldBeGreaterThanOrEqualTo(15);
        return read;
    }

    public static TheoryData<IntegrationStrategyKind?, bool, string> Paths => new()
    {
        // The debayered paths: the in-RAM integrator behind the warp producer, and the strip-warping TilePipelined.
        { IntegrationStrategyKind.InRamAllFrames, true, "debayered, warped by the producer" },
        { IntegrationStrategyKind.TilePipelined, true, "debayered, warped per strip" },
        // The Bayer drizzle paths: the streaming one, and the strip-pipelined one.
        { IntegrationStrategyKind.BayerDrizzle, false, "drizzle" },
        { IntegrationStrategyKind.TilePipelinedDrizzle, false, "drizzle, per strip" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task RedAndBlueLandOnGreenWithinThreeHundredthsOfAPixelWhenAligned(IntegrationStrategyKind? kind, bool disableDrizzle, string path)
    {
        output.WriteLine(path);
        var read = await StackAndReadAsync(kind, disableDrizzle, alignColours: true);

        read.Red.Dx.ShouldBe(0d, Tolerance);
        read.Red.Dy.ShouldBe(0d, Tolerance);
        read.Blue.Dx.ShouldBe(0d, Tolerance);
        read.Blue.Dy.ShouldBe(0d, Tolerance);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task RedAndBlueStayOffGreenWhenAlignmentIsOff(IntegrationStrategyKind? kind, bool disableDrizzle, string path)
    {
        output.WriteLine(path);
        var read = await StackAndReadAsync(kind, disableDrizzle, alignColours: false);

        // The frames' own shifts average to about 0.20 px (red, x) and -0.11 px (blue, x): far outside the bound.
        Math.Abs(read.Red.Dx).ShouldBeGreaterThan(Tolerance * 3);
        Math.Abs(read.Blue.Dx).ShouldBeGreaterThan(Tolerance * 2);
    }
}
