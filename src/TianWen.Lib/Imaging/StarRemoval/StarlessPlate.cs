using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>What the fit made of one point source.</summary>
public enum StarFitOutcome
{
    /// <summary>A star: its model was subtracted.</summary>
    Subtracted = 0,

    /// <summary>Wider than the field's stars, or its model explains under half its peak: a knot of structure, left in
    /// the plate.</summary>
    Knot = 1,

    /// <summary>Narrower than the field's stars: not a star (an artefact the stack kept), left in the plate.</summary>
    TooNarrow = 2,

    /// <summary>The fit found no positive star there.</summary>
    NoFit = 3,

    /// <summary>Inside a saturated star's core: a second peak on its plateau's edge, part of that star (its core is
    /// inpainted), not a source of its own.</summary>
    Merged = 4,
}

/// <summary>The model a subtracted star was taken out with.</summary>
public enum StarFitModel
{
    /// <summary>Nothing was subtracted.</summary>
    None = 0,

    /// <summary>The field's Moffat (with its residual table), fitted on the core or, saturated, on the wings: its amplitude
    /// is the model's peak, past the clip for a saturated star.</summary>
    Moffat = 1,

    /// <summary>A giant's own symmetric profile: its amplitude is the profile's first ring, not an extrapolated peak.</summary>
    Profile = 2,

    /// <summary>A blend fitted as two field-width stars: the amplitude is the brighter's.</summary>
    Pair = 3,
}

/// <summary>One point source and what the builder did with it. Coordinates follow <see cref="ImagedStar"/>: 0-based,
/// pixel centres at integers.</summary>
/// <param name="X">Fitted centre on the luminance.</param>
/// <param name="Y">Fitted centre on the luminance.</param>
/// <param name="Significance">The finder's matched-filter significance, in sigma.</param>
/// <param name="Amplitude">The luminance model's peak above the local sky, in the plane's units.</param>
/// <param name="WidthScale">The star's FWHM over the field's (1 is a typical star of this image); what the knot test reads.</param>
/// <param name="Sky">The local sky the fit found under the star, luminance.</param>
/// <param name="LocalSigma">The sky noise at the star, luminance.</param>
/// <param name="Outcome">What the fit decided.</param>
/// <param name="Saturated">A flat core: fitted on its wings, its core inpainted.</param>
/// <param name="Inpainted">Some of its pixels were inpainted.</param>
/// <param name="CoreResidual">After the plate was built: the RMS of plate minus local sky within one FWHM, in local
/// sigma (pure noise reads 1); NaN where it was inpainted or not subtracted.</param>
/// <param name="CoreBias">The mean of the same pixels in sigma over root n; NaN likewise.</param>
/// <param name="SecondPass">Found only on the residual of the first pass, beside a brighter star.</param>
/// <param name="HoleDepth">Where the star was, filled or not, over its core (a saturated star's whole plateau): the mean of
/// plate minus the plate's own median in an annulus beyond the star, in sigma over root n. Below -3 is a hole, beyond the
/// plate's own false-alarm rate (<see cref="StarlessPlateStatistics.HoleNullRate"/>); NaN where nothing was subtracted.</param>
/// <param name="Model">What it was subtracted with.</param>
/// <param name="ChannelAmplitudes">Each channel's model peak above its sky (one entry for a mono image); empty where
/// nothing was subtracted. What the injector draws a star's brightness and colour from.</param>
/// <param name="SkyAbove">What the star sits on: the luminance sky map under it above the frame's darkest sky (the map's
/// 5th percentile over the covered pixels), in the pixels' own noise there (<see cref="PointSourceFinder.CapByDifferenceNoise"/>).
/// Near 0 on dark sky, tens to thousands on a nebula; a gradient lifts it too, which <paramref name="Texture"/> tells
/// apart. NaN where the centre is off the frame.</param>
/// <param name="Texture">The structure under the star: the sky map's spread over the pixels' own noise, the factor the
/// hole tests' noise was cut by. 1 on a smooth sky or glow, above it where a nebula's texture (or a crowd of fainter stars)
/// moves the sky within a few pixels; 1.6 in the Orion master's M42 core. NaN where the centre is off the frame.</param>
public readonly record struct FittedStar(
    float X, float Y, float Significance, float Amplitude, float WidthScale, float Sky, float LocalSigma,
    StarFitOutcome Outcome, bool Saturated, bool Inpainted, float CoreResidual, float CoreBias, bool SecondPass, float HoleDepth,
    StarFitModel Model = StarFitModel.None, ImmutableArray<float> ChannelAmplitudes = default, float SkyAbove = float.NaN,
    float Texture = float.NaN);

/// <summary>One significance band of the report. Bands are on the finder's first-pass significance.</summary>
/// <param name="SigmaLow">Lower edge, inclusive.</param>
/// <param name="SigmaHigh">Upper edge, exclusive (infinity for the last band).</param>
/// <param name="Found">Point sources found in the band.</param>
/// <param name="Subtracted">Fitted as stars and subtracted.</param>
/// <param name="Knots">Left in the plate as structure (wider, or narrower, than a star).</param>
/// <param name="Inpainted">Subtracted stars that also had pixels inpainted.</param>
/// <param name="Holes">Subtracted stars whose core in the plate sits more than 3 sigma over root n below their own sky
/// (<see cref="FittedStar.HoleDepth"/>): the holes a starless plate must not have.</param>
/// <param name="Leftover">Point sources the finder still sees in the finished plate, at this band's significance.</param>
/// <param name="LeftoverAway">Of those, the ones outside every subtracted star's footprint: a source never subtracted (a
/// missed star, a spike), where the rest are what a subtraction left.</param>
/// <param name="ResidualMedian">Median <see cref="FittedStar.CoreResidual"/> over subtracted, not inpainted stars.</param>
/// <param name="BiasMedian">Median <see cref="FittedStar.CoreBias"/> over the same stars.</param>
public readonly record struct StarlessBand(
    float SigmaLow, float SigmaHigh, int Found, int Subtracted, int Knots, int Inpainted, int Holes, int Leftover, int LeftoverAway,
    float ResidualMedian, float BiasMedian)
{
    /// <summary>Leftovers as a fraction of what was found in the band.</summary>
    public float LeftoverFraction => Found > 0 ? (float)Leftover / Found : float.NaN;
}

