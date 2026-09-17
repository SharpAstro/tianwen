using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// An integrated master's PEDESTAL and black point (<c>MinValue</c>) are what the integration made
/// them, whichever strategy built it: zero when the frames were normalised, and the frames' own
/// pedestal, in the master's units, when they were not.
/// </summary>
/// <remarks>
/// <para>The normaliser maps each frame's pedestal to zero, <c>(in - pedestal) * scale</c>, so a master
/// of normalised frames has none. Five strategies copied the FIRST SOURCE FRAME's pedestal onto the
/// master regardless, and <see cref="MasterPostProcessor"/> then wrote it, scaled, as <c>PEDESTAL</c>,
/// which the display stretch subtracts before its curve. Both drizzle strategies made the opposite
/// mistake with normalisation OFF, hard-coding zero over frames divided by their full scale but never
/// offset. Every case here is a strategy's own output, not <see cref="IntegratedMaster"/> called
/// directly, because the defect was at the call sites: the labeller cannot know whether the frames it
/// is handed were normalised.</para>
/// <para>The black point is the other half. <c>MinValue</c> is where the display takes its pedestal
/// from (<see cref="Image.GetPedestralMedianAndMADScaledToUnit"/>), and a normalised master's black is
/// its zero, not its darkest pixel: on the real 10P/Tempel 2 drizzle master the darkest finite pixel
/// sits at 69 percent of the sky median (0.00488 against 0.00712), so labelling the observed minimum
/// would move the black point most of the way up to the sky. The comet composite did exactly that.</para>
/// </remarks>
[Collection("Stacking")]
public class IntegratedMasterLabelTests
{
    private const int Size = 16;
    private const float UnitPedestal = 0.1f;
    private const float AduPedestal = 100f;
    private const float AduFullScale = 4096f;

    public static TheoryData<IntegrationStrategyKind, bool> WarpedFrameStrategies => new()
    {
        { IntegrationStrategyKind.InRamAllFrames, true },
        { IntegrationStrategyKind.InRamAllFrames, false },
        { IntegrationStrategyKind.ChunkedTwoPass, true },
        { IntegrationStrategyKind.ChunkedTwoPass, false },
        { IntegrationStrategyKind.Float16Staged, true },
        { IntegrationStrategyKind.Float16Staged, false },
        { IntegrationStrategyKind.FootprintStaged, true },
        { IntegrationStrategyKind.FootprintStaged, false },
    };

