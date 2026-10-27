using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The cursor readout of a TIFF master (<see cref="TiffReadoutTiles"/>) answers the number <c>Image.TryReadTiff</c>
/// puts in <c>image[c, y, x]</c>, bit for bit, by decoding only the strips or tiles a 64 x 64 block touches (#1853).
/// </summary>
/// <remarks>
/// SharpAstro.Tiff's writer has no planar layout and no predictor option, so those fixtures are hand-built bytes
/// (<see cref="HandBuiltTiff"/>, classic little-endian TIFF); the writer, which encodes only Uncompressed, Deflate and ZlibPkzip, is
/// used for those. Every fixture is 150 x 131, so no block, strip (40 rows) or tile (64 x 48) divides it.
/// </remarks>
public class TiffReadoutTileTests : IDisposable
{
    private const int Width = 150;
    private const int Height = 131;

    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private string NewPath(string name = "master.tif")
        => Path.Combine(_folders.Create("tianwen-tiff-readout-").FullName, name);

    /// <summary>Every sample of every channel equals the whole-page read's, as a bit pattern.</summary>
    private static TiffReadoutTiles AssertEverySampleMatches(string path, float? unitScale = null)
    {
        Image.TryReadTiff(path, out var image).ShouldBeTrue();
        TiffReadoutTiles.TryOpen(path, unitScale, out var tiles).ShouldBeTrue();
        tiles.ChannelCount.ShouldBe(image.ChannelCount);
        tiles.Width.ShouldBe(image.Width);
        tiles.Height.ShouldBe(image.Height);
        tiles.TilesRead.ShouldBe(0, "opening reads the directory and the layout, no pixel");

        for (var c = 0; c < image.ChannelCount; c++)
        {
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    tiles.TryRead(c, x, y, out var got).ShouldBeTrue($"({c}, {y}, {x})");
                    var expected = unitScale is { } unit ? image[c, y, x] * unit : image[c, y, x];
                    BitConverter.SingleToInt32Bits(got).ShouldBe(BitConverter.SingleToInt32Bits(expected),
                        $"channel {c} at ({x}, {y}) read {got} against {expected}");
                }
            }
        }

        return tiles;
    }

    public static TheoryData<string, HandBuiltTiff.Spec> HandBuiltCases()
    {
        var strips = new HandBuiltTiff.Spec { RowsPerStrip = 40 };
        var tiled = new HandBuiltTiff.Spec { TileWidth = 64, TileHeight = 48 };
        return new TheoryData<string, HandBuiltTiff.Spec>
        {
            { "u8 mono strips", strips with { Spp = 1, Bps = 8 } },
            { "u8 rgb strips", strips with { Spp = 3, Bps = 8 } },
            { "u8 rgba strips drops the alpha", strips with { Spp = 4, Bps = 8, ExtraSample = true } },
            { "u8 mono tiled", tiled with { Spp = 1, Bps = 8 } },
            { "u16 mono tiled", tiled with { Spp = 1, Bps = 16 } },
            { "u16 rgb strips", strips with { Spp = 3, Bps = 16 } },
            { "u16 rgb strips deflate", strips with { Spp = 3, Bps = 16, Deflate = true } },
            { "u16 rgb strips deflate predictor", strips with { Spp = 3, Bps = 16, Deflate = true, Predictor = true } },
            { "u16 rgb tiled deflate", tiled with { Spp = 3, Bps = 16, Deflate = true } },
            { "u16 rgb tiled deflate predictor", tiled with { Spp = 3, Bps = 16, Deflate = true, Predictor = true } },
            { "u8 rgb tiled deflate predictor", tiled with { Spp = 3, Bps = 8, Deflate = true, Predictor = true } },
            { "f32 mono strips", strips with { Spp = 1, Bps = 32, Float = true } },
            { "f32 rgb tiled", tiled with { Spp = 3, Bps = 32, Float = true } },
            { "f32 rgb strips deflate", strips with { Spp = 3, Bps = 32, Float = true, Deflate = true } },
            { "f32 mono tiled deflate", tiled with { Spp = 1, Bps = 32, Float = true, Deflate = true } },
            { "u8 rgb planar strips", strips with { Spp = 3, Bps = 8, Planar = true } },
            { "u16 rgb planar strips", strips with { Spp = 3, Bps = 16, Planar = true } },
            { "u16 rgb planar tiled deflate predictor", tiled with { Spp = 3, Bps = 16, Planar = true, Deflate = true, Predictor = true } },
            { "f32 rgb planar tiled", tiled with { Spp = 3, Bps = 32, Float = true, Planar = true } },
            { "u16 mono min-is-white strips", strips with { Spp = 1, Bps = 16, Photometric = 0 } },
            { "u8 mono min-is-white tiled deflate", tiled with { Spp = 1, Bps = 8, Photometric = 0, Deflate = true } },
            { "f32 mono min-is-white strips", strips with { Spp = 1, Bps = 32, Float = true, Photometric = 0 } },
            { "u16 rgb one strip", new HandBuiltTiff.Spec { Spp = 3, Bps = 16, RowsPerStrip = Height } },
        };
    }

    [Theory]
    [MemberData(nameof(HandBuiltCases))]
    public void EverySampleIsTheWholePageReadsOwnBitForBit(string name, HandBuiltTiff.Spec spec)
    {
        _ = name;
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(spec with { Width = Width, Height = Height }));

        AssertEverySampleMatches(path);
    }

    public static TheoryData<TiffCompression, bool, int, bool> WriterCases()
    {
        var cases = new TheoryData<TiffCompression, bool, int, bool>();
        foreach (var compression in new[] { TiffCompression.Uncompressed, TiffCompression.Deflate, TiffCompression.ZlibPkzip })
        {
            foreach (var tiled in new[] { false, true })
            {
                foreach (var (spp, floats) in new[] { (3, false), (1, true) })
                {
                    cases.Add(compression, tiled, spp, floats);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(WriterCases))]
    public async Task ASampleOfTheWritersOwnFileIsTheWholePageReadsOwn(TiffCompression compression, bool tiled, int spp, bool floats)
    {
        var path = NewPath();
        await WriteWithWriterAsync(path, compression, tiled, spp, floats);

        AssertEverySampleMatches(path);
    }

    private static async Task WriteWithWriterAsync(string path, TiffCompression compression, bool tiled, int spp, bool floats, int pages = 1)
    {
        await using var writer = TiffWriter.Create(path);
        for (var page = 0; page < pages; page++)
        {
            // A second page of another size: Image carries page 0 only, and so does the readout.
            var (w, h) = page == 0 ? (Width, Height) : (30, 20);
            var bps = floats ? 32 : 16;
            var bytes = new byte[w * h * spp * (bps / 8)];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    for (var c = 0; c < spp; c++)
                    {
                        var at = ((y * w) + x) * spp + c;
                        var bits = HandBuiltTiff.SampleBits(c, x, y, bps, floats);
                        if (floats)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at * 4), bits);
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at * 2), (ushort)bits);
                        }
                    }
                }
            }

            await writer.AddPageAsync(bytes, w, h, new TiffPageOptions
            {
                Compression = compression,
                SamplesPerPixel = spp,
                BitsPerSample = bps,
                Photometric = spp == 3 ? TiffPhotometric.Rgb : TiffPhotometric.MinIsBlack,
                SampleFormat = floats ? TiffSampleFormat.IeeeFloat : TiffSampleFormat.Uint,
                Layout = tiled ? TiffLayout.Tiled : TiffLayout.Strip,
                TileWidth = 64,
                TileHeight = 48,
                RowsPerStrip = 40,
            }, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task OnlyPageZeroIsReadOfAMultiPageFile()
    {
        var path = NewPath();
        await WriteWithWriterAsync(path, TiffCompression.Deflate, tiled: false, spp: 3, floats: false, pages: 2);

        var tiles = AssertEverySampleMatches(path);
        tiles.Width.ShouldBe(Width);
        tiles.Height.ShouldBe(Height);
    }

    [Fact]
    public void TheUnitScaleIsMultipliedInAsTheImportDoes()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 3, Bps = 16, TileWidth = 64, TileHeight = 48,
        }));

        AssertEverySampleMatches(path, unitScale: 0.25f);
    }

    [Fact]
    public void EachBlockIsReadFromTheFileExactlyOnce()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 3, Bps = 16, Planar = true, TileWidth = 64, TileHeight = 48, Deflate = true,
        }));

        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        const int blocksAcross = (Width + 63) / 64;
        const int blocksDown = (Height + 63) / 64;
        for (var c = 0; c < 3; c++)
        {
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    tiles.TryRead(c, x, y, out _).ShouldBeTrue();
                }
            }
        }

        // One decode per block serves all three channels.
        tiles.TilesRead.ShouldBe(blocksAcross * blocksDown);

        // A second sweep is all cache.
        var before = tiles.TilesRead;
        for (var y = 0; y < Height; y += 7)
        {
            for (var x = 0; x < Width; x += 5)
            {
                tiles.TryRead(1, x, y, out _).ShouldBeTrue();
            }
        }

        tiles.TilesRead.ShouldBe(before);
    }

    [Fact]
    public void AllChannelsOfABlockAreOneDecodeOfEachSegment()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 3, Bps = 16, RowsPerStrip = 40, Deflate = true,
        }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        tiles.TryRead(0, 10, 10, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(1);
        tiles.TryRead(1, 10, 10, out _).ShouldBeTrue();
        tiles.TryRead(2, 63, 39, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(1, "the other channels of the block came with the first read");
    }

    [Fact]
    public void ASegmentPastTheLimitIsDeclinedAndTheSameImageInSmallSegmentsIsServed()
    {
        // 1200 x 900 RGB u16 as one whole-page strip decodes to 6.5 MB per block: the planes serve it.
        var wholePage = NewPath("whole.tif");
        File.WriteAllBytes(wholePage, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = 1200, Height = 900, Spp = 3, Bps = 16, RowsPerStrip = 900, Deflate = true,
        }));
        TiffReadoutTiles.TryOpen(wholePage, null, out _).ShouldBeFalse();

        var strips = NewPath("strips.tif");
        File.WriteAllBytes(strips, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = 1200, Height = 900, Spp = 3, Bps = 16, RowsPerStrip = 64, Deflate = true,
        }));
        TiffReadoutTiles.TryOpen(strips, null, out _).ShouldBeTrue();
    }

    [Fact]
    public void ACachedReadAllocatesNothing()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 3, Bps = 16, RowsPerStrip = 40, Deflate = true,
        }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        tiles.TryRead(2, 10, 10, out _).ShouldBeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0f;
        for (var i = 0; i < 1000; i++)
        {
            tiles.TryRead(2, i % 64, (i / 64) % 64, out var value).ShouldBeTrue();
            sum += value;
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBe(0L);
        sum.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public void AChangedFileIsStaleForGood()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 1, Bps = 16, TileWidth = 64, TileHeight = 48,
        }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        tiles.TryRead(0, 1, 1, out _).ShouldBeTrue();
        tiles.IsStale.ShouldBeFalse();

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        tiles.TryRead(0, 100, 100, out _).ShouldBeFalse("an unread block of a changed file is not read");
        tiles.IsStale.ShouldBeTrue();
        tiles.TryRead(0, 1, 1, out _).ShouldBeFalse("and once stale nothing is answered, a cached block included");
    }

    [Fact]
    public void AFileThatChangedLengthIsStale()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec
        {
            Width = Width, Height = Height, Spp = 1, Bps = 8, RowsPerStrip = 40,
        }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        var written = File.GetLastWriteTimeUtc(path);

        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.WriteByte(0);
        }

        File.SetLastWriteTimeUtc(path, written);
        tiles.TryRead(0, 1, 1, out _).ShouldBeFalse();
        tiles.IsStale.ShouldBeTrue();
    }

    [Fact]
    public void AMissingFileAnswersNothingAndIsStale()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 1, Bps = 8 }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        File.Delete(path);

        tiles.TryRead(0, 1, 1, out _).ShouldBeFalse();
        tiles.IsStale.ShouldBeTrue();
    }

    [Fact]
    public void APositionOutsideTheImageIsNotRead()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 3, Bps = 8 }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        tiles.TryRead(3, 0, 0, out _).ShouldBeFalse();
        tiles.TryRead(0, Width, 0, out _).ShouldBeFalse();
        tiles.TryRead(0, 0, Height, out _).ShouldBeFalse();
        tiles.TryRead(0, -1, 0, out _).ShouldBeFalse();
    }

    [Fact]
    public void TheReloadReadsTheWholePageAsTheImportDoes()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 3, Bps = 16, TileWidth = 64, TileHeight = 48 }));
        TiffReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        tiles.TryReadImage(out var reloaded).ShouldBeTrue();
        Image.TryReadTiff(path, out var direct).ShouldBeTrue();
        reloaded.ChannelCount.ShouldBe(direct.ChannelCount);
        reloaded[2, 100, 120].ShouldBe(direct[2, 100, 120]);
    }

    [Fact]
    public void ACmykPageIsDeclinedBecauseTheWholePageRebuildsItAsRgb()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 4, Bps = 8, Photometric = 5, RowsPerStrip = 40 }));

        Image.TryReadTiff(path, out var image).ShouldBeTrue();
        image.ChannelCount.ShouldBe(3);
        TiffReadoutTiles.TryOpen(path, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void AThirtyTwoBitIntegerPageIsDeclinedBecauseTheWholePageReadRefusesIt()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 1, Bps = 32, RowsPerStrip = 40 }));

        Image.TryReadTiff(path, out _).ShouldBeFalse();
        TiffReadoutTiles.TryOpen(path, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void APalettePageIsDeclined()
    {
        var path = NewPath();
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 1, Bps = 8, Photometric = 3, RowsPerStrip = 40 }));

        Image.TryReadTiff(path, out _).ShouldBeFalse();
        TiffReadoutTiles.TryOpen(path, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void ABigTiffIsDeclined()
    {
        var path = NewPath();
        var bytes = HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 1, Bps = 8, RowsPerStrip = 40 });

        // The BigTIFF magic over a classic body: the file is not one this reader lays out, whatever follows.
        bytes[2] = 43;
        File.WriteAllBytes(path, bytes);

        TiffReadoutTiles.TryOpen(path, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void AnEmptyFileAndAFileThatIsNotATiffAreDeclined()
    {
        var empty = NewPath("empty.tif");
        File.WriteAllBytes(empty, []);
        TiffReadoutTiles.TryOpen(empty, null, out _).ShouldBeFalse();

        var junk = NewPath("junk.tif");
        File.WriteAllBytes(junk, [.. Enumerable.Range(0, 200).Select(i => (byte)(i * 7))]);
        TiffReadoutTiles.TryOpen(junk, null, out _).ShouldBeFalse();

        TiffReadoutTiles.TryOpen(NewPath("absent.tif"), null, out _).ShouldBeFalse();
    }

    [Fact]
    public void AnotherExtensionIsNotServed()
    {
        var path = NewPath("master.fits");
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = Width, Height = Height, Spp = 1, Bps = 8 }));

        TiffReadoutTiles.TryOpen(path, null, out _).ShouldBeFalse();
    }
}

