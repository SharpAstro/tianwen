namespace TianWen.Lib.Imaging.Planetary;

/// <summary>
/// How an <see cref="IPlanetaryFrameStream"/> presents each frame's channels. Fixed for the lifetime
/// of a stream (it follows the capture's colour mode), so the grader / aligner / integrator can size
/// their buffers once.
/// </summary>
public enum PlanetaryFrameLayout
{
    /// <summary>A single full-resolution luminance plane (a mono sensor, or an unmodelled CFA family
    /// treated as mono).</summary>
    Mono,

    /// <summary>Three full-resolution colour planes in R, G, B order (an RGB / BGR SER).</summary>
    Rgb,

    /// <summary>
    /// Four half-resolution CFA sub-planes in <c>[R, G1, G2, B]</c> order (a Bayer source split via
    /// <see cref="Image.SplitBayerChannels"/>). Each photosite colour is aligned + stacked independently
    /// and the integrated CFA is demosaiced once after stacking -- the split-CFA lucky-imaging path.
    /// </summary>
    SplitCfa,

    /// <summary>A single full-resolution raw Bayer mosaic (the split is deferred to the caller). Used
    /// when split-CFA is disabled, e.g. for per-frame debayered preview.</summary>
    BayerMosaic,
}
