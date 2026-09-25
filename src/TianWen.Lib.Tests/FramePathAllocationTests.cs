using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Every captured sub goes through a FITS write and a star detection. Neither may allocate an array
/// the size of the frame: that garbage, 52 MB for the quantised plane and 104 MB for a colour
/// camera's mono debayer at 26 MP, is what the session used to sweep up with a forced full GC after
/// every write.
/// </summary>
/// <remarks>
/// Each test warms the call once, which fills the pool and the JIT, and then measures the SECOND call,
/// the steady state of a night. The pool is emptied before each test (the constructor says why). The threshold is half the frame-sized array the call used to allocate,
/// so it fails while that array is still allocated and passes on everything else a call legitimately
/// allocates.
/// </remarks>
[Collection("Allocations")]
public class FramePathAllocationTests
{
    // Every test starts from an EMPTY pool, because warming once and measuring the second call needs
    // the pool to take the warm-up's planes back. The pool trims only above 70 % memory load, so after
    // thousands of tests on a roomy machine its byte budget can be full of other shapes and refuse those
    // returns: on 2026-09-24 (#759) three of these failed that way on CI's arm64 leg and two on x64,
    // and a pool filled to within 64 KiB of its budget failed five of eight here until this ran.
    public FramePathAllocationTests()
    {
        Array2DPool<float>.Clear();
    }