/// <summary>
/// Classic little-endian TIFF bytes with a layout SharpAstro.Tiff's writer cannot make (planar, predictor), the
/// samples a pure function of (sample, x, y) so a test never keeps a second copy of the picture.
/// </summary>
public static class HandBuiltTiff
{
    /// <summary>What to build. <see cref="Photometric"/> is the TIFF value: 0 MinIsWhite, 1 MinIsBlack, 2 RGB, 3 palette, 5 CMYK.</summary>
    public sealed record Spec
    {
        public int Width { get; init; } = 150;
        public int Height { get; init; } = 131;
        public int Spp { get; init; } = 1;
        public int Bps { get; init; } = 8;
        public bool Float { get; init; }
        public int Photometric { get; init; } = -1;
        public bool Planar { get; init; }
        public int TileWidth { get; init; }
        public int TileHeight { get; init; }
        public int RowsPerStrip { get; init; }
        public bool Deflate { get; init; }
        public bool Predictor { get; init; }
        public bool ExtraSample { get; init; }
    }

    /// <summary>The raw bits of sample <paramref name="c"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static uint SampleBits(int c, int x, int y, int bps, bool isFloat)
    {
        var hash = unchecked((uint)((x * 2654435761L) ^ (y * 40503L) ^ (c * 9973L)) * 2246822519u);
        hash ^= hash >> 15;
        if (isFloat)
        {
            // Mostly [0, 1], with some outside it: a float is stored as it is.
            return BitConverter.SingleToUInt32Bits(((hash % 4000u) / 3000f) - 0.1f);
        }

        return bps == 8 ? hash & 0xFFu : hash & 0xFFFFu;
    }

