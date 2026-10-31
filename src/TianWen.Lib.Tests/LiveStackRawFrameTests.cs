using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The playhead's raw frame of a <see cref="LiveStackPreviewSource"/> (#1883): put where the stack's own registration puts its disk,
/// within a pixel of the true offset, on the scale the stack is shown on, and built only when wanted.
/// </summary>
public class LiveStackRawFrameTests
{
    private const int Size = 64;

    // A smooth disk of radius 9 on a faint sky, its centre at (cx, cy): a planet's limb is not a step.
    private static Image Disk(double cx, double cy)
    {
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                var edge = Math.Clamp((9.0 + 1.5 - r) / 3.0, 0, 1);
                plane[y, x] = (float)(0.02 + (0.7 * edge * edge * (3 - (2 * edge))) * (1 - (0.25 * Math.Min(1, r / 9))));
            }
        }

        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f,
            new ImageMeta("test", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN, ObjectName: "Jupiter"));
    }

    // Frames 0 and 1 hold the disk at (32, 30); frame 2 at (35.4, 28.2): the shift the stack's registration has to find.
    private sealed class Frames : IPlanetaryFrameStream
    {
        public static readonly (double X, double Y) Reference = (32, 30);
        public static readonly (double X, double Y) Moved = (35.4, 28.2);

        public int FrameCount => 3;
        public int Width => Size;
        public int Height => Size;
        public PlanetaryFrameLayout Layout => PlanetaryFrameLayout.Mono;
        public bool HasTimestamps => false;
        public DateTimeOffset? TimestampOf(int index) => null;

        public ValueTask<Image> LoadAsync(int index, CancellationToken cancellationToken = default)
        {
            var (x, y) = index == 2 ? Moved : Reference;
            return ValueTask.FromResult(Disk(x, y));
        }

        public void Dispose() { }
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRawFramesDiskLandsOnTheStacksWithinAPixel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var frames = new Frames();
        var masters = new StackedMasters(frames, options: null, ownsStream: false);
        var stack = await masters.MasterAtAsync(0, ct);
        try
        {
            var placed = await masters.PlacedFrameAsync(2, stack, ct);
            placed.ShouldNotBeNull();
            var (expectDx, expectDy) = (Frames.Moved.X - Frames.Reference.X, Frames.Moved.Y - Frames.Reference.Y);
            placed.Dx.ShouldBe(expectDx, 1.0, "the frame is sampled at (x + Dx, y + Dy) to land on the stack's pixel");
            placed.Dy.ShouldBe(expectDy, 1.0);
            placed.Frame.Width.ShouldBe(Size);
            placed.Frame.Release();
        }
        finally
        {
            stack.Release();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TheSourceBuildsItOnlyWhenWantedAndDrawsItTheShiftNegated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var frames = new Frames();
        await using var source = new LiveStackPreviewSource(new StackedMasters(frames, options: null, ownsStream: false), "raw-test", new SystemTimeProvider());

        source.CanShowRaw.ShouldBeFalse("no stack yet");
        source.RequestFollow(2);
        while (!source.TryPublishMaster())
        {
            await Task.Delay(5, ct);
        }

        source.CanShowRaw.ShouldBeTrue();
        source.Raw.ShouldBeNull("not wanted: no second image is built");

        while (source.Raw is null)
        {
            source.WantRaw(true);
            await Task.Delay(5, ct);
        }

        source.RawGeneration.ShouldBe(1);
        source.Raw.ShouldNotBeNull().Width.ShouldBe(Size);
        // The stack on show is the playhead's, so its disk is on the reference's grid and the drawn offset is the frame's shift, negated.
        source.RawOffset.X.ShouldBe((float)(Frames.Reference.X - Frames.Moved.X), 1f);
        source.RawOffset.Y.ShouldBe((float)(Frames.Reference.Y - Frames.Moved.Y), 1f);

        // The same frame against the same stack is not built again.
        source.WantRaw(true);
        await Task.Delay(50, ct);
        source.WantRaw(true);
        source.RawGeneration.ShouldBe(1);

        source.WantRaw(false);
        source.Raw.ShouldBeNull("let go with the comparison");
    }

    [Fact]
    public void ANodesMastersAndAMasterFileHaveNoFramesToCompare()
    {
        using var node = new LiveStackPreviewSource(new NodeMasters(), "node", new SystemTimeProvider());
        node.CanShowRaw.ShouldBeFalse();
    }
}
