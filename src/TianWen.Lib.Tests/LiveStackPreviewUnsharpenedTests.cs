using System;
using System.Linq;
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
/// The unsharpened master of a <see cref="LiveStackPreviewSource"/> (#1883): built only when wanted, on the scale the sharpened one is
/// shown on, and its generation moves with the master, never with the sharpening.
/// </summary>
public class LiveStackPreviewUnsharpenedTests
{
    // A mono master with a peak well above 1 (the stack's own linear scale), a new image per ask as every master a source is handed is.
    private sealed class Masters(int frames) : ILiveMasterSource
    {
        public int FrameCount { get; set; } = frames;
        public bool HasTimestamps => false;
        public DateTimeOffset? TimestampOf(int index) => null;
        public (int Width, int Height, int Channels, SensorType Sensor) ExpectedGeometry => (32, 32, 1, SensorType.Monochrome);

        public Task<Image> MasterAtAsync(int playhead, CancellationToken cancellationToken)
        {
            var plane = new float[32, 32];
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var (dx, dy) = (x - 16, y - 16);
                    plane[y, x] = (dx * dx) + (dy * dy) < 100 ? 600f + (40f * ((x * 7) % 5)) : 20f;
                }
            }

            return Task.FromResult(new Image([plane], BitDepth.Float32, 800f, 0f, 0f,
                new ImageMeta("test", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), FrameType.Light, "",
                    0f, 0f, -1, FocuserPositions.None, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
                    RowOrder.TopDown, float.NaN, float.NaN, ObjectName: "Jupiter")));
        }

        public void Dispose() { }
    }

    private static async Task PublishedAsync(LiveStackPreviewSource source, CancellationToken ct)
    {
        while (!source.TryPublishMaster())
        {
            await Task.Delay(5, ct);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task TheUnsharpenedMasterIsBuiltOnlyWhenWantedAndItsGenerationMovesOnlyWithTheMaster()
    {
        var ct = TestContext.Current.CancellationToken;
        var masters = new Masters(frames: 2);
        await using var source = new LiveStackPreviewSource(masters, "unsharpened-test", new SystemTimeProvider());

        // Not wanted: a master is built and no second one beside it.
        source.RequestFollow(0);
        await PublishedAsync(source, ct);
        source.Unsharpened.ShouldBeNull();
        source.UnsharpenedGeneration.ShouldBe(0);

        // Wanted after the fact: one cheap pass off the cached master builds it, the stack not run again.
        source.SetSharpen(WaveletSharpenOptions.Uniform(6, 1f));
        await PublishedAsync(source, ct);
        source.WantUnsharpened(true);
        await PublishedAsync(source, ct);
        var first = source.Unsharpened.ShouldNotBeNull();
        var generation = source.UnsharpenedGeneration;
        generation.ShouldBe(1);
        source.StacksPublished.ShouldBe(1);

        // The scale is the sharpened master's: with identity gains the two are the same master, sample for sample, and normalised
        // (the stack's own peak of 800 is brought to 1, never left in its ADU).
        first.Width.ShouldBe(32);
        first.GetChannelData(0).ToArray().ShouldBe(source.GetChannelData(0).ToArray());
        first.GetChannelData(0).ToArray().Max().ShouldBeInRange(0.5f, 1f);

        // A dial move re-sharpens and leaves the unsharpened master, and its generation, alone.
        source.SetSharpen(WaveletSharpenOptions.Uniform(6, 2.5f));
        await PublishedAsync(source, ct);
        source.Unsharpened.ShouldBeSameAs(first);
        source.UnsharpenedGeneration.ShouldBe(generation);

        // A new master is a new unsharpened master.
        source.RequestFollow(1);
        await PublishedAsync(source, ct);
        source.StacksPublished.ShouldBe(2);
        source.Unsharpened.ShouldNotBeNull().ShouldNotBeSameAs(first);
        source.UnsharpenedGeneration.ShouldBe(generation + 1);

        // Not wanted any more: it is let go.
        source.WantUnsharpened(false);
        source.Unsharpened.ShouldBeNull();
    }
}
