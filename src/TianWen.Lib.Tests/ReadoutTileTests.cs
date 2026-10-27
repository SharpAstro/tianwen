using System;
using System.IO;
using System.IO.Compression;
using nom.tam.fits;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The cursor readout's tile cache (<see cref="FitsReadoutTiles"/>) answers the number the importer's plane holds,
/// bit for bit, without a plane, and reads a tile once however the cursor moves inside it.
/// </summary>
/// <remarks>
/// The reference is <c>Image.TryReadFitsFile</c> followed by the scaling a document applies on adoption, read
/// through <c>image[c, y, x]</c>. The size is not a multiple of the tile on either axis, so every edge and corner
/// tile is a partial one.
/// </remarks>
public class ReadoutTileTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    // 150 = 2 x 64 + 22 and 70 = 64 + 6: a partial tile on each axis, and a one-row-high strip along the bottom.
    private const int Width = 150, Height = 70;

    public static TheoryData<int, string, int> Cases()
    {
        var cases = new TheoryData<int, string, int>();
        foreach (var bitpix in new[] { 8, 16, 32, -32 })
        {
            foreach (var scaling in new[] { "none", "unsigned", "scaled" })
            {
                foreach (var planes in new[] { 1, 3 })
                {
                    cases.Add(bitpix, scaling, planes);
                }
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EverySampleIsTheImagesOwnSampleBitForBit(int bitpix, string scaling, int planes)
    {
        var path = WriteFits(bitpix, scaling, planes);
        AssertEverySampleMatches(path, $"b{bitpix}_{scaling}_{planes}p");
    }

    /// <summary>A float file with payload NaNs, a negative zero and infinities: what arithmetic would change and a copy keeps.</summary>
    [Fact]
    public void AFloatFileWithNoScalingKeepsItsSpecialValues()
    {
        float[] specials = [-0.0f, float.NaN, BitConverter.Int32BitsToSingle(0x7FC00001), float.PositiveInfinity, float.NegativeInfinity, float.Epsilon, 1.5f, 0f, 65535f];
        var path = WriteFits(-32, "none", 1, i => specials[i % specials.Length]);

        AssertEverySampleMatches(path, "specials");
    }

    [Fact]
    public void MovingInsideATileReadsNothingNew()
    {
        var path = WriteFits(16, "unsigned", 3);
        FitsReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        tiles.TilesRead.ShouldBe(0, "opening reads the header and no pixel");
        tiles.TryRead(1, 70, 10, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(1);

        // Every position of that tile, in both directions: still the one read.
        for (var y = 0; y < FitsReadoutTiles.TileSize; y++)
        {
            for (var x = FitsReadoutTiles.TileSize; x < 2 * FitsReadoutTiles.TileSize; x++)
            {
                tiles.TryRead(1, x, y, out _).ShouldBeTrue();
            }
        }

        tiles.TilesRead.ShouldBe(1, "a move inside a tile is served from the cache");

        // Another channel's tile at the same position is another tile, and another tile of the same channel too.
        tiles.TryRead(0, 70, 10, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(2);
        tiles.TryRead(1, 5, 10, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(3);
    }

    /// <summary>Allocation-based, never working set: a hit allocates nothing, a miss allocates the tile and not much else.</summary>
    [Fact]
    public void AHitAllocatesNothingAndAMissAllocatesTheTile()
    {
        var path = WriteFits(-32, "none", 3);
        FitsReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        tiles.TryRead(0, 3, 3, out _).ShouldBeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        float sum = 0;
        for (var i = 0; i < 10_000; i++)
        {
            tiles.TryRead(0, i % FitsReadoutTiles.TileSize, (i / 7) % FitsReadoutTiles.TileSize, out var v).ShouldBeTrue();
            sum += v;
        }

        var hits = GC.GetAllocatedBytesForCurrentThread() - before;
        sum.ShouldNotBe(float.NaN);
        hits.ShouldBe(0, "a read inside a cached tile allocates nothing");

        before = GC.GetAllocatedBytesForCurrentThread();
        tiles.TryRead(2, 100, 40, out _).ShouldBeTrue();
        var miss = GC.GetAllocatedBytesForCurrentThread() - before;
        var tile = FitsReadoutTiles.TileSize * FitsReadoutTiles.TileSize * sizeof(float);
        miss.ShouldBeLessThan(tile + 4096, $"a miss allocates its {tile}-byte tile and a handle, not a plane ({Width * Height * sizeof(float)} B)");
    }

    [Fact]
    public void EveryTileIsReadOnceHoweverOftenItIsVisited()
    {
        var path = WriteFits(16, "none", 3);
        FitsReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        // Many passes over every tile of every channel (3 x 3 x 2 = 18 tiles, under MaxTiles).
        for (var pass = 0; pass < 3; pass++)
        {
            for (var c = 0; c < 3; c++)
            {
                for (var y = 0; y < Height; y += FitsReadoutTiles.TileSize / 2)
                {
                    for (var x = 0; x < Width; x += FitsReadoutTiles.TileSize / 2)
                    {
                        tiles.TryRead(c, x, y, out _).ShouldBeTrue();
                    }
                }
            }
        }

        tiles.TilesRead.ShouldBe(18, "one read per tile, however many passes");
    }

    [Fact]
    public void APositionOutsideTheImageIsRefused()
    {
        var path = WriteFits(16, "none", 1);
        FitsReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();

        tiles.TryRead(0, -1, 0, out _).ShouldBeFalse();
        tiles.TryRead(0, 0, -1, out _).ShouldBeFalse();
        tiles.TryRead(0, Width, 0, out _).ShouldBeFalse();
        tiles.TryRead(0, 0, Height, out _).ShouldBeFalse();
        tiles.TryRead(1, 0, 0, out _).ShouldBeFalse();
        tiles.TilesRead.ShouldBe(0);
    }

    /// <summary>What the importer does not read through FitsReader, the tiles do not serve: those keep reading the planes.</summary>
    [Fact]
    public void WhatTheReaderDeclinesIsNotServed()
    {
        var folder = _folders.Create("tianwen-readout-tiles-").FullName;

        FitsReadoutTiles.TryOpen(Path.Combine(folder, "missing.fits"), null, out _).ShouldBeFalse("no such file");

        var notFits = Path.Combine(folder, "plain.fits");
        File.WriteAllText(notFits, "not a FITS file");
        FitsReadoutTiles.TryOpen(notFits, null, out _).ShouldBeFalse("not FITS");

        FitsReadoutTiles.TryOpen(WriteFits(64, "none", 1), null, out _).ShouldBeFalse("BITPIX 64 is the HDU path's");

        var plain = WriteFits(16, "none", 1);
        var gzipped = plain + ".gz";
        using (var input = File.OpenRead(plain))
        using (var output = File.Create(gzipped))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            input.CopyTo(gzip);
        }

        FitsReadoutTiles.TryOpen(gzipped, null, out _).ShouldBeFalse("a gzip stream cannot be read at an offset");
    }

    [Fact]
    public void AFileThatChangedIsNotReadAgain()
    {
        var path = WriteFits(16, "none", 1);
        FitsReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        tiles.TryRead(0, 1, 1, out _).ShouldBeTrue();

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        tiles.TryRead(0, 1, 1, out _).ShouldBeTrue("a cached tile is what the file held when it was read");
        tiles.TryRead(0, 100, 60, out _).ShouldBeFalse("a miss would read a different file");
        tiles.IsStale.ShouldBeTrue();
        tiles.TryRead(0, 1, 1, out _).ShouldBeFalse("and once stale nothing is quoted from it");
    }

    private void AssertEverySampleMatches(string path, string name)
    {
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue(name);

        // What a document does on adoption (AstroImageDocument.OpenFitsAsync reads the factor before this spends it).
        float? unitScale = image.HasUnitScalePeak || image.UnitScaleDivisor <= 0f ? null : 1.0f / image.UnitScaleDivisor;
        var adopted = image.HasUnitScalePeak ? image : image.ScaleFloatValuesToUnitInPlace();

        FitsReadoutTiles.TryOpen(path, unitScale, out var tiles).ShouldBeTrue(name);
        tiles.ChannelCount.ShouldBe(adopted.ChannelCount, name);
        tiles.Width.ShouldBe(adopted.Width, name);
        tiles.Height.ShouldBe(adopted.Height, name);

        for (var c = 0; c < adopted.ChannelCount; c++)
        {
            for (var y = 0; y < adopted.Height; y++)
            {
                for (var x = 0; x < adopted.Width; x++)
                {
                    tiles.TryRead(c, x, y, out var got).ShouldBeTrue($"{name} ({c}, {y}, {x})");
                    var want = adopted[c, y, x];
                    BitConverter.SingleToInt32Bits(got).ShouldBe(BitConverter.SingleToInt32Bits(want),
                        $"{name}: channel {c} at ({x}, {y}) read {got} against {want}");
                }
            }
        }

        // Every corner explicitly, since a partial tile's last sample is the one an off-by-one drops.
        foreach (var (x, y) in new[] { (0, 0), (Width - 1, 0), (0, Height - 1), (Width - 1, Height - 1) })
        {
            tiles.TryRead(adopted.ChannelCount - 1, x, y, out var corner).ShouldBeTrue(name);
            BitConverter.SingleToInt32Bits(corner).ShouldBe(BitConverter.SingleToInt32Bits(adopted[adopted.ChannelCount - 1, y, x]), $"{name}: corner ({x}, {y})");
        }
    }

    /// <summary>A file as other software writes one: no DATAMIN or DATAMAX, BZERO and BSCALE only when asked for.</summary>
    private string WriteFits(int bitpix, string scaling, int planes, Func<int, float>? floats = null)
    {
        var (bzero, bscale) = scaling switch
        {
            "none" => ((double?)null, (double?)null),
            "unsigned" => (32768.0, 1.0),
            _ => (-3.25, 0.5),
        };
        var path = Path.Combine(_folders.Create("tianwen-readout-tiles-").FullName, $"b{bitpix}_{scaling}_{planes}p.fits");
        var header = planes == 1 ? FitsWriter.ImageHeader(bitpix, Width, Height) : FitsWriter.ImageHeader(bitpix, Width, Height, planes);
        if (bzero is { } zero)
        {
            header.AddValue("BZERO", zero, "");
        }

        if (bscale is { } scale)
        {
            header.AddValue("BSCALE", scale, "");
        }

        var n = Width * Height * planes;
        using var writer = FitsWriter.CreateFile(path, header);
        switch (bitpix)
        {
            case 8: writer.Write<byte>(Samples(n, i => (byte)(i * 7))); break;
            case 16: writer.Write<short>(Samples(n, i => (short)((i * 1301) - 20000))); break;
            case 32: writer.Write<int>(Samples(n, i => (i * 1_000_003) - 7_000_000)); break;
            case 64: writer.Write<long>(Samples(n, i => ((long)i << 36) - i)); break;
            case -32: writer.Write<float>(Samples(n, floats ?? (i => (i * 0.37f) - 100f))); break;
        }

        writer.Finish();
        return path;
    }

    private static T[] Samples<T>(int count, Func<int, T> value)
    {
        var samples = new T[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = value(i);
        }

        return samples;
    }
}