    [Theory]
    [MemberData(nameof(WarpedFrameStrategies))]
    public async Task AWarpedFrameStrategysMasterHasNoPedestalOnceItsFramesWereNormalised(IntegrationStrategyKind kind, bool normalise)
    {
        var ct = TestContext.Current.CancellationToken;
        var staging = Directory.CreateTempSubdirectory("IntegratedMasterLabelTests_");
        try
        {
            IIntegrationStrategy strategy = kind switch
            {
                IntegrationStrategyKind.InRamAllFrames => new InRamAllFramesStrategy(),
                IntegrationStrategyKind.ChunkedTwoPass => new ChunkedTwoPassStrategy(),
                IntegrationStrategyKind.Float16Staged => new Float16StagedStrategy(),
                IntegrationStrategyKind.FootprintStaged => new FootprintStagedStrategy(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

            var frames = new List<Image>();
            for (var f = 0; f < 3; f++)
            {
                frames.Add(new Image([UnitPlane(0.45f + f * 0.05f)], BitDepth.Float32, maxValue: 1f, minValue: 0f,
                    pedestal: UnitPedestal, imageMeta: new ImageMeta { Instrument = "synth-label", SensorType = SensorType.Monochrome }));
            }

            var job = new IntegrationJob(
                WarpedFrames: token => Enumerate(frames, token),
                ExpectedFrameCount: frames.Count,
                Options: new IntegrationOptions(ApplyNormalization: normalise),
                StagingDir: staging.FullName,
                StatsRect: Rectangle.Empty);

            var master = (await strategy.RunAsync(job, ct)).Master;

            ShouldCarryTheIntegrationsZero(master, normalise ? 0f : UnitPedestal, $"{kind}, normalise={normalise}");
        }
        finally
        {
            try { staging.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheTileStrategysMasterHasNoPedestalOnceItsFramesWereNormalised(bool normalise)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("IntegratedMasterLabelTests_");
        try
        {
            var sources = new List<RawLightSource>();
            for (var f = 0; f < 3; f++)
            {
                var path = Path.Combine(dir.FullName, $"mono{f}.fits");
                new Image([UnitPlane(0.45f + f * 0.05f)], BitDepth.Float32, maxValue: 1f, minValue: 0f,
                    pedestal: UnitPedestal, imageMeta: new ImageMeta { Instrument = "synth-label", SensorType = SensorType.Monochrome }).WriteToFitsFile(path);
                sources.Add(new RawLightSource(path, Matrix3x2.Identity));
            }

            var job = new IntegrationJob(
                WarpedFrames: _ => Enumerate(new List<Image>(), CancellationToken.None),
                ExpectedFrameCount: sources.Count,
                Options: new IntegrationOptions(ApplyNormalization: normalise),
                StagingDir: dir.FullName,
                StatsRect: Rectangle.Empty,
                RawLightSources: sources,
                Calibrator: new Calibrator(),
                DebayerAlgorithm: DebayerAlgorithm.BilinearMono,
                CanvasWidth: Size,
                CanvasHeight: Size);

            var master = (await new TilePipelinedStrategy().RunAsync(job, ct)).Master;

            ShouldCarryTheIntegrationsZero(master, normalise ? 0f : UnitPedestal, $"TilePipelined, normalise={normalise}");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ADrizzleMastersPedestalIsZeroWhenNormalisedAndTheFramesPedestalInUnitsWhenNot(bool tiled, bool normalise)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("IntegratedMasterLabelTests_");
        try
        {
            const int frameCount = 4;
            var frames = new List<Image>(frameCount);
            var sources = new List<RawLightSource>(frameCount);
            for (var f = 0; f < frameCount; f++)
            {
                var frame = BayerFrame(f);
                frames.Add(frame);
                var path = Path.Combine(dir.FullName, $"bayer{f}.fits");
                frame.WriteToFitsFile(path);
                sources.Add(new RawLightSource(path, Matrix3x2.Identity));
            }

            async IAsyncEnumerable<RawBayerFrame> RawFrames([EnumeratorCancellation] CancellationToken token)
            {
                foreach (var frame in frames)
                {
                    token.ThrowIfCancellationRequested();
                    yield return new RawBayerFrame(frame, Matrix3x2.Identity);
                    await Task.Yield();
                }
            }

            var job = new IntegrationJob(
                WarpedFrames: _ => Enumerate(new List<Image>(), CancellationToken.None),
                ExpectedFrameCount: frameCount,
                Options: new IntegrationOptions(ApplyNormalization: normalise),
                StagingDir: dir.FullName,
                StatsRect: Rectangle.Empty,
                RawLightSources: tiled ? sources : null,
                Calibrator: tiled ? new Calibrator() : null,
                CanvasWidth: Size,
                CanvasHeight: Size,
                DrizzleOptions: new DrizzleOptions(),
                RawBayerFrames: tiled ? null : RawFrames);

            IIntegrationStrategy strategy = tiled
                ? new TilePipelinedDrizzleStrategy(minFrameCount: 1)
                : new DrizzleStrategy(minFrameCount: 1);
            var master = (await strategy.RunAsync(job, ct)).Master;

            // Unnormalised, the drizzle divides every sample by the frame's full scale and subtracts
            // nothing, so the pedestal is still in the data, divided by the same number.
            ShouldCarryTheIntegrationsZero(master, normalise ? 0f : AduPedestal / AduFullScale,
                $"{(tiled ? "TilePipelinedDrizzle" : "BayerDrizzle")}, normalise={normalise}");
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void TheCompositeStatesItsLayersBlackPointNotItsDarkestPixel()
    {
        // A star layer shaped like the real drizzle master: sky at 0.5 after normalisation, its
        // darkest finite pixel at 69 percent of that sky, NaN around the canvas, one star far above.
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = UnitPlane(0.5f);
            planes[c][0, 0] = float.NaN;
            planes[c][7, 9] = 0.5f * 0.69f;
            planes[c][4, 4] = 40f;
        }
        var layer = IntegratedMaster.Labelled(
            new Image(planes, BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { Instrument = "synth-label", SensorType = SensorType.Monochrome }), normalised: true);

        var withBody = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            withBody[c] = (float[,])layer.GetChannelArray(c).Clone();
            withBody[c][10, 10] += 25f;
        }

        var composite = IntegratedMaster.Composite(layer, withBody);

        composite.MinValue.ShouldBe(layer.MinValue, "the composite's black point is its layer's zero, not its darkest pixel (0.345)");
        composite.Pedestal.ShouldBe(layer.Pedestal, "and its pedestal is the layer's");
        composite.MaxValue.ShouldBe(40f, "its peak is still observed, NaN skipped");
    }

    private static void ShouldCarryTheIntegrationsZero(Image master, float expectedPedestal, string what)
    {
        master.Pedestal.ShouldBe(expectedPedestal, 1e-6f, $"{what}: the master's pedestal");
        master.MinValue.ShouldBe(0f, $"{what}: the master's black point is the integration's zero");
    }

    /// <summary>A sky plane with a small deterministic ripple, so a median and a MAD exist.</summary>
    private static float[,] UnitPlane(float sky)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = sky + ((x * 7 + y * 3) % 5 - 2) * 0.01f;
            }
        }
        return plane;
    }

    /// <summary>An RGGB frame in ADU: a sky per colour above a pedestal, labelled with its full scale.</summary>
    private static Image BayerFrame(int index)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var sky = ((y & 1), (x & 1)) switch
                {
                    (0, 0) => 800f,
                    (1, 1) => 600f,
                    _ => 1000f,
                };
                plane[y, x] = AduPedestal + sky + index * 20f + ((x * 7 + y * 3) % 5 - 2) * 4f;
            }
        }
        var meta = new ImageMeta { Instrument = "synth-label", SensorType = SensorType.RGGB };
        return new Image([plane], BitDepth.Float32, maxValue: AduFullScale, minValue: 0f, pedestal: AduPedestal, imageMeta: meta);
    }

    private static async IAsyncEnumerable<Image> Enumerate(List<Image> frames, [EnumeratorCancellation] CancellationToken token)
    {
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }
    }
}
