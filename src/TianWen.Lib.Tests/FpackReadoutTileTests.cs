using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The cursor readout of a tile-compressed master (<see cref="FpackReadoutTiles"/>) answers the number a full import
/// holds, bit for bit, by decoding only the stored tiles a 64 x 64 block touches, and lets a large <c>.fz</c> master
/// release its planes like a plain FITS one (#1856).
/// </summary>
/// <remarks>
/// FITS.Lib compresses nothing (its writer emits the plain image extension), so the only <c>.fz</c> there is to test
/// against is the embedded <c>tilecompressed.fz</c> (3 x 24 x 32, float, fpack's one-row tiles): a quantized float
/// image, which is the dither path. The integer decoders are FITS.Lib's own and are served through the same
/// conversion the plain source is pinned on.
/// </remarks>
public class FpackReadoutTileTests : IDisposable
{
    private const string Fixture = "tilecompressed.fz";

    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private string FixturePath(string name = Fixture)
    {
        var path = Path.Combine(_folders.Create("tianwen-fpack-readout-").FullName, name);
        using var source = SharedTestData.OpenEmbeddedFileStream(Fixture)
            ?? throw new InvalidOperationException($"Missing test data {Fixture}");
        using var target = File.Create(path);
        source.CopyTo(target);
        return path;
    }

    [Fact]
    public void EverySampleIsTheImportsOwnSampleBitForBit()
    {
        var path = FixturePath();
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue();

        // What a document does on adoption (AstroImageDocument.OpenFitsAsync reads the factor before this spends it).
        float? unitScale = image.HasUnitScalePeak || image.UnitScaleDivisor <= 0f ? null : 1.0f / image.UnitScaleDivisor;
        var adopted = image.HasUnitScalePeak ? image : image.ScaleFloatValuesToUnitInPlace();

        FpackReadoutTiles.TryOpen(path, unitScale, out var tiles).ShouldBeTrue();
        tiles.ChannelCount.ShouldBe(adopted.ChannelCount);
        tiles.Width.ShouldBe(adopted.Width);
        tiles.Height.ShouldBe(adopted.Height);
        tiles.TilesRead.ShouldBe(0, "opening reads headers and geometry, no pixel");

        for (var c = 0; c < adopted.ChannelCount; c++)
        {
            for (var y = 0; y < adopted.Height; y++)
            {
                for (var x = 0; x < adopted.Width; x++)
                {
                    tiles.TryRead(c, x, y, out var got).ShouldBeTrue($"({c}, {y}, {x})");
                    BitConverter.SingleToInt32Bits(got).ShouldBe(BitConverter.SingleToInt32Bits(adopted[c, y, x]),
                        $"channel {c} at ({x}, {y}) read {got} against {adopted[c, y, x]}");
                }
            }
        }
    }

    [Fact]
    public void TheUnitScaleIsMultipliedInAsTheImportDoes()
    {
        var path = FixturePath();
        Image.TryReadFitsFile(path, out var raw).ShouldBeTrue();
        FpackReadoutTiles.TryOpen(path, 0.25f, out var tiles).ShouldBeTrue();

        for (var y = 0; y < raw.Height; y += 5)
        {
            for (var x = 0; x < raw.Width; x += 3)
            {
                tiles.TryRead(1, x, y, out var got).ShouldBeTrue();
                BitConverter.SingleToInt32Bits(got).ShouldBe(BitConverter.SingleToInt32Bits(raw[1, y, x] * 0.25f), $"({x}, {y})");
            }
        }
    }

