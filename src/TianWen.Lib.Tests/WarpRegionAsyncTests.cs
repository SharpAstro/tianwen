using System.Drawing;
using System.Numerics;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="Image.WarpRegionAsync"/> against
/// <see cref="Image.WarpToReferenceGridAsync"/>: a region-warp must produce
/// the exact same pixels as the full-canvas warp restricted to that region.
/// Region-warp is the Phase 8.2 primitive the tile-pipelined integrator
/// uses to bound peak RAM by strip rather than full canvas.
/// </summary>
[Collection("Imaging")]
public class WarpRegionAsyncTests
{
    private const int CanvasW = 48;
    private const int CanvasH = 32;

    [Theory]
    [InlineData(0, 0, 16, 16)]            // top-left tile
    [InlineData(16, 8, 16, 16)]           // middle tile
    [InlineData(32, 16, 16, 16)]          // bottom-right tile
    [InlineData(0, 0, CanvasW, 1)]         // full-width strip (row 0)
    [InlineData(0, 16, CanvasW, 8)]        // full-width strip (middle)
    [InlineData(0, 0, CanvasW, CanvasH)]   // full canvas: must equal WarpToReferenceGridAsync exactly
    public async Task RegionMatchesFullCanvasPixels_RotateAndShift(int rx, int ry, int rw, int rh)
    {
        var src = BuildSyntheticSource();

        // Non-trivial transform: 0.2 rad rotation + (3, -2) translation.
        // The inverse map exercises bilinear sampling at non-integer source
        // coords and the out-of-source-bounds NaN path near the edges.
        var transform = Matrix3x2.CreateRotation(0.2f) * Matrix3x2.CreateTranslation(3f, -2f);

        var ct = TestContext.Current.CancellationToken;
        var full = await src.WarpToReferenceGridAsync(transform, CanvasW, CanvasH, ct);
        var region = await src.WarpRegionAsync(transform, new Rectangle(rx, ry, rw, rh), CanvasW, CanvasH, ct);

        region.Width.ShouldBe(rw);
        region.Height.ShouldBe(rh);

        var fullCh = full.GetChannelArray(0);
        var regionCh = region.GetChannelArray(0);
        for (var r = 0; r < rh; r++)
        {
            for (var c = 0; c < rw; c++)
            {
                var canvasX = rx + c;
                var canvasY = ry + r;
                var f = fullCh[canvasY, canvasX];
                var p = regionCh[r, c];
                // NaN compares equal on both sides; otherwise exact since both
                // call the same SubpixelValue path with the same inverse transform.
                if (float.IsNaN(f))
                {
                    float.IsNaN(p).ShouldBeTrue($"region NaN expected at ({canvasX}, {canvasY}) got {p}");
                }
                else
                {
                    p.ShouldBe(f, tolerance: 1e-6f,
                        $"mismatch at ({canvasX}, {canvasY}): full={f}, region={p}");
                }
            }
        }
    }

    [Fact]
    public async Task OutOfBoundsRegion_Throws()
    {
        var src = BuildSyntheticSource();
        var transform = Matrix3x2.Identity;
        // Identity is not invertible-safe for the canvas extent here, but the
        // bounds-check fires before invert: the region must lie inside canvas.
        await Should.ThrowAsync<System.ArgumentOutOfRangeException>(async () =>
            await src.WarpRegionAsync(transform, new Rectangle(-1, 0, 8, 8), CanvasW, CanvasH));
        await Should.ThrowAsync<System.ArgumentOutOfRangeException>(async () =>
            await src.WarpRegionAsync(transform, new Rectangle(0, 0, CanvasW + 1, 8), CanvasW, CanvasH));
        await Should.ThrowAsync<System.ArgumentOutOfRangeException>(async () =>
            await src.WarpRegionAsync(transform, new Rectangle(0, 0, 0, 8), CanvasW, CanvasH));
    }

    private static Image BuildSyntheticSource()
    {
        // 16x12 mono image with a position-encoded gradient: pixel(x, y) = (y * 16 + x) / 192.
        // Distinctive per-pixel signature so any sampling mistake (off-by-one
        // row/col, x/y swap, dropped column) shows up as a tolerance-busting
        // mismatch.
        var arr = new float[12, 16];
        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                arr[y, x] = (y * 16 + x) / 192f;
            }
        }
        return Image.FromChannel(arr, maxValue: 1f, minValue: 0f);
    }
}
