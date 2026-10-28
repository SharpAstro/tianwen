using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpAstro.Tiff;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A float TIFF master over the size budget releases its planes after load and reads them back through
/// <c>Image.TryReadTiff</c>, as a plain FITS does (#1853); the cursor readout answers from the file's strips or tiles.
/// </summary>
public class TiffReloadBackedReleaseTests : IDisposable
{
    private const int W = 200, H = 200;

    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    /// <summary>A float RGB master, 16 stars on a noisy sky, in [0, 1], written tiled or as strips, Deflate-compressed.</summary>
    internal static async Task<string> WriteFloatTiffAsync(string folder, string name, bool tiled, int spp = 3)
    {
        var rng = new Random(11);
        var bytes = new byte[W * H * spp * 4];
        var bg = new[] { 0.003f, 0.003f, 0.009f };
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var star = 0f;
                for (var i = 0; i < 16; i++)
                {
                    var (sx, sy) = (20 + (i % 4 * 45), 20 + (i / 4 * 45));
                    var (dx, dy) = (x - sx, y - sy);
                    if (Math.Abs(dx) <= 4 && Math.Abs(dy) <= 4)
                    {
                        star += 0.09f * MathF.Exp(-((dx * dx) + (dy * dy)) / (2f * 1.4f * 1.4f));
                    }
                }

                for (var c = 0; c < spp; c++)
                {
                    var v = bg[c] + star + (float)(rng.NextDouble() * 3e-4);
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((((y * W) + x) * spp + c) * 4), v);
                }
            }
        }

        var path = Path.Combine(folder, name);
        await using var writer = TiffWriter.Create(path);
        await writer.AddPageAsync(bytes, W, H, new TiffPageOptions
        {
            Compression = TiffCompression.Deflate,
            SamplesPerPixel = spp,
            BitsPerSample = 32,
            Photometric = spp == 3 ? TiffPhotometric.Rgb : TiffPhotometric.MinIsBlack,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            Layout = tiled ? TiffLayout.Tiled : TiffLayout.Strip,
            TileWidth = 64,
            TileHeight = 48,
            RowsPerStrip = 40,
        }, TestContext.Current.CancellationToken);
        return path;
    }

    private string Folder() => _folders.Create("tianwen-tiff-reload-").FullName;

    private static async Task<AstroImageDocument> OpenAsync(string path, bool released)
    {
        var ct = TestContext.Current.CancellationToken;
        var document = (await AstroImageDocument.OpenAsync(path, cancellationToken: ct)).ShouldNotBeNull();
        if (released)
        {
            document.PlaneBudgetBytes = 1;
        }

        await document.DetectStarsAsync(ct);
        return document;
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFloatTiffOverTheBudgetHoldsNoPlanesAfterItsLoadTimeReaders(bool tiled)
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var document = await OpenAsync(await WriteFloatTiffAsync(Folder(), "over.tif", tiled), released: true);

        document.ReadoutTiles.ShouldBeOfType<TiffReadoutTiles>();
        document.UnstretchedImage.PlanesResident.ShouldBeFalse();
        document.UnstretchedImage.PlanesNeedReload.ShouldBeTrue();
        document.UnstretchedImage.ChannelCount.ShouldBe(3);
    }

    [Fact(Timeout = 60_000)]
    public async Task AFloatTiffUnderTheBudgetKeepsItsPlanes()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var document = await OpenAsync(await WriteFloatTiffAsync(Folder(), "under.tif", tiled: true), released: false);

        document.UnstretchedImage.PlanesResident.ShouldBeTrue();
        document.PlaneReloads.ShouldBe(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheReadoutAnswersFromTheFileWithNoReload()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var path = await WriteFloatTiffAsync(Folder(), "readout.tif", tiled: true);
        var resident = await OpenAsync(path, released: false);
        var released = await OpenAsync(path, released: true);

        for (var c = 0; c < 3; c++)
        {
            var want = ((IPreviewSource)resident).SampleAt(c, 21, 33);
            var got = ((IPreviewSource)released).SampleAt(c, 21, 33);
            BitConverter.SingleToInt32Bits(got).ShouldBe(BitConverter.SingleToInt32Bits(want), $"channel {c}");
        }

        released.UnstretchedImage.PlanesResident.ShouldBeFalse();
        released.PlaneReloads.ShouldBe(0);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsurePlanesRestoresBitIdenticalPlanesOnceForEveryCaller(bool tiled)
    {
        var path = await WriteFloatTiffAsync(Folder(), "ensure.tif", tiled);
        var ct = TestContext.Current.CancellationToken;
        var resident = await OpenAsync(path, released: false);
        var released = await OpenAsync(path, released: true);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => released.EnsurePlanesAsync(ct).AsTask()));

        released.PlaneReloads.ShouldBe(1, "six callers, one read of the file");
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

    [Fact(Timeout = 60_000)]
    public async Task AFileChangedSinceTheOpenIsRefused()
    {
        var path = await WriteFloatTiffAsync(Folder(), "changed.tif", tiled: true);
        var released = await OpenAsync(path, released: true);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        await Should.ThrowAsync<IOException>(async () => await released.EnsurePlanesAsync(TestContext.Current.CancellationToken));
        released.UnstretchedImage.PlanesResident.ShouldBeFalse();
        float.IsNaN(((IPreviewSource)released).SampleAt(0, 3, 3)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task ACmykTiffTheReadoutDeclinesKeepsItsPlanes()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var path = Path.Combine(Folder(), "cmyk.tif");
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = W, Height = H, Spp = 4, Bps = 8, Photometric = 5, RowsPerStrip = 40 }));

        var document = await OpenAsync(path, released: true);

        document.ReadoutTiles.ShouldBeNull();
        document.UnstretchedImage.PlanesNeedReload.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEightBitTiffKeepsItsRasterEvictionAndTakesNoFileRoute()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var path = Path.Combine(Folder(), "eight.tif");
        File.WriteAllBytes(path, HandBuiltTiff.Build(new HandBuiltTiff.Spec { Width = W, Height = H, Spp = 3, Bps = 8, RowsPerStrip = 40 }));

        var document = await OpenAsync(path, released: true);

        document.ReadoutTiles.ShouldBeNull("a raster-backed document rebuilds its planes in memory");
        document.UnstretchedImage.TryGetSourceRaster(0, out _).ShouldBeTrue();
        document.UnstretchedImage.PlanesNeedReload.ShouldBeFalse();
    }

    private const int BigW = 1200, BigH = 1000;

    [Fact(Timeout = 120_000)]
    public async Task AWholePageStripTiffOverTheSegmentCapIsDeclinedAndKeepsItsPlanes()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var path = Path.Combine(Folder(), "whole-page.tif");
        await using (var writer = TiffWriter.Create(path))
        {
            await writer.AddPageAsync(new byte[BigW * BigH * 4], BigW, BigH, new TiffPageOptions
            {
                Compression = TiffCompression.Deflate,
                SamplesPerPixel = 1,
                BitsPerSample = 32,
                Photometric = TiffPhotometric.MinIsBlack,
                SampleFormat = TiffSampleFormat.IeeeFloat,
                RowsPerStrip = BigH,
            }, TestContext.Current.CancellationToken);
        }

        var document = await OpenAsync(path, released: true);

        document.ReadoutTiles.ShouldBeNull("one 4.8 MB strip is over TiffReadoutTiles.MaxSegmentBytes");
        document.UnstretchedImage.PlanesResident.ShouldBeTrue();
        document.UnstretchedImage.PlanesNeedReload.ShouldBeFalse();
    }

    [Fact(Timeout = 120_000)]
    public async Task ATiffTianWenWritesItselfIsBoundedInStripsSoItIsServedAndReleased()
    {
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        var planes = new[] { new float[BigH, BigW] };
        for (var y = 0; y < BigH; y++)
        {
            for (var x = 0; x < BigW; x++)
            {
                planes[0][y, x] = ((x * 7) + (y * 13)) % 1000 / 1000f;
            }
        }

        var image = new Image(planes, BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta("export", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN,
                SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN));
        var path = Path.Combine(Folder(), "export.tif");
        await image.WriteTiffAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        var document = await OpenAsync(path, released: true);

        document.ReadoutTiles.ShouldBeOfType<TiffReadoutTiles>();
        document.UnstretchedImage.PlanesResident.ShouldBeFalse();
        await document.EnsurePlanesAsync(TestContext.Current.CancellationToken);
        document.UnstretchedImage.PlanesResident.ShouldBeTrue();
    }
}
