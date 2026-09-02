using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.BackgroundExtraction
{
    /// <summary>
    /// Classical (non-AI) background extraction: fit a smooth model of the sky background to a
    /// linear frame and remove its SHAPE while preserving its LEVEL. The plan behind it is
    /// <c>docs/plans/background-extraction.md</c>; the algorithm is the robust iterative fit its
    /// reference review settled on, not the sample-point walk the plan started from.
    /// </summary>
    /// <remarks>
    /// <para>Domain: linear units in, linear units out, and the fit runs in linear too. Every
    /// reference that stretched before fitting and then corrected in the stretched domain got the
    /// physics wrong (an additive gradient is only additive in linear data); SAS Pro's own "KEY FIX"
    /// moved its correction back. Thresholds are stated in noise units (robust sigma of the fit
    /// residual), never in absolute pixel values, so the same defaults hold for a master whose sky
    /// sits at 0.001 and one whose sky sits at 0.2.</para>
    /// <para>The caller owns both returned images and releases each when done
    /// (<see cref="Image.Release"/>), the background included even when only the cleaned frame is
    /// wanted downstream.</para>
    /// </remarks>
    public interface IBackgroundExtractor
    {
        /// <summary>Fits and removes the background gradient of <paramref name="source"/>.</summary>
        Task<BackgroundExtractionResult> ExtractAsync(Image source, BackgroundExtractionOptions options, CancellationToken cancellationToken = default);
    }
}