    [Fact]
    public void MovingInsideABlockDecodesNothingNew()
    {
        FpackReadoutTiles.TryOpen(FixturePath(), null, out var tiles).ShouldBeTrue();

        tiles.TryRead(1, 5, 7, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(1);

        for (var y = 0; y < tiles.Height; y++)
        {
            for (var x = 0; x < tiles.Width; x++)
            {
                tiles.TryRead(1, x, y, out _).ShouldBeTrue();
            }
        }

        tiles.TilesRead.ShouldBe(1, "the 32 x 24 channel is one block, served from the cache");

        tiles.TryRead(2, 5, 7, out _).ShouldBeTrue();
        tiles.TilesRead.ShouldBe(2, "another channel is another block");
    }

    [Fact]
    public void AChangedFileGoesStale()
    {
        var path = FixturePath();
        FpackReadoutTiles.TryOpen(path, null, out var tiles).ShouldBeTrue();
        tiles.TryRead(0, 1, 1, out _).ShouldBeTrue();

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        tiles.TryRead(0, 1, 1, out _).ShouldBeTrue("a cached block is what the file held when it was read");
        tiles.TryRead(1, 1, 1, out _).ShouldBeFalse("a miss would read a different file");
        tiles.IsStale.ShouldBeTrue();
    }

    [Fact]
    public void WhatTheSourceDoesNotServeIsRefused()
    {
        var folder = _folders.Create("tianwen-fpack-readout-").FullName;
        FpackReadoutTiles.TryOpen(Path.Combine(folder, "missing.fz"), null, out _).ShouldBeFalse();

        var notFits = Path.Combine(folder, "plain.fz");
        File.WriteAllText(notFits, "not a FITS file");
        FpackReadoutTiles.TryOpen(notFits, null, out _).ShouldBeFalse();

        var path = FixturePath();
        var renamed = Path.Combine(folder, "tilecompressed.dat");
        File.Copy(path, renamed);
        FpackReadoutTiles.TryOpen(renamed, null, out _).ShouldBeFalse(".fz is matched on the extension");

        var gzipped = Path.Combine(folder, "tilecompressed.fits.gz");
        using (var input = File.OpenRead(path))
        using (var output = File.Create(gzipped))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            input.CopyTo(gzip);
        }

        ReadoutTileSources.TryOpen(gzipped, null, out _).ShouldBeFalse("a gzip stream cannot be read at an offset");
        ReadoutTileSources.TryOpen(path, null, out var source).ShouldBeTrue("the dispatcher takes a .fz");
        source.ShouldBeOfType<FpackReadoutTiles>();
    }

    [Fact(Timeout = 60_000)]
    public async Task AReleasedFzDocumentAnswersTheReadoutFromTheFileAndRestoresIdenticalPlanes()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = FixturePath();
        var resident = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        var released = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        released.PlaneBudgetBytes = 1;
        await released.DetectStarsAsync(ct);

        released.UnstretchedImage.PlanesResident.ShouldBeFalse("a .fz master has a readout source and a reload stamp");
        released.UnstretchedImage.PlanesNeedReload.ShouldBeTrue();

        for (var c = 0; c < 3; c++)
        {
            var fromFile = ((IPreviewSource)released).SampleAt(c, 17, 11);
            var fromPlanes = ((IPreviewSource)resident).SampleAt(c, 17, 11);
            BitConverter.SingleToInt32Bits(fromFile).ShouldBe(BitConverter.SingleToInt32Bits(fromPlanes), $"channel {c}");
        }

        released.UnstretchedImage.PlanesResident.ShouldBeFalse("the readout restores nothing");
        released.PlaneReloads.ShouldBe(0);

        await released.EnsurePlanesAsync(ct);

        released.PlaneReloads.ShouldBe(1);
        released.UnstretchedImage.PlanesResident.ShouldBeTrue();
        for (var c = 0; c < 3; c++)
        {
            var want = resident.UnstretchedImage.GetChannelSpan(c);
            var got = released.UnstretchedImage.GetChannelSpan(c);
            got.Length.ShouldBe(want.Length);
            for (var i = 0; i < want.Length; i++)
            {
                BitConverter.SingleToInt32Bits(got[i]).ShouldBe(BitConverter.SingleToInt32Bits(want[i]), $"channel {c} sample {i}");
            }
        }
    }
}
