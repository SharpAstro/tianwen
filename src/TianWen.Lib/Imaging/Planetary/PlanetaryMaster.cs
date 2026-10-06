using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;

namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// Shared "turn integrated accumulators into a display master" steps for the planetary stackers: the
/// coverage-normalise (<see cref="NormalizeInPlace"/>) and, for a split-CFA stack, the CFA-sub-plane merge
/// + single MHC demosaic (<see cref="MergeAndDemosaicAsync"/>). Both <see cref="LuckyImagingStacker"/>
/// (batch) and <see cref="RollingWindowStacker"/> (live) fold frames into <c>sum</c>/<c>weight</c> planes
/// the exact same way, so this is the one place the post-integration master is built -- the batch path and
/// the live path can never drift apart.
/// </summary>
internal static class PlanetaryMaster
{
    /// <summary>
    /// In-place coverage divide: <c>channelAccum[c][y,x] /= weightAccum[y,x]</c> (zero where coverage is
    /// zero), then wraps the (now mean-valued) planes as a Float32 <see cref="Image"/> carrying
    /// <paramref name="meta"/>. The accumulators are consumed -- the returned image reuses their arrays.
    /// Dimensions are taken from the arrays themselves so the same helper serves a sub-plane accumulator
    /// (split-CFA) and a full-frame one (mono / RGB).
    /// </summary>
    internal static Image NormalizeInPlace(float[][,] channelAccum, float[,] weightAccum, ImageMeta meta)
        => NormalizeInto(channelAccum, weightAccum, channelAccum, meta);

    /// <summary>
    /// Coverage divide into <paramref name="dst"/>: <c>dst[c][y,x] = channelAccum[c][y,x] / weightAccum[y,x]</c>
    /// (zero where coverage is zero), then wraps the destination planes as a Float32 <see cref="Image"/>
    /// carrying <paramref name="meta"/>. With <paramref name="dst"/> distinct from the accumulator this is
    /// the fused clone+normalise for a live stacker: the accumulators keep their integral state (one read
    /// pass, one write pass -- no separate <c>Clone()</c>), and the returned image wraps <paramref name="dst"/>,
    /// so the caller owns its lifetime. <see cref="NormalizeInPlace"/> is the <c>dst == channelAccum</c> case.
    /// A pixel whose weight is at most <paramref name="minWeight"/> counts as uncovered: a stack that takes frames
    /// back out (the rolling window) leaves what rounding made of an evicted frame's weight and sum where no frame
    /// now reaches, and their ratio is no measurement (#1319). Zero, the default, is a stack that only adds.
    /// </summary>
    internal static Image NormalizeInto(float[][,] channelAccum, float[,] weightAccum, float[][,] dst, ImageMeta meta, float minWeight = 0f)
    {
        var channels = channelAccum.Length;
        var height = weightAccum.GetLength(0);
        var width = weightAccum.GetLength(1);

        for (var c = 0; c < channels; c++)
        {
            var src = channelAccum[c];
            var dstPlane = dst[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var wv = weightAccum[y, x];
                    dstPlane[y, x] = wv > minWeight ? src[y, x] / wv : 0f;
                }
            }
        }

        return new Image(dst, BitDepth.Float32, 1f, 0f, 0f, meta);
    }

    /// <summary>
    /// The largest rectangle of a stack's output grid every block of which (2 x 2 pixels) at least
    /// <see cref="PlanetaryStackOptions.CoverageCropFraction"/> of the frames' weight reached, read off its <see cref="PlanetaryCoverage"/>:
    /// the deep-sky master's rule (<see cref="Image.LargestCoveredRectangle(Image, double, int)"/>), so under-coverage counts only where it
    /// reaches the border. Never off the weight the stack folds, which per-point quality weighting makes lower on the sky than on the planet.
    /// </summary>
    internal static PixelRect CoveredRectangle(float[,] coverage)
    {
        var plane = new Image([coverage], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta());
        return plane.LargestCoveredRectangle(plane, PlanetaryStackOptions.CoverageCropFraction, blockSize: 2);
    }

    /// <summary>
    /// <paramref name="master"/> cropped to <paramref name="covered"/> (<see cref="CoveredRectangle"/>, on a grid <paramref name="scale"/>
    /// master pixels a cell) grown to hold its <see cref="PlanetaryDisk.Footprint"/>, so the planet, its rings and its moons are never cut;
    /// the master itself, and an empty rectangle, when that keeps all of it. The caller owns the image returned.
    /// </summary>
    internal static (Image Master, PixelRect Kept) CropToCovered(Image master, PixelRect covered, int scale)
    {
        var full = new PixelRect(0, 0, master.Width, master.Height);
        if (covered.IsEmpty)
        {
            return (master, PixelRect.Empty);
        }
        var kept = new PixelRect(covered.X * scale, covered.Y * scale, covered.Width * scale, covered.Height * scale);
        var light = PlanetaryDisk.Footprint(master);
        kept = PixelRect.FromLTRB(Math.Min(kept.Left, light.Left), Math.Min(kept.Top, light.Top), Math.Max(kept.Right, light.Right), Math.Max(kept.Bottom, light.Bottom));
        kept = PixelRect.Intersect(kept, full);
        return kept == full || kept.IsEmpty ? (master, PixelRect.Empty) : (master.Crop(kept), kept);
    }

    /// <summary>
    /// For a split-CFA stack the integrated master is four CFA sub-planes; merge them into a
    /// full-resolution mosaic and demosaic once (MHC). Mono / RGB masters pass through unchanged. This is
    /// the demosaic-once step shared by the batch finaliser and the live rolling-window publisher (neither
    /// debayers per frame -- only the integrated master).
    /// </summary>
    internal static async Task<Image> MergeAndDemosaicAsync(Image stacked, PlanetaryFrameLayout layout, CancellationToken cancellationToken = default)
    {
        if (layout == PlanetaryFrameLayout.SplitCfa && stacked.ChannelCount == 4)
        {
            // The mosaic is read once, by the demosaic, and then never again: rented, where it used to be a
            // new full-size plane per master (the demosaic's RGB output is the master and stays fresh).
            var mosaicPlane = Array2DPool<float>.Rent(stacked.Height * 2, stacked.Width * 2);
            try
            {
                var mosaic = stacked.MergeBayerChannelsInto(mosaicPlane);
                return await mosaic.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Array2DPool<float>.Return(mosaicPlane);
            }
        }

        return stacked;
    }
}
