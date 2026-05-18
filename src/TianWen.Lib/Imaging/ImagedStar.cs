namespace TianWen.Lib.Imaging;

/// <summary>
/// One detected star. <c>Ellipticity</c> is the moment-based elongation
/// of the aperture pixel cloud (0 = circular, → 1 = highly elongated).
/// Derived from the eigenvalues a², b² of the flux-weighted second-moment
/// matrix as <c>e = sqrt(1 - b²/a²)</c>. Useful for spotting tracking
/// drift or collimation issues per frame, and for grading the stacked
/// master.
/// </summary>
public readonly record struct ImagedStar(float HFD, float StarFWHM, float SNR, float Flux, float XCentroid, float YCentroid, float Ellipticity);