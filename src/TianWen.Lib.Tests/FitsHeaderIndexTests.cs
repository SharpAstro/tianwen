using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The archive scan's header index: an unchanged file is parsed from the header the last scan stored,
/// through the same parse as a read, and a changed one is read again.
/// </summary>
[Collection("Imaging")]
public sealed class FitsHeaderIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "headerindex-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteLight(string folder, int index, string target = "M 42")
    {
        var dir = Path.Combine(_dir, "archive", folder);
        Directory.CreateDirectory(dir);
        var data = Image.CreateChannelData(1, 6, 8);
        var meta = new ImageMeta(
            Instrument: "TestCam",
            ExposureStartTime: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
            ExposureDuration: TimeSpan.FromSeconds(60),
            FrameType: FrameType.Light,
            Telescope: "T",
            PixelSizeX: 3.76f,
            PixelSizeY: 3.76f,
            FocalLength: 135,
            FocusPos: -1,
            Filter: Filter.FromName("IDAS LPS D3"),
            BinX: 1,
            BinY: 1,
            CCDTemperature: -10f,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN,
            Gain: 121,
            Offset: 13,
            ObjectName: target);
        var image = new Image(data, BitDepth.Int16, maxValue: 65535, minValue: 0, pedestal: 0, imageMeta: meta);
        var path = Path.Combine(dir, $"light_{index:D3}.fits");
        image.WriteToFitsFile(path);
        image.Release();
        return path;
    }

    private FitsHeaderIndex Index() => FitsHeaderIndex.Load(Path.Combine(_dir, "index", "root.headers"));

    [Fact]
    public void AStoredHeader_ParsesToTheFrameTheFileReadGives()
    {
        var path = WriteLight("night", 0);
        Image.TryReadFitsHeader(path, out var read, out var replayable).ShouldBeTrue();
        replayable.ShouldBeGreaterThan(0, "a primary-HDU image in a plain file replays");
        (replayable % 2880).ShouldBe(0, "a header is whole FITS blocks");

        var header = File.ReadAllBytes(path)[..replayable];
        Image.TryReadFitsHeaderFromBytes(path, header, out var replayed).ShouldBeTrue();
        FitsHeaderIndex.SameFrame(replayed, read).ShouldBeTrue();
        replayed.Meta.ObjectName.ShouldBe("M 42");
        replayed.Width.ShouldBe(8);
        replayed.Height.ShouldBe(6);
    }

    [Fact]
    public async Task ARescan_IsServedFromTheIndex_AndAChangedFileIsReadAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var paths = Enumerable.Range(0, 3).Select(i => WriteLight("night", i)).ToArray();
        var root = Path.Combine(_dir, "archive");

        var first = Index();
        var scanned = await new FitsFolderFrameSource(root, recursive: true) { HeaderIndex = first }.EnumerateAsync(ct).ToListAsync(ct);
        scanned.Count.ShouldBe(3);
        first.Misses.ShouldBe(3);
        first.Save();

        // Rewrite one header IN PLACE keeping its size and write time: a scan that reads the file sees
        // the new target, one served from the index still sees the stored one. That is what proves the
        // index, not the file, answered, and why the stamp is the whole of the contract.
        var stamp = File.GetLastWriteTimeUtc(paths[1]);
        var bytes = File.ReadAllBytes(paths[1]);
        var text = Encoding.ASCII.GetString(bytes);
        var at = text.IndexOf("'M 42", StringComparison.Ordinal);
        at.ShouldBeGreaterThan(0);
        Encoding.ASCII.GetBytes("'M 43").CopyTo(bytes, at);
        File.WriteAllBytes(paths[1], bytes);
        File.SetLastWriteTimeUtc(paths[1], stamp);

        var second = Index();
        second.Count.ShouldBe(3);
        var rescanned = await new FitsFolderFrameSource(root, recursive: true) { HeaderIndex = second }.EnumerateAsync(ct).ToListAsync(ct);
        second.Hits.ShouldBe(3);
        second.Misses.ShouldBe(0);
        rescanned.Select(f => f.Meta.ObjectName).ShouldBe(["M 42", "M 42", "M 42"], "every header came from the index");
        for (var i = 0; i < 3; i++)
        {
            FitsHeaderIndex.SameFrame(rescanned[i], scanned[i]).ShouldBeTrue();
        }

        // A new write time is a changed file, and it is read again.
        File.SetLastWriteTimeUtc(paths[1], stamp.AddSeconds(5));
        var third = Index();
        var reread = await new FitsFolderFrameSource(root, recursive: true) { HeaderIndex = third }.EnumerateAsync(ct).ToListAsync(ct);
        third.Hits.ShouldBe(2);
        third.Misses.ShouldBe(1);
        reread[1].Meta.ObjectName.ShouldBe("M 43");
    }

    [Fact]
    public void AnIndexOfAnotherFormatOrCorrupt_IsIgnored()
    {
        var file = Path.Combine(_dir, "index", "root.headers");
        Directory.CreateDirectory(Path.GetDirectoryName(file).ShouldNotBeNull());
        File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        FitsHeaderIndex.Load(file).Count.ShouldBe(0);
    }

    [Fact]
    public void EachRootGetsItsOwnIndexFile()
    {
        var lights = Path.Combine(_dir, "archive", "lights");
        var index = FitsHeaderIndex.PathFor(_dir, lights);

        FitsHeaderIndex.PathFor(_dir, Path.Combine(_dir, "archive", "flats")).ShouldNotBe(index);
        FitsHeaderIndex.PathFor(_dir, lights + Path.DirectorySeparatorChar)
            .ShouldBe(index, "a trailing separator is the same root");

        var otherCase = FitsHeaderIndex.PathFor(_dir, Path.Combine(_dir, "archive", "LIGHTS"));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            otherCase.ShouldBe(index, "a case-insensitive file system names one root either way");
        }
        else
        {
            otherCase.ShouldNotBe(index, "on a case-sensitive file system they are two roots");
        }
    }
}
