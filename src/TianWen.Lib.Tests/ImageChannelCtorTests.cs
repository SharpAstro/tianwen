using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the Channel-typed <see cref="Image"/> constructor semantics: per-channel min/max carried on
/// each <see cref="Channel"/> with the image-wide values derived as the extrema, the legacy raw-array
/// overload stamping image-wide values on every channel, buffer travel + harvest (a
/// <see cref="Channel"/>-attached ref-counted buffer released exactly once via <see cref="Image.Release"/>),
/// and the in-place rescale NOT carrying the buffer into the rewrapped image (double-release guard).
/// </summary>
public class ImageChannelCtorTests
{
    private static Channel MakeChannel(int h, int w, float fill, float min, float max, byte index = 0)
    {
        var data = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                data[y, x] = fill;
            }
        }
        return new Channel(data, default, min, max, index);
    }

    [Fact]
    public void ChannelCtor_DerivesImageWideExtremaAndKeepsPerChannelValues()
    {
        var r = MakeChannel(4, 4, 100f, 10f, 900f, 0);
        var g = MakeChannel(4, 4, 200f, 5f, 1500f, 1);
        var b = MakeChannel(4, 4, 300f, 20f, 1200f, 2);

        var image = new Image([r, g, b], BitDepth.Float32, pedestal: 0f, new ImageMeta { SensorType = SensorType.Color });

        image.MaxValue.ShouldBe(1500f); // max over channels
        image.MinValue.ShouldBe(5f);    // min over channels
        image.ChannelCount.ShouldBe(3);
        image.GetChannel(1).MaxValue.ShouldBe(1500f); // per-channel values survive intact
        image.GetChannel(0).MaxValue.ShouldBe(900f);
        image.GetChannel(2).MinValue.ShouldBe(20f);
        image.GetChannel(1).Index.ShouldBe((byte)1);
    }

    [Fact]
    public void RawArrayCtor_StampsImageWideValuesOnEveryChannel()
    {
        var planes = Image.CreateChannelData(2, 3, 5);

        var image = new Image(planes, BitDepth.Int16, maxValue: 65535f, minValue: 12f, pedestal: 0f, new ImageMeta());

        image.MaxValue.ShouldBe(65535f);
        image.MinValue.ShouldBe(12f);
        for (var c = 0; c < 2; c++)
        {
            image.GetChannel(c).MaxValue.ShouldBe(65535f);
            image.GetChannel(c).MinValue.ShouldBe(12f);
            image.GetChannel(c).Index.ShouldBe((byte)c);
            image.GetChannel(c).Data.ShouldBeSameAs(planes[c]); // wrap, not copy
        }
    }

    [Fact]
    public void BufferTravelsWithChannel_ReleaseFiresOnceAndIsIdempotent()
    {
        var releases = 0;
        var data = new float[2, 2];
        var buffer = new ChannelBuffer(data, onRelease: _ => releases++);
        var channel = new Channel(data, default, 0f, 1f, 0) { Buffer = buffer };

        var image = new Image([channel], BitDepth.Float32, 0f, new ImageMeta());

        releases.ShouldBe(0); // harvest transfers the ref, no release yet
        image.Release();
        releases.ShouldBe(1); // refcount hit zero -> recycled to the camera
        image.Release();
        releases.ShouldBe(1); // idempotent
    }

    /// <summary>
    /// The asymmetry that makes the loud failure safe to add: a RECYCLED frame throws on read, a
    /// self-owned one does not.
    /// </summary>
    /// <remarks>
    /// <para>Reading a frame whose arrays went back to a camera or the pool used to return whatever
    /// the driver had since put there. <c>ChannelBuffer.Data</c> guarded itself and always had, but
    /// nothing ever called it -- pixels come through <c>Channel.Data</c>, a plain field -- so the
    /// "conventions 1 and 3 fail loudly" claim in the policy was not true until the check moved onto
    /// the <c>Planes</c> accessor.</para>
    /// <para>The second half matters as much as the first. The guard keys on arrays having been
    /// HANDED BACK, not on release having happened, because release is a no-op for a self-owned frame
    /// and "release the image and go on reading it" is a correct, deliberately pinned pattern. A
    /// guard on <c>_released</c> would have broken every one of those call sites.</para>
    /// </remarks>
    [Fact]
    public void ReadingARecycledFrameThrows_WhileASelfOwnedOneStaysReadable()
    {
        var recycledData = new float[2, 2] { { 1f, 2f }, { 3f, 4f } };
        var buffer = new ChannelBuffer(recycledData, onRelease: _ => { });
        var recycled = new Image(
            [new Channel(recycledData, default, 1f, 4f, 0) { Buffer = buffer }],
            BitDepth.Float32, 0f, new ImageMeta());

        recycled[0, 0, 0].ShouldBe(1f, "readable while it is still owned");
        recycled.Release();

        Should.Throw<ObjectDisposedException>(() => recycled[0, 0, 0]);
        Should.Throw<ObjectDisposedException>(() => recycled.GetChannelSpan(0).Length);

        // Convention 2, untouched: no buffer means nothing was handed back, so nothing is poisoned.
        var selfOwned = new Image(
            [new Channel(new float[2, 2] { { 5f, 6f }, { 7f, 8f } }, default, 5f, 8f, 0)],
            BitDepth.Float32, 0f, new ImageMeta());

        selfOwned.Release();
        selfOwned[0, 0, 0].ShouldBe(5f, "release is a no-op for a self-owned frame and must stay one");
        selfOwned.GetChannelSpan(0).Length.ShouldBe(4);
    }

    [Fact]
    public void ScaleFloatValuesToUnitInPlace_DoesNotCarryTheBufferIntoTheRewrappedImage()
    {
        var releases = 0;
        var data = new float[2, 2] { { 100f, 200f }, { 300f, 400f } };
        var buffer = new ChannelBuffer(data, onRelease: _ => releases++);
        var channel = new Channel(data, default, 100f, 400f, 0) { Buffer = buffer };
        var original = new Image([channel], BitDepth.Float32, 0f, new ImageMeta());

        var rescaled = original.ScaleFloatValuesToUnitInPlace();

        rescaled.ShouldNotBeSameAs(original);
        rescaled.GetChannelArray(0).ShouldBeSameAs(data); // same arrays, in-place rescale
        rescaled.MaxValue.ShouldBe(1f);
        rescaled.GetChannel(0).Buffer.ShouldBeNull(); // release responsibility stays with the original

        rescaled.Release();
        releases.ShouldBe(0); // rewrapped image holds no buffer ref
        original.Release();
        releases.ShouldBe(1); // exactly one release, from the original owner
    }

    [Fact]
    public void ChannelCtor_RejectsMismatchedShapesAndEmpty()
    {
        var a = MakeChannel(4, 4, 0f, 0f, 1f);
        var mismatched = MakeChannel(4, 5, 0f, 0f, 1f, 1);

        Should.Throw<ArgumentException>(() => new Image([a, mismatched], BitDepth.Float32, 0f, new ImageMeta()));
        Should.Throw<ArgumentException>(() => new Image(ImmutableArray<Channel>.Empty, BitDepth.Float32, 0f, new ImageMeta()));
    }

    [Fact]
    public void ScaleFloatValuesToUnit_DividesBySensorFullScaleWhenKnown_AndRescalesTheMeta()
    {
        // Native 14-bit full-scale (ASI533MC Pro: the SDK hands TianWen native-scale values,
        // 2^14-1 = 16383), frame's observed peak well below it (an under-exposed live frame).
        var data = new float[2, 2] { { 400f, 2000f }, { 4000f, 8000f } };
        var meta = new ImageMeta { SensorFullScaleAdu = 16383f };
        var original = new Image([new Channel(data, default, 400f, 8000f, 0)], BitDepth.Int16, 0f, meta);

        var unit = original.ScaleFloatValuesToUnit();

        // Pixels divide by the FIXED full-scale, not the observed peak ...
        unit[0, 0, 0].ShouldBe(400f / 16383f, 1e-7f);
        unit[0, 1, 1].ShouldBe(8000f / 16383f, 1e-7f);
        // ... so MaxValue is the observed peak in unit space (NOT stretched to 1.0) ...
        unit.MaxValue.ShouldBe(8000f / 16383f, 1e-7f);
        // ... and the full-scale metadata rescales with the pixels (saturation now = 1.0).
        unit.ImageMeta.SensorFullScaleAdu.ShouldNotBeNull();
        unit.ImageMeta.SensorFullScaleAdu.Value.ShouldBe(1f, 1e-6f);
    }

    [Fact]
    public void ScaleFloatValuesToUnit_FallsBackToObservedPeakWithoutSensorFullScale()
    {
        var data = new float[2, 2] { { 400f, 8000f }, { 16000f, 32000f } };
        var original = new Image([new Channel(data, default, 400f, 32000f, 0)], BitDepth.Int16, 0f, new ImageMeta());

        var unit = original.ScaleFloatValuesToUnit();

        // No fixed full-scale known (e.g. a file import) -> prior behaviour: peak stretches to 1.0.
        unit.MaxValue.ShouldBe(1f, 1e-6f);
        unit[0, 1, 1].ShouldBe(1f, 1e-6f);
        unit.ImageMeta.SensorFullScaleAdu.ShouldBeNull();
    }
}
