using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// What a DAL camera's download makes of the bytes its SDK hands over: every value the sensor can
/// deliver arrives as that value, and a recycled download reads the SDK buffer in place.
/// </summary>
/// <remarks>
/// The 16-bit read used to go through a SIGNED <c>short[]</c>, so a pixel of 32768 or more became
/// a large negative float. ZWO and QHY hand over native-scale values, which a 12- or 14-bit sensor
/// keeps below 32768, so nothing on those showed it; a 16-bit converter (IMX571, IMX455) delivers
/// up to 65535, and Player One left-aligns a 12-bit sensor up to 65520, so on those every pixel
/// above half scale, which is every bright star core, arrived negative. The scripted SDK used to
/// write nothing into the buffer, which is why no test saw it.
/// </remarks>
[Collection("Devices")]
public class DALCameraDownloadTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(1);
    private const int Width = 100, Height = 100;

    [Fact]
    public async Task ASixteenBitFrameReadsBackUnsignedAcrossTheWholeRange()
    {
        var (camera, state, time) = await ScriptedDalCamera.ConnectAsync(output);
        var pixels = new ushort[Width * Height];
        pixels.AsSpan().Fill(1000);
        ushort[] edges = [0, 1, 32767, 32768, 40000, 65520, 65535];
        edges.CopyTo(pixels, 0);
        state.Pixels = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();

        var image = await ExposeAndReadAsync(camera, time);
        try
        {
            var read = image.GetChannelSpan(0);
            for (var i = 0; i < edges.Length; i++)
            {
                read[i].ShouldBe(edges[i], $"a pixel of {edges[i]} ADU must arrive as {edges[i]}");
            }

            read[^1].ShouldBe(1000f);
            image.MaxValue.ShouldBe(65535f, "the frame's peak is its brightest pixel, not 32767");
        }
        finally
        {
            image.Release();
        }
    }

    [Fact]
    public async Task AnEightBitFrameReadsBackAsItsBytes()
    {
        var (camera, state, time) = await ScriptedDalCamera.ConnectAsync(output, formats: [PixelDataFormat.RAW8, PixelDataFormat.RAW16]);
        await camera.SetBitDepthAsync(BitDepth.Int8, TestContext.Current.CancellationToken);
        (await camera.GetBitDepthAsync(TestContext.Current.CancellationToken)).ShouldBe(BitDepth.Int8);

        var pixels = new byte[Width * Height];
        pixels.AsSpan().Fill(17);
        byte[] edges = [0, 1, 127, 128, 200, 255];
        edges.CopyTo(pixels, 0);
        state.Pixels = pixels;

        var image = await ExposeAndReadAsync(camera, time);
        try
        {
            var read = image.GetChannelSpan(0);
            for (var i = 0; i < edges.Length; i++)
            {
                read[i].ShouldBe(edges[i]);
            }

            read[^1].ShouldBe(17f);
            image.MaxValue.ShouldBe(255f);
        }
        finally
        {
            image.Release();
        }
    }

    [Fact]
    public async Task ARecycledDownloadAllocatesNothingTheSizeOfTheFrame()
    {
        var (camera, state, time) = await ScriptedDalCamera.ConnectAsync(output);
        var frameBytes = Width * Height * sizeof(ushort);
        state.Pixels = new byte[frameBytes];

        // The first frame's float[,] goes back to the camera when it is released...
        (await ExposeAndReadAsync(camera, time)).Release();

        // ...so the second download needs no frame-sized allocation at all: it reads the SDK's own
        // buffer in place, into the recycled array. It used to copy it into a new short[] first,
        // 52 MB of garbage for every 26 MP frame.
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure);
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var channel = camera.ImageData;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var buffer = channel.ShouldNotBeNull().Buffer.ShouldNotBeNull();
        try
        {
            allocated.ShouldBeLessThan(frameBytes / 2, "the download reads the SDK buffer in place, into the recycled float[,]");
        }
        finally
        {
            buffer.Release();
        }
    }

    private static async Task<Image> ExposeAndReadAsync(TestDalCameraDriver camera, FakeTimeProviderWrapper time)
    {
        await camera.StartExposureAsync(Exposure, cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(Exposure);
        (await camera.GetImageReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        // GetImageAsync is ICameraDriver's default method, which is the path every consumer takes.
        return (await ((ICameraDriver)camera).GetImageAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}