    public static byte[] Build(Spec spec)
    {
        var photometric = spec.Photometric >= 0 ? spec.Photometric : spec.Spp >= 3 ? 2 : 1;
        var tiled = spec.TileWidth > 0;
        var planar = spec.Planar && spec.Spp > 1;
        var bytesPerSample = spec.Bps / 8;
        var segmentSamples = planar ? 1 : spec.Spp;
        var rowsPerStrip = tiled ? 0 : spec.RowsPerStrip > 0 ? spec.RowsPerStrip : spec.Height;
        var across = tiled ? (spec.Width + spec.TileWidth - 1) / spec.TileWidth : 1;
        var down = tiled ? (spec.Height + spec.TileHeight - 1) / spec.TileHeight : (spec.Height + rowsPerStrip - 1) / rowsPerStrip;
        var planes = planar ? spec.Spp : 1;

        var segments = new List<byte[]>();
        for (var plane = 0; plane < planes; plane++)
        {
            for (var row = 0; row < down; row++)
            {
                for (var column = 0; column < across; column++)
                {
                    var x0 = tiled ? column * spec.TileWidth : 0;
                    var y0 = tiled ? row * spec.TileHeight : row * rowsPerStrip;
                    var w = tiled ? spec.TileWidth : spec.Width;
                    var h = tiled ? spec.TileHeight : Math.Min(rowsPerStrip, spec.Height - y0);
                    var raw = Segment(spec, plane, planar, segmentSamples, bytesPerSample, x0, y0, w, h);
                    segments.Add(spec.Deflate ? Deflate(raw) : raw);
                }
            }
        }

        var entries = new List<Entry>
        {
            Long(256, spec.Width),
            Long(257, spec.Height),
            Shorts(258, [.. Enumerable.Repeat((ushort)spec.Bps, spec.Spp)]),
            Shorts(259, (ushort)(spec.Deflate ? 8 : 1)),
            Shorts(262, (ushort)photometric),
            Shorts(277, (ushort)spec.Spp),
            Shorts(284, (ushort)(planar ? 2 : 1)),
            Shorts(339, [.. Enumerable.Repeat((ushort)(spec.Float ? 3 : 1), spec.Spp)]),
        };
        if (spec.Predictor)
        {
            entries.Add(Shorts(317, 2));
        }

        if (spec.ExtraSample)
        {
            entries.Add(Shorts(338, 2));
        }

        var offsets = new Entry(tiled ? (ushort)324 : (ushort)273, 4, segments.Count, new byte[segments.Count * 4]);
        var counts = new Entry(tiled ? (ushort)325 : (ushort)279, 4, segments.Count, new byte[segments.Count * 4]);
        entries.Add(offsets);
        entries.Add(counts);
        if (tiled)
        {
            entries.Add(Long(322, spec.TileWidth));
            entries.Add(Long(323, spec.TileHeight));
        }
        else
        {
            entries.Add(Long(278, rowsPerStrip));
        }

        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        // Header, the directory, then each value too big for its field (4-aligned), then the segments.
        var cursor = 8 + 2 + (entries.Count * 12) + 4;
        var external = new Dictionary<Entry, int>();
        foreach (var entry in entries.Where(e => e.Data.Length > 4))
        {
            external[entry] = cursor;
            cursor += (entry.Data.Length + 3) & ~3;
        }

        var dataStart = cursor;
        for (var i = 0; i < segments.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(offsets.Data.AsSpan(i * 4), (uint)cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(counts.Data.AsSpan(i * 4), (uint)segments[i].Length);
            cursor += (segments[i].Length + 3) & ~3;
        }

        var file = new byte[cursor];
        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var at = 10 + (i * 12);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), entry.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 2), entry.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), (uint)entry.Count);
            if (external.TryGetValue(entry, out var offset))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 8), (uint)offset);
                entry.Data.CopyTo(file, offset);
            }
            else
            {
                entry.Data.CopyTo(file, at + 8);
            }
        }

        var position = dataStart;
        foreach (var segment in segments)
        {
            segment.CopyTo(file, position);
            position += (segment.Length + 3) & ~3;
        }

        return file;
    }

    private static byte[] Segment(Spec spec, int plane, bool planar, int segmentSamples, int bytesPerSample, int x0, int y0, int w, int h)
    {
        var bytes = new byte[w * h * segmentSamples * bytesPerSample];
        var mask = spec.Bps == 8 ? 0xFFu : 0xFFFFu;
        var previous = new uint[segmentSamples];
        for (var row = 0; row < h; row++)
        {
            Array.Clear(previous);
            for (var column = 0; column < w; column++)
            {
                var x = x0 + column;
                var y = y0 + row;
                for (var s = 0; s < segmentSamples; s++)
                {
                    // An edge tile's padding is stored as zeros.
                    var bits = x < spec.Width && y < spec.Height ? SampleBits(planar ? plane : s, x, y, spec.Bps, spec.Float) : 0u;
                    var stored = bits;
                    if (spec.Predictor)
                    {
                        // Horizontal differencing, per sample, modulo the sample's width.
                        stored = (bits - previous[s]) & mask;
                        previous[s] = bits;
                    }

                    var at = ((((row * w) + column) * segmentSamples) + s) * bytesPerSample;
                    switch (bytesPerSample)
                    {
                        case 1: bytes[at] = (byte)stored; break;
                        case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)stored); break;
                        default: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), stored); break;
                    }
                }
            }
        }

        return bytes;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private sealed record Entry(ushort Tag, ushort Type, int Count, byte[] Data);

    private static Entry Long(ushort tag, int value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)value);
        return new Entry(tag, 4, 1, data);
    }

    private static Entry Shorts(ushort tag, params ushort[] values)
    {
        var data = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), values[i]);
        }

        return new Entry(tag, 3, values.Length, data);
    }
}