    [Fact]
    public async Task StarDetectionOnAColourFrameAllocatesNoFrameSizedPlane()
    {
        const int width = 1024, height = 768;
        var image = RggbStarField(width, height);
        var ct = TestContext.Current.CancellationToken;

        (await image.FindStarsAsync(0, snrMin: 10f, cancellationToken: ct)).Count.ShouldBeGreaterThan(0, "the frame has stars to find");
        image.InvalidateStarListCache();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stars = await image.FindStarsAsync(0, snrMin: 10f, cancellationToken: ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        stars.Count.ShouldBeGreaterThan(0);
        var plane = (long)width * height * sizeof(float);
        allocated.ShouldBeLessThan(plane / 2,
            $"a colour frame's mono debayer is rented, not allocated: {allocated:N0} bytes against a {plane:N0}-byte plane");
    }

    [Fact]
    public void ASixteenBitFitsWriteAllocatesNoFrameSizedPlane()
    {
        const int width = 3000, height = 2000;
        var image = SixteenBitFrame(width, height);
        var path = Path.Combine(Path.GetTempPath(), $"tianwen-alloc-{Guid.NewGuid():N}.fits");
        try
        {
            image.WriteToFitsFile(path);

            var before = GC.GetTotalAllocatedBytes(precise: true);
            image.WriteToFitsFile(path);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            var quantised = (long)width * height * sizeof(short);
            allocated.ShouldBeLessThan(quantised / 2,
                $"the quantised plane is rented, not allocated: {allocated:N0} bytes against a {quantised:N0}-byte plane");

            Image.TryReadFitsFile(path, out var readBack).ShouldBeTrue();
            readBack.ShouldNotBeNull().GetChannelSpan(0)[width + 1].ShouldBe(image.GetChannelSpan(0)[width + 1], "the rented plane wrote the same pixels");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void APooledSixteenBitFitsReadAllocatesNoFrameSizedArray()
    {
        // The read side of the test above: the stacker reads hundreds of subs pooled, and each read used
        // to allocate FITS.Lib's typed array (the whole frame again, as shorts) and a 2 MB read-ahead
        // buffer before converting a sample. Counted on this thread, since the read is synchronous and
        // other collections allocate in parallel.
        const int width = 3000, height = 2000;
        var image = SixteenBitFrame(width, height);
        var path = Path.Combine(Path.GetTempPath(), $"tianwen-alloc-{Guid.NewGuid():N}.fits");
        try
        {
            image.WriteToFitsFile(path);
            Image.TryReadFitsFile(path, out var warm, out _, pooled: true).ShouldBeTrue();
            warm.Release();

            var before = GC.GetAllocatedBytesForCurrentThread();
            Image.TryReadFitsFile(path, out var read, out _, pooled: true).ShouldBeTrue();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            var typed = (long)width * height * sizeof(short);
            allocated.ShouldBeLessThan(typed / 2,
                $"the read goes straight into the rented plane: {allocated:N0} bytes against a {typed:N0}-byte typed array");
            read.GetChannelSpan(0)[width + 1].ShouldBe(image.GetChannelSpan(0)[width + 1], "the same pixels");
            read.Release();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AColourLiveMasterAllocatesItsColourPlanesAndNoMosaic()
    {
        // A split-CFA master merges its four sub-planes back into a mosaic only to demosaic it once; the
        // RGB result is the master, and the mosaic between them used to be a new full-size plane per master.
        const int sub = 256;
        var planes = Image.CreateChannelData(4, sub, sub);
        var rng = new Random(5);
        foreach (var plane in planes)
        {
            for (var y = 0; y < sub; y++)
            {
                for (var x = 0; x < sub; x++)
                {
                    plane[y, x] = (float)rng.NextDouble();
                }
            }
        }

        var stacked = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, Meta(SensorType.RGGB));
        var ct = TestContext.Current.CancellationToken;
        _ = await PlanetaryMaster.MergeAndDemosaicAsync(stacked, PlanetaryFrameLayout.SplitCfa, ct);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var master = await PlanetaryMaster.MergeAndDemosaicAsync(stacked, PlanetaryFrameLayout.SplitCfa, ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        master.ChannelCount.ShouldBe(3);
        var fullPlane = 4L * sub * sub * sizeof(float);
        TestContext.Current.TestOutputHelper?.WriteLine($"colour live master from {sub} x {sub} sub-planes: {allocated} bytes, a full plane is {fullPlane}");
        allocated.ShouldBeLessThan((fullPlane * 7) / 2,
            $"three colour planes for the master and no mosaic: {allocated:N0} bytes against {fullPlane:N0} a plane");
    }

    [Fact]
    public async Task LumaStretchStatisticsAllocateNoLumaPlane()
    {
        // Every colour document (a live master on each publish) takes a luminance statistic, and the luma
        // plane it is taken on used to be a new full-size array per call, read once for a median and a MAD.
        const int n = 512;
        var planes = Image.CreateChannelData(3, n, n);
        var rng = new Random(9);
        foreach (var plane in planes)
        {
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    plane[y, x] = (float)rng.NextDouble();
                }
            }
        }

        var image = new Image(planes, BitDepth.Float32, 1f, 0f, 0f, Meta(SensorType.Color));
        var ct = TestContext.Current.CancellationToken;
        var first = await image.GetLumaStretchStatsAsync(ct);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var again = await image.GetLumaStretchStatsAsync(ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        TestContext.Current.TestOutputHelper?.WriteLine($"luma stretch statistics over {n} x {n}: {allocated} bytes");
        again.ShouldBe(first, "a rented luma plane gives the same statistic");
        var lumaPlane = (long)n * n * sizeof(float);
        allocated.ShouldBeLessThan(lumaPlane / 2, $"the luma plane is rented: {allocated:N0} bytes against a {lumaPlane:N0}-byte plane");
    }

    [Fact]
    public void AMedianAndMadAllocateNoHistogram()
    {
        // Only two numbers leave GetPedestralMedianAndMADScaledToUnit, yet it built a whole histogram for them:
        // 65,536 bins, 256 KB for a unit-scaled float image, per call. The live preview makes one per channel
        // per frame (StretchSolver.CollectPerChannelStats), and every document several.
        const int n = 512;
        var plane = new float[n, n];
        var rng = new Random(13);
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                plane[y, x] = (float)rng.NextDouble();
            }
        }

        var image = new Image([plane], BitDepth.Float32, 1f, 0f, 0f, Meta(SensorType.Monochrome));
        var first = image.GetPedestralMedianAndMADScaledToUnit(0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var again = image.GetPedestralMedianAndMADScaledToUnit(0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Current.TestOutputHelper?.WriteLine($"median and MAD over {n} x {n}: {allocated} bytes");
        again.ShouldBe(first, "rented bins give the same statistic");
        var statistics = image.Statistics(0, removePedestral: true);
        again.Median.ShouldBe(statistics.Median.ShouldNotBeNull() / statistics.RescaledMaxValue.ShouldNotBeNull(), "and the one Statistics gives");
        allocated.ShouldBeLessThan(16 * 1024, "the 256 KB histogram is rented");
    }

    private static ImageMeta Meta(SensorType sensorType) => new ImageMeta(
        "alloc", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, sensorType, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    /// <summary>Gaussian stars on a noisy RGGB mosaic, enough for a detection to find.</summary>
    private static Image RggbStarField(int width, int height)
    {
        const float background = 1000f, amplitude = 20000f, twoSigmaSq = 2f * 0.9f * 0.9f;
        var rng = new Random(11);
        var stars = new (float X, float Y)[12];
        for (var i = 0; i < stars.Length; i++)
        {
            stars[i] = (40f + (float)rng.NextDouble() * (width - 80), 40f + (float)rng.NextDouble() * (height - 80));
        }

        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = 0f;
                foreach (var (sx, sy) in stars)
                {
                    var dx = x - sx;
                    var dy = y - sy;
                    var r2 = (dx * dx) + (dy * dy);
                    if (r2 < 64f)
                    {
                        v += amplitude * MathF.Exp(-r2 / twoSigmaSq);
                    }
                }

                var gain = ((y & 1), (x & 1)) switch { (0, 0) => 1.00f, (1, 1) => 0.55f, _ => 0.80f };
                data[y, x] = background + (v * gain) + (float)(rng.NextDouble() * 16.0 - 8.0);
            }
        }

        return new Image([data], BitDepth.Float32, background + amplitude, background - 8f, 0f, Meta(SensorType.RGGB));
    }

    /// <summary>
    /// The hosted preview debayers a colour frame on every request, and the guider's is requested per
    /// guide frame by every remote client, so three fresh planes were 12 bytes a pixel of garbage each
    /// time. Rented, the planes are not the garbage; the JPEG's input and output still are.
    /// </summary>
    [Fact]
    public async Task AColourPreviewDebayersIntoRentedPlanes()
    {
        const int width = 512, height = 384;
        var image = RggbStarField(width, height);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            await PreviewEncoder.EncodeJpegAsync(image, PreviewEncoder.DefaultQuality, scale: 0.5, ct);
        }

        // The FEWEST bytes of several calls. ArrayPool keeps a returned buffer in the returning thread's
        // own slot first, so one call whose Task.Run landed on another thread can miss the RGBA raster's
        // rent (a 1 MB array here) and read as garbage it is not. A cost every frame pays shows in every
        // call, so the minimum still carries it.
        var allocated = long.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var jpeg = await PreviewEncoder.EncodeJpegAsync(image, PreviewEncoder.DefaultQuality, scale: 0.5, ct);
            allocated = Math.Min(allocated, GC.GetTotalAllocatedBytes(precise: true) - before);
            jpeg.Length.ShouldBeGreaterThan(0);
        }

        var planes = 3L * width * height * sizeof(float);
        TestContext.Current.TestOutputHelper?.WriteLine($"{width}x{height} mosaic preview at 0.5: {allocated:N0} bytes, against {planes:N0} of planes");
        allocated.ShouldBeLessThan(planes / 2,
            $"the debayer's planes are rented, not allocated: {allocated:N0} bytes against {planes:N0} of planes");
    }

    /// <summary>
    /// A plate solver bins the frame it detects on, on every polar-alignment refine, and a new binned
    /// frame was a quarter of the frame at factor 2: 26 MB a refine on a 26 MP sensor. Rented, it must be
    /// exactly what <see cref="Image.Downsample"/> gives, a NaN block and the binned metadata included,
    /// whatever the planes held before, and it must allocate no plane.
    /// </summary>
    [Fact]
    public void ABinnedDetectionFrameIsRentedAndIsExactlyTheDownsample()
    {
        const int width = 1024, height = 768, factor = 2;
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = ((y * 131) + (x * 17)) % 65536;
            }
        }
        // One block wholly NaN (it stays NaN) and one partly (the rest of it averages).
        (data[10, 10], data[10, 11], data[11, 10], data[11, 11], data[20, 21]) = (float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
        var image = new Image([data], BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.Monochrome));