/// <summary>The measures R0's report reads (docs/plans/star-remover-training.md, "R0: the classical plate builder").</summary>
/// <param name="Bands">Per significance band: 5-10, 10-20, 20-50, 50-100, 100 and up.</param>
/// <param name="InpaintFraction">Inpainted over present pixels.</param>
/// <param name="LeftoverNearThreshold">Point sources the finder sees in the plate between its leftover threshold and the
/// first band's lower edge, which have no first-pass counterpart to be a fraction of.</param>
/// <param name="FaintResidualCentre">Median core residual of faint (5-20 sigma) subtracted stars in the inner third of the
/// field (by distance from the centre over the half-diagonal).</param>
/// <param name="FaintResidualCorners">The same in the outer third.</param>
/// <param name="FwhmPx">The PSF FWHM measured per channel (luminance last), in pixels.</param>
/// <param name="MoffatBeta">The Moffat beta measured per channel (luminance last).</param>
/// <param name="FieldWidthScale">The median width scale of the bright isolated stars the field was calibrated on: what
/// the measured FWHM becomes once the model is integrated over the pixel.</param>
/// <param name="FieldBeta">The Moffat beta fitted on the same stars, which every subtraction uses; the stacked profile's
/// <paramref name="MoffatBeta"/> only seeds it.</param>
/// <param name="HoleNullRate">The hole test's false-alarm rate on this plate: the fraction of random star-free apertures
/// reading below -3 the same way; a band's holes count only as an excess over it.</param>
/// <param name="NoiseCorrelation">The lag-1 correlation of each channel's sky noise, which the fill's grain matches.</param>
/// <param name="Seconds">Wall time of the build.</param>
/// <param name="Speckles">Dark speckles at the subtracted sites, per significance band, against the plate's own sky
/// (<see cref="StarlessSpeckles"/>); null on a record from before they were read.</param>
public sealed record StarlessPlateStatistics(
    ImmutableArray<StarlessBand> Bands, float InpaintFraction, int LeftoverNearThreshold,
    float FaintResidualCentre, float FaintResidualCorners, ImmutableArray<float> FwhmPx, ImmutableArray<float> MoffatBeta,
    float FieldWidthScale, float FieldBeta, float HoleNullRate, ImmutableArray<float> NoiseCorrelation, double Seconds,
    SpeckleReport? Speckles = null);

/// <summary>What <see cref="ClassicalStarRemover.BuildAsync"/> returns: the starless plate and the record of what made it.</summary>
/// <param name="Plate">The starless plate, same shape, units, pedestal and metadata as the input.</param>
/// <param name="Subtracted">Pixels a subtracted star's model reached above a tenth of the local sigma.</param>
/// <param name="Inpainted">Pixels replaced by the fill.</param>
/// <param name="Stars">Every point source found, with what the fit made of it.</param>
/// <param name="Statistics">The report's measures.</param>
/// <param name="FieldProfile">The profile every star was subtracted with, the one its catalogued amplitude means anything
/// with (null only where a caller built the record itself).</param>
public sealed record StarlessPlate(
    Image Plate, BitMatrix Subtracted, BitMatrix Inpainted, ImmutableArray<FittedStar> Stars, StarlessPlateStatistics Statistics,
    StarlessFieldProfile? FieldProfile = null);

/// <summary>Knobs of the classical builder; the defaults are the design's (docs/plans/star-remover-training.md, R0).</summary>
/// <param name="DetectionSigma">The finder's threshold for a point source, first and second pass.</param>
/// <param name="LeftoverSigma">The finder's threshold on the finished plate for the leftover count.</param>
/// <param name="NonlinearFitSigma">From this significance up, a star's centre and width are fitted; below, the found
/// centre is kept and the width chosen from a coarse grid.</param>
/// <param name="BetaFitSigma">From this significance up, a star's own Moffat beta is fitted as well, so a bright star's
/// wider wings are modelled and its core width judged by its FWHM, not by how far a fixed-beta model had to widen.</param>
/// <param name="MaxWidthScale">A candidate whose FWHM is more than this times the field's is a knot.</param>
/// <param name="MinWidthScale">A candidate whose FWHM is less than this times the field's is not a star.</param>
/// <param name="MinPeakExplained">A candidate whose model explains less than this fraction of its peak is a knot.</param>
/// <param name="InpaintSigma">A residual beyond this (in the smoothed residual's own noise) inside a star's footprint is
/// inpainted.</param>
/// <param name="Seed">The fill's noise seed; the same seed gives the same plate.</param>
/// <param name="SecondPass">Run the finder again on the first pass's residual.</param>
public sealed record StarlessPlateOptions(
    float DetectionSigma = 5f,
    float LeftoverSigma = 4f,
    float NonlinearFitSigma = 20f,
    float BetaFitSigma = 100f,
    float MaxWidthScale = 1.6f,
    float MinWidthScale = 0.5f,
    float MinPeakExplained = 0.5f,
    float InpaintSigma = 3f,
    int Seed = 0x5EED,
    bool SecondPass = true)
{
    /// <summary>The design's defaults.</summary>
    public static StarlessPlateOptions Default { get; } = new StarlessPlateOptions();
}
