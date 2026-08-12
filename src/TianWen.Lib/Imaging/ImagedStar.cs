namespace TianWen.Lib.Imaging;

/// <summary>
/// One detected star. <c>Ellipticity</c> is the moment-based elongation
/// of the aperture pixel cloud (0 = circular, → 1 = highly elongated).
/// Derived from the eigenvalues a², b² of the flux-weighted second-moment
/// matrix as <c>e = sqrt(1 - b²/a²)</c>. Useful for spotting tracking
/// drift or collimation issues per frame, and for grading the stacked
/// master.
/// </summary>
/// <param name="Flux">Signal summed inside the measurement aperture, with
/// <paramref name="LocalBackground"/> already subtracted. Note this is a FIXED-aperture sum, so it
/// falls when the PSF widens even at constant total brightness; never read a positional flux trend
/// as pure throughput without dividing out the aperture loss implied by <paramref name="StarFWHM"/>.</param>
/// <param name="LocalBackground">Median of the annulus outside the aperture, in the same value space
/// as the channel's pixels (NOT pedestal-subtracted), i.e. the level subtracted before
/// <paramref name="Flux"/> was summed. Retained because it is a free local sample of the ADDITIVE sky
/// background at (<paramref name="XCentroid"/>, <paramref name="YCentroid"/>): light pollution, moon
/// glow and airglow, which is what a gradient model fits and what <paramref name="Flux"/> is blind to
/// by construction. Two caveats for that use: samples land only where stars are, so bright nebulosity
/// is under-sampled (arguably correct, since a background model should not be anchored there), and in
/// a crowded field the annulus carries a neighbour's wings, so it reads high. 0 for a star that was
/// not measured through <see cref="Image.AnalyseStar"/>.</param>
public readonly record struct ImagedStar(
    float HFD,
    float StarFWHM,
    float SNR,
    float Flux,
    float XCentroid,
    float YCentroid,
    float Ellipticity,
    float LocalBackground = 0f);