        var expected = image.Downsample(factor);

        // A plane of exactly the binned shape, full of junk, handed to the pool for the rent to take.
        var junk = Array2DPool<float>.Rent(height / factor, width / factor);
        System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref junk[0, 0], junk.Length).Fill(12345f);
        Array2DPool<float>.Return(junk);

        using (var rented = image.DownsampleRented(factor))
        {
            rented.Image.GetChannelSpan(0).SequenceEqual(expected.GetChannelSpan(0)).ShouldBeTrue();
            float.IsNaN(rented.Image.GetChannelSpan(0)[5 * (width / factor) + 5]).ShouldBeTrue("premise: the NaN block stayed NaN");
            rented.Image.ImageMeta.ShouldBe(expected.ImageMeta);
            (rented.Image.Width, rented.Image.Height, rented.Image.MaxValue, rented.Image.MinValue)
                .ShouldBe((expected.Width, expected.Height, expected.MaxValue, expected.MinValue));
        }

        image.DownsampleRented(factor).Dispose();
        var before = GC.GetAllocatedBytesForCurrentThread();
        image.DownsampleRented(factor).Dispose();
        var rentedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        _ = image.Downsample(factor);
        var allocatingBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var plane = (long)(width / factor) * (height / factor) * sizeof(float);
        TestContext.Current.TestOutputHelper?.WriteLine($"{width}x{height} at factor {factor}: Downsample {allocatingBytes:N0} bytes, rented {rentedBytes:N0}");
        allocatingBytes.ShouldBeGreaterThanOrEqualTo(plane, "premise: the allocating downsample makes a plane");
        rentedBytes.ShouldBeLessThan(1024L);
    }

    /// <summary>
    /// A plane returned to the pool twice is handed to two renters at once. Disposing a rented image
    /// again must not return anything; two rents of its shape afterwards must get two planes.
    /// </summary>
    [Fact]
    public void ARentedImageReturnsItsPlanesOnceHoweverOftenItIsDisposed()
    {
        // A shape nothing else in the suite rents, so the two rents below see only this test's return.
        var image = new Image([new float[46, 74]], BitDepth.Float32, 1f, 0f, 0f, Meta(SensorType.Monochrome));

        var rented = image.DownsampleRented(2);
        rented.Dispose();
        rented.Dispose();

        var first = Array2DPool<float>.Rent(23, 37);
        var second = Array2DPool<float>.Rent(23, 37);
        second.ShouldNotBeSameAs(first);
        Array2DPool<float>.Return(first);
        Array2DPool<float>.Return(second);
    }

    /// <summary>A mono frame of whole ADU across the 16-bit range, as a camera hands it over.</summary>
    private static Image SixteenBitFrame(int width, int height)
    {
        var data = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = ((y * 131) + (x * 17)) % 65536;
            }
        }

        return new Image([data], BitDepth.Int16, 65535f, 0f, 0f, Meta(SensorType.Monochrome));
    }
}
