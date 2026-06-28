using TianWen.Lib.Devices.Fake;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase 1 of the image-based Jupiter simulation: the embedded NASA/ESA Hubble OPAL texture renders as a
/// correctly-sized, oblate, bright disk on dark sky. (The fake video path's end-to-end COM / brightness /
/// jog behaviour is pinned by <see cref="FakeCameraVideoTests"/>, which now routes through this renderer.)
/// </summary>
public class JupiterTextureRendererTests
{
    private const float MaxAdu = 65535f;

    [Fact]
    public void Renders_a_bright_disk_on_dark_sky()
    {
        var arr = JupiterTextureRenderer.Render(
            400, 400, centerX: 200, centerY: 200, equatorialRadius: 150,
            blurSigma: 0.4, maxAdu: MaxAdu, noiseSeed: 1);

        // Disk centre is far brighter than a sky corner.
        arr[200, 200].ShouldBeGreaterThan(arr[4, 4] * 10f);
    }

    [Fact]
    public void Disk_is_oblate_polar_smaller_than_equatorial()
    {
        var arr = JupiterTextureRenderer.Render(
            500, 500, centerX: 250, centerY: 250, equatorialRadius: 180,
            blurSigma: 0.4, maxAdu: MaxAdu, noiseSeed: 2);

        var threshold = MaxAdu * 0.1f; // well above sky, below the (limb-darkened) disk edge

        // Horizontal bright extent along the centre row vs vertical along the centre column. Limb darkening
        // is radial, so the extent ratio reflects the geometric oblateness (polar = 0.935 x equatorial).
        var horizontal = Extent(arr, 250, isRow: true, threshold);
        var vertical = Extent(arr, 250, isRow: false, threshold);

        vertical.ShouldBeGreaterThan(0);
        horizontal.ShouldBeGreaterThan(0);
        vertical.ShouldBeLessThan(horizontal); // oblate: shorter pole-to-pole
        ((double)vertical / horizontal).ShouldBe(JupiterTextureRenderer.Oblateness, 0.06);
    }

    [Fact]
    public void Larger_radius_makes_a_larger_disk()
    {
        var small = CountBright(JupiterTextureRenderer.Render(400, 400, 200, 200, equatorialRadius: 60, blurSigma: 0.4, maxAdu: MaxAdu, noiseSeed: 3));
        var large = CountBright(JupiterTextureRenderer.Render(400, 400, 200, 200, equatorialRadius: 150, blurSigma: 0.4, maxAdu: MaxAdu, noiseSeed: 3));

        large.ShouldBeGreaterThan(small * 3); // area scales ~ radius^2 (150/60)^2 ~ 6.25x
    }

    [Fact]
    public void RenderBayer_encodes_a_warm_coloured_disk_in_the_mosaic()
    {
        // The colour Bayer mosaic carries Jupiter's tan/brown colour. With offset (0,0), red sits at
        // (even, even) photosites and blue at (odd, odd); averaging the red-CFA samples against the blue-CFA
        // samples across the disk, red exceeds blue. (No sky/read noise so the disk mask is clean; shot noise
        // on the bright disk is negligible against the R/B colour difference.)
        var arr = JupiterTextureRenderer.RenderBayer(
            400, 400, centerX: 200, centerY: 200, equatorialRadius: 150,
            bayerOffsetX: 0, bayerOffsetY: 0, blurSigma: 0.4, maxAdu: MaxAdu,
            skyBackground: 0, readNoiseElectrons: 0, noiseSeed: 5);

        double rSum = 0, bSum = 0;
        long rN = 0, bN = 0;
        for (var y = 0; y < 400; y++)
        {
            for (var x = 0; x < 400; x++)
            {
                if (arr[y, x] < MaxAdu * 0.02f)
                {
                    continue; // skip the dark sky so the colour signal isn't diluted
                }

                if ((x & 1) == 0 && (y & 1) == 0)
                {
                    rSum += arr[y, x];
                    rN++;
                }
                else if ((x & 1) == 1 && (y & 1) == 1)
                {
                    bSum += arr[y, x];
                    bN++;
                }
            }
        }

        rN.ShouldBeGreaterThan(0);
        bN.ShouldBeGreaterThan(0);
        (rSum / rN).ShouldBeGreaterThan(bSum / bN);
    }

    // First-to-last index above threshold along a centre row (isRow) or column.
    private static int Extent(float[,] arr, int line, bool isRow, float threshold)
    {
        var n = isRow ? arr.GetLength(1) : arr.GetLength(0);
        int first = -1, last = -1;
        for (var i = 0; i < n; i++)
        {
            var v = isRow ? arr[line, i] : arr[i, line];
            if (v > threshold)
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
            }
        }
        return first < 0 ? 0 : last - first + 1;
    }

    private static int CountBright(float[,] arr)
    {
        var threshold = MaxAdu * 0.1f;
        var count = 0;
        foreach (var v in arr)
        {
            if (v > threshold)
            {
                count++;
            }
        }
        return count;
    }
}
