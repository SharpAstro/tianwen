using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
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
/// the steady state of a night. The threshold is half the frame-sized array the call used to allocate,
/// so it fails while that array is still allocated and passes on everything else a call legitimately
/// allocates.
/// </remarks>
[Collection("Allocations")]
public class FramePathAllocationTests
{
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
