using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class FitsFolderFrameSourceTests
{
    // Smallest possible synthetic FITS file — 4x4 mono float32. Constructor
    // signature mirrors the real ImageMeta layout so the only thing the tests
    // vary per call is what we want to assert against.
    private static Image MakeSynthetic(FrameType type, float exposureSec, Filter? filter = null, float ccdTempC = -10f)
    {
        var channel = new float[4, 4];
        for (var h = 0; h < 4; h++)
            for (var w = 0; w < 4; w++)
                channel[h, w] = 0.1f * (h * 4 + w);

        var meta = new ImageMeta(
            Instrument: "synthetic",
            ExposureStartTime: new DateTimeOffset(2026, 5, 14, 21, 0, 0, TimeSpan.Zero),
            ExposureDuration: TimeSpan.FromSeconds(exposureSec),
            FrameType: type,
            Telescope: "TestScope",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 400,
            FocusPos: -1,
            Filter: filter ?? Filter.Luminance,
            BinX: 1,
            BinY: 1,
            CCDTemperature: ccdTempC,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN);

        return new Image([channel], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static string WriteFits(string folder, string name, Image image)
    {
        var path = Path.Combine(folder, name);
        image.WriteToFitsFile(path);
        return path;
    }

    private static string CreateTempDir([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TianWen.FrameSourceTests", name ?? "unnamed", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ConstructingWithMissingFolderThrows()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        Should.Throw<DirectoryNotFoundException>(() => new FitsFolderFrameSource(nonExistent));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EmptyFolderYieldsNothing()
    {
        var dir = CreateTempDir();
        var source = new FitsFolderFrameSource(dir);

        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);

        frames.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnumeratesOnlyFitsFiles()
    {
        var dir = CreateTempDir();
        WriteFits(dir, "light_01.fits", MakeSynthetic(FrameType.Light, 30f));
        WriteFits(dir, "light_02.fits", MakeSynthetic(FrameType.Light, 30f));
        File.WriteAllText(Path.Combine(dir, "log.txt"), "not a fits file");
        File.WriteAllText(Path.Combine(dir, "image.png"), "not a fits file");

        var source = new FitsFolderFrameSource(dir);
        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);

        frames.Count.ShouldBe(2);
        frames.All(f => f.Path.EndsWith(".fits")).ShouldBeTrue();
    }

    [Fact]
    public async Task PopulatesFrameInfoFromHeaders()
    {
        var dir = CreateTempDir();
        WriteFits(dir, "bias.fits", MakeSynthetic(FrameType.Bias, 0.001f, filter: Filter.Luminance, ccdTempC: -10f));
        WriteFits(dir, "dark.fits", MakeSynthetic(FrameType.Dark, 300f, filter: Filter.Luminance, ccdTempC: -10f));
        WriteFits(dir, "flat.fits", MakeSynthetic(FrameType.Flat, 0.5f, filter: Filter.HydrogenAlpha, ccdTempC: -10f));

        var source = new FitsFolderFrameSource(dir);
        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);

        frames.Count.ShouldBe(3);
        var byType = frames.ToDictionary(f => f.FrameType);

        byType[FrameType.Bias].Width.ShouldBe(4);
        byType[FrameType.Bias].Height.ShouldBe(4);
        byType[FrameType.Bias].ChannelCount.ShouldBe(1);
        byType[FrameType.Bias].Meta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(0.001));

        byType[FrameType.Dark].Meta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(300));
        // Filter.HydrogenAlpha's RawName differs after FITS round-trip but the
        // canonical Name + Bandpass survive — those are what downstream grouping
        // by filter cares about.
        byType[FrameType.Flat].Meta.Filter.Name.ShouldBe(Filter.HydrogenAlpha.Name);
        byType[FrameType.Flat].Meta.Filter.Bandpass.ShouldBe(Filter.HydrogenAlpha.Bandpass);
    }

    [Fact]
    public async Task ResultsAreOrderedByPathCaseInsensitive()
    {
        var dir = CreateTempDir();
        // Lexicographic case-insensitive order: a, B, c, Z
        WriteFits(dir, "Z_last.fits", MakeSynthetic(FrameType.Light, 30f));
        WriteFits(dir, "a_first.fits", MakeSynthetic(FrameType.Light, 30f));
        WriteFits(dir, "B_second.fits", MakeSynthetic(FrameType.Light, 30f));
        WriteFits(dir, "c_third.fits", MakeSynthetic(FrameType.Light, 30f));

        var source = new FitsFolderFrameSource(dir);
        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);

        var fileNames = frames.Select(f => Path.GetFileName(f.Path)).ToList();
        fileNames.ShouldBe(["a_first.fits", "B_second.fits", "c_third.fits", "Z_last.fits"]);
    }

    [Fact]
    public async Task RecursiveModeDescendsIntoSubdirectories()
    {
        var dir = CreateTempDir();
        var sub = Path.Combine(dir, "session_01");
        Directory.CreateDirectory(sub);
        WriteFits(dir, "top.fits", MakeSynthetic(FrameType.Light, 30f));
        WriteFits(sub, "nested.fits", MakeSynthetic(FrameType.Light, 30f));

        var topOnly = new FitsFolderFrameSource(dir, recursive: false);
        var ct = TestContext.Current.CancellationToken;
        (await CollectAsync(topOnly.EnumerateAsync(ct), ct)).Count.ShouldBe(1);

        var recursive = new FitsFolderFrameSource(dir, recursive: true);
        (await CollectAsync(recursive.EnumerateAsync(ct), ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task LoadFullAsyncRoundTripsTheImage()
    {
        var dir = CreateTempDir();
        var original = MakeSynthetic(FrameType.Light, 60f);
        WriteFits(dir, "frame.fits", original);

        var source = new FitsFolderFrameSource(dir);
        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);
        frames.Count.ShouldBe(1);

        var loaded = await frames[0].LoadFullAsync(ct);
        loaded.Width.ShouldBe(original.Width);
        loaded.Height.ShouldBe(original.Height);
        loaded.ChannelCount.ShouldBe(original.ChannelCount);
        // Spot-check one pixel — FITS round-trip preserves float32 exactly
        loaded[0, 1, 1].ShouldBe(original[0, 1, 1], tolerance: 1e-6f);
    }

    [Fact]
    public async Task LoadFullAsyncThrowsIfFileDisappeared()
    {
        var dir = CreateTempDir();
        WriteFits(dir, "frame.fits", MakeSynthetic(FrameType.Light, 60f));

        var source = new FitsFolderFrameSource(dir);
        var ct = TestContext.Current.CancellationToken;
        var frames = await CollectAsync(source.EnumerateAsync(ct), ct);
        frames.Count.ShouldBe(1);

        File.Delete(frames[0].Path);

        await Should.ThrowAsync<IOException>(async () => await frames[0].LoadFullAsync(ct));
    }

    [Fact]
    public async Task CancellationStopsEnumeration()
    {
        var dir = CreateTempDir();
        for (var i = 0; i < 5; i++)
        {
            WriteFits(dir, $"frame_{i:D2}.fits", MakeSynthetic(FrameType.Light, 30f));
        }

        var source = new FitsFolderFrameSource(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.EnumerateAsync(cts.Token))
            {
                // never reached
            }
        });
    }

    private static async Task<System.Collections.Generic.List<FrameInfo>> CollectAsync(System.Collections.Generic.IAsyncEnumerable<FrameInfo> source, CancellationToken ct)
    {
        var list = new System.Collections.Generic.List<FrameInfo>();
        await foreach (var item in source.WithCancellation(ct))
        {
            list.Add(item);
        }
        return list;
    }
}
