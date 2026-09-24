using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A preview of a frame someone else owns must never change that frame. The TUI's live preview used to
/// hand the session's own frame to <see cref="AstroImageDocument.AdoptImageAsync"/>, which CONSUMES its
/// input and rescales it to [0, 1] in place, while the session still had that frame queued for its FITS
/// write and was running star detection on it: the sub could be written as 0 or 1 ADU.
/// <see cref="AstroImageDocument.FromLiveFrameAsync"/> leases the frame, copies it and adopts the copy.
/// </summary>
public class LiveFrameDocumentTests
{
    private const int Width = 64, Height = 48;
    private const float FullScale = 4095f;

    /// <summary>A frame as a session holds one: 12-bit ADU samples in a buffer the camera recycles.</summary>
    private static (Image Frame, float[,] Plane, int[] Recycled) CameraFrame()
    {
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = (((y * Width) + x) * 7) % 4096;
            }
        }

        var recycled = new int[1];
        var buffer = new ChannelBuffer(plane, onRelease: _ => recycled[0]++);
        var frame = new Image(
            [new Channel(plane, default, 0f, FullScale, 0) { Buffer = buffer }],
            BitDepth.Int16,
            pedestal: 0f,
            new ImageMeta());
        return (frame, plane, recycled);
    }

    private static ReadOnlySpan<float> Flat(float[,] plane) => MemoryMarshal.CreateReadOnlySpan(ref plane[0, 0], plane.Length);

    [Fact]
    public async Task APreviewLeavesTheFramesPixelsAsTheCameraDeliveredThem()
    {
        var (frame, plane, _) = CameraFrame();
        var before = (float[,])plane.Clone();

        var doc = await AstroImageDocument.FromLiveFrameAsync(frame, TestContext.Current.CancellationToken);

        doc.ShouldNotBeNull();
        Flat(plane).SequenceEqual(Flat(before)).ShouldBeTrue("the session's frame is still in ADU, as it will be written");
        frame.MaxValue.ShouldBe(FullScale);
        doc.UnstretchedImage.GetChannelArray(0).ShouldNotBeSameAs(plane, "the document owns a copy, not the camera's buffer");
    }

    [Fact]
    public async Task APreviewGivesItsLeaseBackSoTheFrameStillRecycles()
    {
        var (frame, _, recycled) = CameraFrame();

        _ = await AstroImageDocument.FromLiveFrameAsync(frame, TestContext.Current.CancellationToken);
        recycled[0].ShouldBe(0, "the session still owns the frame");

        frame.Release();
        recycled[0].ShouldBe(1, "the preview left no reference behind, so the camera gets its buffer back");
    }

    [Fact]
    public async Task AFrameAlreadyGivenBackIsNotPreviewed()
    {
        var (frame, _, _) = CameraFrame();
        frame.Release();

        (await AstroImageDocument.FromLiveFrameAsync(frame, TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}
