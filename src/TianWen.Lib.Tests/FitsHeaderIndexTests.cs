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
using TianWen.Lib.Devices;

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
            FocusPos: FocuserPositions.None,
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

    /// <summary>
    /// A 16-bit image of 4 x 4 written by hand, so that its header can repeat cards as Astro Pixel Processor's
    /// does: 5 mandatory cards and 25 others, then <paramref name="repeated"/> of the others once more, then
    /// END. With 10 repeated that is 41 cards in two blocks, which FITS.Lib parses to 31 in one.
    /// </summary>
    private string WriteWithRepeatedCards(string name, int repeated)
    {
        static string Card(string text) => text.PadRight(80);
        var header = new StringBuilder();
        header.Append(Card("SIMPLE  =                    T"));
        header.Append(Card("BITPIX  =                   16"));
        header.Append(Card("NAXIS   =                    2"));
        header.Append(Card("NAXIS1  =                    4"));
        header.Append(Card("NAXIS2  =                    4"));
        for (var i = 0; i < 25; i++)
        {
            header.Append(Card($"MEDNR{i,-3}=                  {100 + i}"));
        }
        for (var i = 0; i < repeated; i++)
        {
            header.Append(Card($"MEDNR{i,-3}=                  {100 + i}"));
        }
        header.Append(Card("END"));
        header.Append(' ', (2880 - header.Length % 2880) % 2880);

        var dir = Path.Combine(_dir, "archive", "proc");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes(header.ToString()));
        file.Write(new byte[2880]);
        return path;
    }

    // #1529: an Astro Pixel Processor integration writes 18 cards twice. FITS.Lib keeps one of each, so the PARSED
    // header was a block shorter than the file's, the index stored that many bytes, they ended before the END
    // card, and the replay read them for ever (28 GB of warnings from one archive scan). The stored header is the
    // file's own, through its END.
    [Fact]
    public void AHeaderWithRepeatedCardsIsStoredThroughItsEndCardAndReplays()
    {
        var path = WriteWithRepeatedCards("integration.fits", repeated: 10);
        Image.TryReadFitsHeader(path, out var read, out var parsedSize).ShouldBeTrue();
        parsedSize.ShouldBe(2880, "31 cards are left of the 41 once the repeats are merged: one block");

        var index = Index();
        var onDisk = new FileInfo(path);
        var stamp = new FileStamp(path, onDisk.Length, onDisk.LastWriteTimeUtc);
        index.Remember(stamp, parsedSize, read);

        index.TryGet(stamp, out var stored).ShouldBeTrue("the header replays, so it is stored");
        stored.Length.ShouldBe(5760, "the two blocks the file's header takes, not the one the parsed header would");
        FitsHeaderBlocks.HoldsEnd(stored).ShouldBeTrue();
        Image.TryReadFitsHeaderFromBytes(path, stored, out var replayed).ShouldBeTrue();
        FitsHeaderIndex.SameFrame(replayed, read).ShouldBeTrue();
    }

    // Bytes that end before a header's END card are not a header. They are refused before the parser sees them,
    // which matters beyond tidiness: until FITS.Lib#11 the library never returned from such bytes.
    [Fact]
    public void BytesWithNoEndCardAreNotAHeaderAndAreNotParsed()
    {
        var path = WriteWithRepeatedCards("integration.fits", repeated: 10);
        var firstBlock = File.ReadAllBytes(path)[..2880];

        FitsHeaderBlocks.HoldsEnd(firstBlock).ShouldBeFalse();
        Image.TryReadFitsHeaderFromBytes(path, firstBlock, out _).ShouldBeFalse();
    }

    // #2410. An index written before #1529 could hold a header cut before its END card. Such an entry is a MISS:
    // it is not served, the scan reads the file (FitsFolderFrameSource.TryReadFrameInfo does exactly the three steps
    // below), and the header is stored again, whole. Asserted on the index's own answer and counts, so a lost refusal
    // fails here at once, whatever the parser would have made of such bytes.
    [Fact]
    public void AStoredHeaderThatEndsBeforeItsEndCardIsAMissAndIsStoredAgainWhole()
    {
        var path = WriteWithRepeatedCards("integration.fits", repeated: 10);
        Image.TryReadFitsHeader(path, out var read, out var parsedSize).ShouldBeTrue();
        var onDisk = new FileInfo(path);
        var stamp = new FileStamp(path, onDisk.Length, onDisk.LastWriteTimeUtc);

        // The index as the older build left it: this file's first block, which ends before the END card.
        var index = Index();
        var cut = File.ReadAllBytes(path)[..2880];
        FitsHeaderBlocks.HoldsEnd(cut).ShouldBeFalse("premise: the first block of a two-block header holds no END");
        index.Plant(stamp, cut);
        index.Count.ShouldBe(1);

        index.TryGet(stamp, out var served).ShouldBeFalse("an entry with no END card is not a header, so it is not served");
        served.ShouldBeNull();
        (index.Hits, index.Misses).ShouldBe((0, 1), "and it is a miss, which is what sends the scan to the file");

        index.Remember(stamp, parsedSize, read);

        index.TryGet(stamp, out var stored).ShouldBeTrue("the file's header is stored in its place");
        stored.Length.ShouldBe(5760, "whole: both of its blocks");
        FitsHeaderBlocks.HoldsEnd(stored).ShouldBeTrue();
    }

    // #1529, the library's half: a FILE that ends before its header's END card. FITS.Lib read it for ever until
    // FITS.Lib#11; now it throws, the read answers false, and a scan passes the file by. Run off the test's thread
    // under the test's own timeout, so that a library that lost the fix fails this test and does not hang the suite
    // (the read would still spin on its thread until the process ends: that loop is the library's, and its own tests
    // hold it).
    [Fact(Timeout = 60_000)]
    public async Task AFileThatEndsBeforeItsHeadersEndCardIsNotAFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var whole = WriteWithRepeatedCards("integration.fits", repeated: 10);
        var cut = Path.Combine(Path.GetDirectoryName(whole) ?? _dir, "cut.fits");
        File.WriteAllBytes(cut, File.ReadAllBytes(whole)[..2880]);

        var read = await Task.Run(() => Image.TryReadFitsHeader(cut, out _), ct).WaitAsync(ct);

        read.ShouldBeFalse("a header with no END card within the file is a truncated file, not a frame");
    }

    // Where a header ends in a file's bytes: at the end of the block that holds its END card, wherever the card
    // sits in the block, and nowhere when there is none. A card that only BEGINS with END is not the END card.
    [Theory]
    [InlineData(0, 2880)]       // END is the first card
    [InlineData(35, 2880)]      // END is the last card of the first block
    [InlineData(36, 5760)]      // END is the first card of the second block
    [InlineData(100, 8640)]     // somewhere in the third
    public void AHeaderEndsWithTheBlockThatHoldsItsEndCard(int cardsBeforeEnd, int length)
    {
        var bytes = new byte[4 * 2880];
        bytes.AsSpan().Fill((byte)' ');
        for (var i = 0; i < cardsBeforeEnd; i++)
        {
            Encoding.ASCII.GetBytes($"ENDTIME{i % 10}= 1").CopyTo(bytes, i * 80);
        }
        Encoding.ASCII.GetBytes("END").CopyTo(bytes, cardsBeforeEnd * 80);

        FitsHeaderBlocks.LengthThroughEnd(bytes).ShouldBe(length);
        FitsHeaderBlocks.LengthThroughEnd(bytes.AsSpan(0, cardsBeforeEnd * 80)).ShouldBe(-1, "the cards before it hold no END card");

        // Read from a stream, starting from one block as a parsed header would say: the same bytes.
        FitsHeaderBlocks.ReadThroughEnd(new MemoryStream(bytes), atLeast: 2880).ShouldNotBeNull().Length.ShouldBe(length);
        // A file that ends before the END card has no header to store.
        FitsHeaderBlocks.ReadThroughEnd(new MemoryStream(bytes, 0, cardsBeforeEnd * 80), atLeast: 2880).ShouldBeNull();
    }

    // A header is followed for 32 blocks and no further: past that it is not a header this reads.
    [Fact]
    public void AHeaderWithNoEndCardWithinTheLimitIsNotRead()
    {
        var blanks = new byte[40 * 2880];
        blanks.AsSpan().Fill((byte)' ');
        var stream = new MemoryStream(blanks);

        FitsHeaderBlocks.ReadThroughEnd(stream, atLeast: 2880).ShouldBeNull();
        stream.Position.ShouldBe(32 * 2880, "it stops reading at the limit and does not walk the whole file");
    }

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
