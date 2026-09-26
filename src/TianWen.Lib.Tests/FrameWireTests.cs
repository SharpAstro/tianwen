using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A linear frame on the wire (<see cref="FrameWire"/>, P4 of docs/plans/hardware-in-the-server.md, #931): every sample
/// comes back bit for bit, packed to 16 bits exactly when that loses nothing, with its metadata and each channel's filter
/// and range; and the reader recycles its planes.
/// </summary>
public class FrameWireTests(ITestOutputHelper output)
{
    private static Image Frame(int width, int height, Func<int, int, float> sample, int channels = 1)
    {
        var planes = ImmutableArray.CreateBuilder<Channel>(channels);
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    plane[y, x] = sample(x, y) + c * 1000;
                }
            }
            // A second channel's filter is one no catalogue knows, whose text is its identity.
            planes.Add(new Channel(plane, channels == 1 ? Filter.Luminance : c == 0 ? Filter.Red : Filter.FromName("Vendor XYZ 7nm"), c * 1000, 4000 + c * 1000, (byte)c));
        }
        var meta = new ImageMeta("Frame", new DateTimeOffset(2026, 9, 27, 21, 30, 0, TimeSpan.Zero), TimeSpan.FromSeconds(120), FrameType.Light,
            "Fake Camera", 3.76f, 3.76f, 1000, 1, Filter.Luminance, 1, 1, float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, -10, 0.5f);
        return new Image(planes.MoveToImmutable(), BitDepth.Int16, pedestal: 12, meta);
    }

    private static async Task<(Image Read, long Bytes)> RoundTripAsync(Image image, FrameReader? reader = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var wire = new MemoryStream();
        await FrameWire.WriteAsync(image, wire, ct);
        var bytes = wire.Length;
        wire.Position = 0;
        return (await (reader ?? new FrameReader()).ReadAsync(wire, ct), bytes);
    }

    private static void ShouldBeBitExact(Image read, Image sent)
    {
        (read.Width, read.Height, read.ChannelCount).ShouldBe((sent.Width, sent.Height, sent.ChannelCount));
        for (var c = 0; c < sent.ChannelCount; c++)
        {
            var got = read.GetChannelSpan(c);
            var want = sent.GetChannelSpan(c);
            for (var i = 0; i < want.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(got[i]) != BitConverter.SingleToInt32Bits(want[i]))
                {
                    throw new ShouldAssertException($"channel {c} sample {i}: sent {want[i]:R}, read {got[i]:R}");
                }
            }
        }
    }

    [Fact]
    public async Task AFrameInWholeAduGoesAsSixteenBitsAndComesBackBitExact()
    {
        var sent = Frame(97, 61, (x, y) => (x * 131 + y * 977) % 65536);

        FrameWire.PacksAsUInt16(sent).ShouldBeTrue();
        var (read, bytes) = await RoundTripAsync(sent);

        ShouldBeBitExact(read, sent);
        bytes.ShouldBeLessThan(97 * 61 * sizeof(float), "a whole-ADU frame went as floats");
    }

    [Fact]
    public async Task AFrameWithAFractionAnywhereGoesAsFloatsBitExact()
    {
        // Only the very last sample is not whole: the check runs to it and the frame goes as floats.
        var sent = Frame(97, 61, (x, y) => x == 96 && y == 60 ? 0.5f : x + y);

        FrameWire.PacksAsUInt16(sent).ShouldBeFalse();
        var (read, bytes) = await RoundTripAsync(sent);

        ShouldBeBitExact(read, sent);
        bytes.ShouldBeGreaterThanOrEqualTo(97 * 61 * sizeof(float));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(-1f)]
    [InlineData(65536f)]
    public void ASampleOutsideSixteenBitsKeepsTheFrameInFloats(float outlier)
    {
        FrameWire.PacksAsUInt16(Frame(33, 17, (x, y) => x == 5 && y == 5 ? outlier : 100)).ShouldBeFalse();
    }

    [Fact]
    public async Task EveryChannelCarriesItsFilterRangeAndPlaceAndTheImageItsMetadata()
    {
        var sent = Frame(40, 30, (x, y) => x * y, channels: 2);

        var (read, _) = await RoundTripAsync(sent);

        ShouldBeBitExact(read, sent);
        read.ImageMeta.ShouldBe(sent.ImageMeta);
        (read.BitDepth, read.Pedestal, read.SamplesAreUnitReferred).ShouldBe((sent.BitDepth, sent.Pedestal, sent.SamplesAreUnitReferred));
        for (var c = 0; c < 2; c++)
        {
            var (got, want) = (read.GetChannel(c), sent.GetChannel(c));
            (got.Filter, got.MinValue, got.MaxValue, got.Index).ShouldBe((want.Filter, want.MinValue, want.MaxValue, want.Index));
        }
    }

    // A client showing frame after frame: the second frame is read into the first one's plane, not a new one.
    [Fact]
    public async Task TheReaderReadsTheNextFrameIntoAReleasedFramesPlane()
    {
        var reader = new FrameReader();
        var (first, _) = await RoundTripAsync(Frame(64, 48, (x, y) => x), reader);
        var plane = first.GetChannelArray(0);

        first.Release();
        reader.FreePlanes.ShouldBe(1);
        var (second, _) = await RoundTripAsync(Frame(64, 48, (x, y) => y), reader);

        second.GetChannelArray(0).ShouldBeSameAs(plane);
        reader.FreePlanes.ShouldBe(0);
        second.Release();
    }

    // The proof the plan asks for: what the camera delivered is what the client reads.
    [Fact(Timeout = 60_000)]
    public async Task ACamerasOwnFrameComesBackPixelExact()
    {
        var ct = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 1), external.BuildServiceProvider());
        await camera.ConnectAsync(ct);
        await camera.StartExposureAsync(TimeSpan.FromSeconds(2), cancellationToken: ct);
        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), ct);
        (await camera.GetImageReadyAsync(ct)).ShouldBeTrue();
        var sent = (await ((ICameraDriver)camera).GetImageAsync(ct)).ShouldNotBeNull();

        try
        {
            var (read, bytes) = await RoundTripAsync(sent);
            output.WriteLine($"{sent.Width}x{sent.Height}x{sent.ChannelCount}: {bytes:N0} bytes, {(FrameWire.PacksAsUInt16(sent) ? "16-bit" : "float")}");

            ShouldBeBitExact(read, sent);
            read.ImageMeta.ShouldBe(sent.ImageMeta);
            read.Release();
        }
        finally
        {
            sent.Release();
        }
    }
}
