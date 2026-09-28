using Shouldly;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A stream's frames through shared memory (P4b of docs/plans/hardware-in-the-server.md, #932): a frame comes out of its
/// slot bit for bit, exactly the bytes the socket carries; a slot written again while it waited is dropped, never shown
/// half; and nothing a reader does holds the writer up.
/// </summary>
public class FrameSlotTests : IDisposable
{
    // Two test runs at once must never share a section, and a Unix section falls back to this where there is no /dev/shm.
    private readonly string _prefix = $"tianwen-test-{Guid.NewGuid():N}";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("twslots");

    public void Dispose()
    {
        SharedMemorySection.RemoveStale(_prefix, _directory.FullName);
        _directory.Delete(recursive: true);
    }

    private FrameSlotWriter Writer() => new FrameSlotWriter(_prefix, _directory.FullName);

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
            planes.Add(new Channel(plane, channels == 1 ? Filter.Luminance : c == 0 ? Filter.Red : Filter.Green, c * 1000, 4000 + c * 1000, (byte)c));
        }
        var meta = new ImageMeta("Frame", new DateTimeOffset(2026, 9, 28, 21, 30, 0, TimeSpan.Zero), TimeSpan.FromMilliseconds(10), FrameType.Light,
            "Fake Camera", 2.9f, 2.9f, 100, 1, Filter.Luminance, 1, 1, float.NaN, SensorType.RGGB, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes.MoveToImmutable(), BitDepth.Int16, pedestal: 0, meta);
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
        read.ImageMeta.ShouldBe(sent.ImageMeta);
    }

    [Theory]
    [InlineData(false)] // whole ADU: goes as 16 bits
    [InlineData(true)]  // a fraction: goes as floats
    public void AFrameComesOutOfItsSlotBitExact(bool fractional)
    {
        var sent = Frame(641, 359, (x, y) => (x * 7 + y * 13) % 4096 + (fractional ? 0.25f : 0f), channels: 2);
        using var writer = Writer();
        using var slots = new FrameSlotReader();

        var slot = writer.Write(7, sent);
        var read = slots.TryRead(slot, new FrameReader()).ShouldNotBeNull("nothing wrote the slot again");

        ShouldBeBitExact(read, sent);
        read.Release();
    }

    [Fact]
    public async Task ASlotHoldsTheBytesTheSocketCarries()
    {
        var sent = Frame(320, 200, (x, y) => x ^ y);
        using var wire = new MemoryStream();
        await FrameWire.WriteAsync(sent, wire, TestContext.Current.CancellationToken);

        var prepared = FrameWire.Prepare(sent);
        var bytes = new byte[prepared.Length];
        FrameWire.Write(sent, prepared, bytes);

        bytes.ShouldBe(wire.ToArray(), "one format for both carriers, so a client reads either with the same code");
    }

    [Fact]
    public void ASlotWrittenAgainBeforeItWasReadIsDroppedNotShownHalf()
    {
        using var writer = Writer();
        using var slots = new FrameSlotReader();
        var first = writer.Write(1, Frame(64, 32, (x, y) => 1));
        writer.Write(2, Frame(64, 32, (x, y) => 2));
        writer.Write(3, Frame(64, 32, (x, y) => 3)); // two slots: this one lands where frame 1 waited

        var reader = new FrameReader();
        slots.TryRead(first, reader).ShouldBeNull("frame 1's slot holds frame 3 now");
        slots.Torn.ShouldBe(1);
        reader.FreePlanes.ShouldBe(0, "a slot known to be written again is not even copied");
    }

    [Fact]
    public void AFrameASlotHoldsIsAnsweredFromThereUnwritten()
    {
        using var writer = Writer();
        var frame = Frame(64, 32, (x, y) => x);

        var first = writer.Write(5, frame);
        var again = writer.Write(5, frame);

        again.ShouldBe(first, "the same frame, the same slot and generation: a client asking twice costs no copy");
    }

    [Fact]
    public void AFrameThatDoesNotFitMakesTheSectionAgainAndTheReaderFollows()
    {
        using var writer = Writer();
        using var slots = new FrameSlotReader();
        var small = writer.Write(1, Frame(64, 32, (x, y) => x));
        slots.TryRead(small, new FrameReader()).ShouldNotBeNull().Release();

        var sent = Frame(1024, 768, (x, y) => y);
        var large = writer.Write(2, sent);

        large.Map.ShouldNotBe(small.Map, "a window the section cannot hold makes a larger one");
        writer.SectionsMade.ShouldBe(2);
        var read = slots.TryRead(large, new FrameReader()).ShouldNotBeNull();
        ShouldBeBitExact(read, sent);
        read.Release();
    }

    /// <summary>
    /// A reader that took a slot's name and never copied it, a client stalled or killed mid-read, holds nothing: the writer
    /// writes on, and the slot it named reads as gone.
    /// </summary>
    [Fact]
    public void AStalledReaderNeverHoldsTheWriterUp()
    {
        using var writer = Writer();
        using var stalled = new FrameSlotReader();
        var named = writer.Write(1, Frame(64, 32, (x, y) => 1));
        stalled.TryRead(writer.Write(2, Frame(64, 32, (x, y) => 2)), new FrameReader()).ShouldNotBeNull().Release(); // it has the section mapped

        for (var number = 3; number < 103; number++)
        {
            writer.Write(number, Frame(64, 32, (x, y) => number));
        }

        stalled.TryRead(named, new FrameReader()).ShouldBeNull();
        writer.SectionsMade.ShouldBe(1, "a hundred writes into the same two slots, none waiting on the reader");
    }

    [Fact]
    public void TheReaderRecyclesItsPlanesFromSlotToSlot()
    {
        using var writer = Writer();
        using var slots = new FrameSlotReader();
        var reader = new FrameReader();

        var first = slots.TryRead(writer.Write(1, Frame(64, 32, (x, y) => 1)), reader).ShouldNotBeNull();
        var plane = first.GetChannelArray(0);
        first.Release();
        var second = slots.TryRead(writer.Write(2, Frame(64, 32, (x, y) => 2)), reader).ShouldNotBeNull();

        second.GetChannelArray(0).ShouldBeSameAs(plane, "a frame from a slot is read into a plane the last one gave back");
        second.Release();
    }
}
