using System.Threading;
using System.Threading.Tasks;

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
    /// </summary>
    internal static Image NormalizeInto(float[][,] channelAccum, float[,] weightAccum, float[][,] dst, ImageMeta meta)
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
                    dstPlane[y, x] = wv > 0f ? src[y, x] / wv : 0f;
                }
            }
        }

        return new Image(dst, BitDepth.Float32, 1f, 0f, 0f, meta);
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
            var mosaic = stacked.MergeBayerChannels();
            return await mosaic.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken).ConfigureAwait(false);
        }

        return stacked;
    }
}
