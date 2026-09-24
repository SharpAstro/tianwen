using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>tianwen stack</c>'s master cache serves a master only when it declares the set of frames it is
/// asked for. It used to trust any file under the right name, and a name says which configuration a
/// master serves, never which frames built it: once calibration was grouped by temperature run
/// (#307 <c>#96</c>), a drifting run's master took the name one degree's master already had, so a
/// re-run over an existing <c>masters/</c> folder served the one-degree master and logged the new
/// run's frame count beside it.
/// </summary>
[Collection("Imaging")]
public sealed class StackingMasterCacheTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stackmastercache-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FrameInfo WriteDark(int index, float temperatureC, float level)
    {
        var darkDir = Path.Combine(_dir, "darks");
        Directory.CreateDirectory(darkDir);
        var data = Image.CreateChannelData(1, 8, 8);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                data[0][y, x] = level;
            }
        }
        var meta = new ImageMeta(
            Instrument: "TestCam",
            ExposureStartTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index * 3),
            ExposureDuration: TimeSpan.FromSeconds(120),
            FrameType: FrameType.Dark,
            Telescope: "T",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 135,
            FocusPos: -1,
            Filter: Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: temperatureC,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN,
            Gain: 100);
        var image = new Image(data, BitDepth.Float32, maxValue: 65535, minValue: 0, pedestal: 0, imageMeta: meta);
        var path = Path.Combine(darkDir, $"dark_{index:D3}.fits");
        image.WriteToFitsFile(path);
        image.Release();
        Image.TryReadFitsHeader(path, out var frame).ShouldBeTrue();
        return frame.ShouldNotBeNull();
    }

    /// <summary>One drifting run, 16.6 to 17.6 C in 0.2 C steps, so one set keyed on its median (17 C).
    /// The first two frames sit at 100 and the other four at 200, so a master of the two reads 100 and
    /// the run's median master reads 200.</summary>
    private List<FrameInfo> DriftingRun()
        => [.. Enumerable.Range(0, 6).Select(i => WriteDark(i, 16.6f + (0.2f * i), i < 2 ? 100f : 200f))];

    private async Task<(MasterGroupKey Key, Image Master)> BuildOneAsync(StackingPipeline pipeline, List<FrameInfo> frames, string mastersDir)
    {
        // RunAsync creates the folder before building any master; this calls the stage directly.
        Directory.CreateDirectory(mastersDir);
        var masters = await pipeline.BuildMastersAsync(
            frames, (_, list, token) => MasterFrameBuilder.BuildDarkMasterAsync(list, token), mastersDir,
            TestContext.Current.CancellationToken);
        return masters.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AMasterOfOtherFramesUnderTheSameName_IsRebuilt_NotServed()
    {
        var run = DriftingRun();
        var mastersDir = Path.Combine(_dir, "masters");
        var pipeline = new StackingPipeline(new StackingOptions(DataRoot: _dir, OutputDir: _dir), new XunitLogger(output));

        // The master a previous run built from two of the run's frames: same configuration, same key,
        // same file name.
        var (twoKey, two) = await BuildOneAsync(pipeline, run.Take(2).ToList(), mastersDir);
        two.GetChannelArray(0)[0, 0].ShouldBe(100f);
        var (runKey, whole) = await BuildOneAsync(pipeline, run, mastersDir);

        runKey.Slug().ShouldBe(twoKey.Slug(), "the precondition: both sets file under one name");
        whole.GetChannelArray(0)[0, 0].ShouldBe(200f, "the run's own master, not the cached two-frame one");
        var masterPath = Directory.GetFiles(mastersDir, "master_*.fits").ShouldHaveSingleItem();
        MasterFrameBuilder.ReadInputSet(masterPath).ShouldNotBeNull()
            .ShouldBe((MasterFrameBuilder.InputSetFingerprint(run), run.Count), "the file now declares the whole run");
    }

    [Fact]
    public async Task AMasterThatDeclaresNoInputSet_IsRebuiltOnce_ThenServed()
    {
        var run = DriftingRun();
        var mastersDir = Path.Combine(_dir, "masters");
        var pipeline = new StackingPipeline(new StackingOptions(DataRoot: _dir, OutputDir: _dir), new XunitLogger(output));

        // A master written before masters declared their inputs: the two-frame master, rewritten with
        // no input cards at all, which is what every masters/ folder from an earlier version holds.
        var (_, two) = await BuildOneAsync(pipeline, run.Take(2).ToList(), mastersDir);
        var masterPath = Directory.GetFiles(mastersDir, "master_*.fits").ShouldHaveSingleItem();
        two.WriteToFitsFile(masterPath);
        MasterFrameBuilder.ReadInputSet(masterPath).ShouldBeNull("the precondition: a legacy master states no input set");

        var (_, rebuilt) = await BuildOneAsync(pipeline, run, mastersDir);
        rebuilt.GetChannelArray(0)[0, 0].ShouldBe(200f);

        // And a re-run over the same frames is a cache hit: the file is not written again.
        var written = File.GetLastWriteTimeUtc(masterPath);
        var (_, served) = await BuildOneAsync(pipeline, run, mastersDir);
        served.GetChannelArray(0)[0, 0].ShouldBe(200f);
        File.GetLastWriteTimeUtc(masterPath).ShouldBe(written, "the same set is served from the cache, not rebuilt");
    }
}
