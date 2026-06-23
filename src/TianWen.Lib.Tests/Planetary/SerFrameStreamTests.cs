using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Ser;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class SerFrameStreamTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"ser-stream-{Guid.NewGuid():N}.ser");

    /// <summary>Writes a 16-bit synthetic SER. Each frame is <c>width*height*planes</c> ushorts.</summary>
    private static void WriteSer(string path, int width, int height, SerColorId colorId, ushort[][] frames,
        DateTimeOffset[]? timestamps = null)
    {
        using var writer = new SerWriter(path, width, height, colorId, pixelDepthPerPlane: 16);
        for (var i = 0; i < frames.Length; i++)
        {
            var bytes = MemoryMarshal.AsBytes<ushort>(frames[i]);
            if (timestamps is not null)
            {
                writer.AppendFrame(bytes, timestamps[i]);
            }
            else
            {
                writer.AppendFrame(bytes);
            }
        }
    }

    [Fact]
    public async Task Bayer_source_yields_split_cfa_half_resolution()
    {
        var path = NewTempPath();
        try
        {
            // Two 4x4 RGGB frames; frame 1 = frame 0 + a constant so they differ.
            var f0 = new ushort[16];
            var f1 = new ushort[16];
            for (var i = 0; i < 16; i++)
            {
                f0[i] = (ushort)(i * 1000);
                f1[i] = (ushort)((i * 1000) + 500);
            }

            WriteSer(path, 4, 4, SerColorId.BayerRGGB, [f0, f1]);

            using var stream = SerFrameStream.Open(path);

            stream.Layout.ShouldBe(PlanetaryFrameLayout.SplitCfa);
            stream.FrameCount.ShouldBe(2);
            stream.Width.ShouldBe(2);
            stream.Height.ShouldBe(2);

            var img = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
            img.ChannelCount.ShouldBe(4);
            img.Width.ShouldBe(2);
            img.Height.ShouldBe(2);
            // R sub-plane top-left = mosaic (0,0) = raw 0 -> 0.0; values stay in [0,1].
            img[0, 0, 0].ShouldBe(0f);
            for (var c = 0; c < 4; c++)
            {
                for (var y = 0; y < 2; y++)
                {
                    for (var x = 0; x < 2; x++)
                    {
                        img[c, y, x].ShouldBeInRange(0f, 1f);
                    }
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bayer_source_without_split_yields_full_resolution_mosaic()
    {
        var path = NewTempPath();
        try
        {
            WriteSer(path, 4, 4, SerColorId.BayerRGGB, [new ushort[16]]);

            using var stream = SerFrameStream.Open(path, splitBayer: false);

            stream.Layout.ShouldBe(PlanetaryFrameLayout.BayerMosaic);
            stream.Width.ShouldBe(4);
            stream.Height.ShouldBe(4);

            var img = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
            img.ChannelCount.ShouldBe(1);
            img.Width.ShouldBe(4);
            img.ImageMeta.SensorType.ShouldBe(SensorType.RGGB);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Mono_source_yields_single_full_resolution_plane()
    {
        var path = NewTempPath();
        try
        {
            WriteSer(path, 4, 3, SerColorId.Mono, [new ushort[12]]);

            using var stream = SerFrameStream.Open(path);

            stream.Layout.ShouldBe(PlanetaryFrameLayout.Mono);
            stream.Width.ShouldBe(4);
            stream.Height.ShouldBe(3);

            var img = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
            img.ChannelCount.ShouldBe(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rgb_source_yields_three_planes()
    {
        var path = NewTempPath();
        try
        {
            // 2x2 RGB -> 4 pixels * 3 planes = 12 samples.
            WriteSer(path, 2, 2, SerColorId.Rgb, [new ushort[12]]);

            using var stream = SerFrameStream.Open(path);

            stream.Layout.ShouldBe(PlanetaryFrameLayout.Rgb);
            stream.Width.ShouldBe(2);
            stream.Height.ShouldBe(2);

            var img = await stream.LoadAsync(0, TestContext.Current.CancellationToken);
            img.ChannelCount.ShouldBe(3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Timestamps_round_trip_and_out_of_range_is_null()
    {
        var path = NewTempPath();
        try
        {
            var t0 = new DateTimeOffset(2024, 12, 15, 12, 33, 50, TimeSpan.Zero);
            var t1 = t0.AddMilliseconds(40);
            WriteSer(path, 2, 2, SerColorId.Mono, [new ushort[4], new ushort[4]], [t0, t1]);

            using var stream = SerFrameStream.Open(path);

            stream.HasTimestamps.ShouldBeTrue();
            stream.TimestampOf(0)!.Value.UtcDateTime.ShouldBe(t0.UtcDateTime);
            stream.TimestampOf(1)!.Value.UtcDateTime.ShouldBe(t1.UtcDateTime);
            stream.TimestampOf(0)!.Value.ShouldBeLessThan(stream.TimestampOf(1)!.Value);
            stream.TimestampOf(2).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_honors_cancellation()
    {
        var path = NewTempPath();
        try
        {
            WriteSer(path, 2, 2, SerColorId.Mono, [new ushort[4]]);
            using var stream = SerFrameStream.Open(path);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(async () => await stream.LoadAsync(0, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
