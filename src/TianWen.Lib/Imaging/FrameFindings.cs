using TianWen.Lib.Astrometry;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging;

/// <summary>
/// What is known about one frame beyond its pixels: where it is on the sky, and the stars found in it with
/// their median HFD and FWHM.
/// </summary>
/// <remarks>
/// <para><b>Immutable, and replaced whole.</b> Whoever learns something new about the frame (a plate solve, a
/// star detection, a node's measurement arriving with a live frame) builds the next record and swaps it in with
/// one reference write, so a reader on another thread sees the old record or the new one, never a solution from
/// one and a star list from the other.</para>
/// <para>It is what lets the viewer draw a grid, a star overlay, a selection or the sky behind ANY source's frame
/// (step 2 of P1, docs/plans/live-session-preview.md): a file's document fills it locally, the live preview from
/// the node. Before it, a frame that was not a document reached the viewer's WCS only through a bolt-on override,
/// and its stars not at all.</para>
/// </remarks>
/// <param name="Wcs">The frame's place on the sky: a solve, or the header's approximate hint; null when unknown.</param>
/// <param name="Stars">The stars detected in the frame; null until a detection has run (an empty list when one
/// ran and found none).</param>
/// <param name="MedianHfd">Median half-flux DIAMETER of <paramref name="Stars"/>, in pixels; NaN with no stars.</param>
/// <param name="MedianFwhm">Median FWHM of <paramref name="Stars"/>, in pixels; NaN with no stars.</param>
public sealed record FrameFindings(WCS? Wcs, StarList? Stars, float MedianHfd, float MedianFwhm)
{
    /// <summary>Nothing known beyond the pixels.</summary>
    public static readonly FrameFindings None = new FrameFindings(null, null, float.NaN, float.NaN);

    /// <summary>A frame placed only on the sky, with no stars measured.</summary>
    public static FrameFindings Placed(WCS? wcs) => None with { Wcs = wcs };

    /// <summary>Whether the WCS is a solver's answer (a CD matrix), not an approximate hint from a header.</summary>
    public bool IsPlateSolved => Wcs is { HasCDMatrix: true, IsApproximate: false };

    /// <summary>These findings with <paramref name="stars"/>, and the medians taken from them.</summary>
    public FrameFindings WithStars(StarList stars) => this with
    {
        Stars = stars,
        MedianHfd = stars.Count > 0 ? stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median) : float.NaN,
        MedianFwhm = stars.Count > 0 ? stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median) : float.NaN,
    };
}
