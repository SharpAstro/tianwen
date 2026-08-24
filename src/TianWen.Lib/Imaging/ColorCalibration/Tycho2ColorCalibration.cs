using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.ColorCalibration;

/// <summary>
/// Photometric color calibration against the Tycho-2 catalog.
/// Matches detected stars to Tycho-2 entries via WCS coordinates, extracts aperture
/// photometry from the unstretched image, compares observed RGB ratios to expected
/// ratios from B-V (blackbody) or Pickles SED + system throughput, and computes
/// per-channel white balance multipliers.
/// </summary>
public static class Tycho2ColorCalibration
{
    /// <summary>One matched star with observed and expected photometry.</summary>
    public readonly record struct StarMatch(
        double ObservedR, double ObservedG, double ObservedB,
        double ExpectedR, double ExpectedG, double ExpectedB,
        double Magnitude);

    /// <summary>Per-gate funnel diagnostic for <see cref="MatchStars"/>. Every
    /// detected star ends in exactly one of the bucket counters; the sum equals
    /// <see cref="Detected"/>. Used to answer "of 500 detected stars why did
    /// only N reach the photometric fit?" -- so we can target the dominant
    /// failure mode (faint stars with no catalog at all? real-image distortion
    /// pushing positions past the tolerance? Tycho-2 photometry gaps?).
    /// <para>
    /// The per-quadrant breakdown (<see cref="TL"/> / <see cref="TR"/> /
    /// <see cref="BL"/> / <see cref="BR"/>) splits stars by image-pixel
    /// position relative to the centre, so a tol-miss imbalance ("BL=89% miss
    /// vs centre TR=70% miss") signals corner distortion (SIP fix would help)
    /// rather than uniform registration drift.
    /// </para>
    /// </summary>
    public readonly record struct SpccFunnel(
        int Detected,
        int WcsFail,                // PixelToSky returned null (out-of-WCS-bounds star)
        int NoCandidates,           // CoordinateGrid cell is empty -- Tycho-2 has nothing nearby
        int TolMissed,              // no candidate within EffectiveRadiusArcsec at all
        int RejMagDiff,             // candidate(s) within radius, but all failed |V - predicted| <= maxMagDiff
        int NoBmv,                  // matched but Tycho-2 BMinusV is missing (~4% of entries)
        int NoVmag,                 // matched but V_Mag is also NaN (very rare)
        int Accepted,               // entered the initial photometry set
        bool MagGateActive,         // true once enough pass-1 matches existed to estimate a zero-point
        float ZeroPoint,            // photometric zero-point used by the mag gate (NaN when inactive)
        float EffectiveRadiusArcsec,// match radius actually used; either the caller's input (probe failed) or the adaptive value derived from the probe pass
        float ProbeMedianArcsec,    // median residual under the input WCS over the probe pass (NaN when probe didn't run)
        float ProbeMadArcsec,       // MAD of residuals (NaN when probe didn't run); a noisy WCS shows up as large MAD here
        SpccQuadrant TL,            // x < W/2, y < H/2 (image top-left, matches PNG render orientation)
        SpccQuadrant TR,            // x >= W/2, y < H/2
        SpccQuadrant BL,            // x < W/2, y >= H/2
        SpccQuadrant BR);           // x >= W/2, y >= H/2

    /// <summary>Per-quadrant slice of <see cref="SpccFunnel"/>. Carries only the
    /// counters whose distribution across the image is diagnostic of the failure
    /// mode (detect-density floor, tol-miss localisation, accept density).
    /// Sum of all four quadrants' <see cref="Detected"/> equals
    /// <see cref="SpccFunnel.Detected"/>.</summary>
    public readonly record struct SpccQuadrant(
        int Detected,
        int TolMissed,
        int RejMagDiff,
        int Accepted);

    /// <summary>Outcome of the white-balance fit. Carries both the converged
    /// multipliers and the iteration trail (initial-vs-final match count +
    /// iteration count) so the caller can log "started with 127 candidates,
    /// 119 survived 3-iter kappa-sigma" without re-deriving the numbers.
    /// <see cref="Funnel"/> exposes WHERE the 500-detected-star pipeline
    /// dropped to <see cref="InitialMatches"/>.</summary>
    public readonly record struct SpccWhiteBalanceResult(
        float R, float G, float B,
        int InitialMatches,
        int FinalMatches,
        int Iterations,
        SpccFunnel Funnel,
        string WhiteReferenceName = "")
    {
        /// <summary>Back-compat shim: callers that only want the survivor
        /// count read this; equivalent to <see cref="FinalMatches"/>.</summary>
        public int MatchCount => FinalMatches;
    }

    /// <summary>
    /// The spectrum that SPCC declares to be white: after calibration, an object with this
    /// spectral shape renders with equal R, G and B. Everything else is measured relative to it.
    /// <para>
    /// Without one, <see cref="ComputeExpectedRgbFromSed"/> returns raw instrumental band
    /// integrals and the fit forces the median star to the *sensor's* band ratio instead of to
    /// display-neutral -- which on an IMX492 OSC put a solar-type star at (0.772, 1.000, 0.594),
    /// i.e. visibly green, no matter how good the photometry was. The white reference is the one
    /// division that turns a spectral measurement into a white balance.
    /// </para>
    /// <para>
    /// The reference is a REAL spectrum resolved by record name, not a colour index. It was briefly
    /// a B-V-matched stellar template standing in for a galaxy, on the assumption that
    /// <c>pickles_sed.gs.gz</c> held only stars -- it does not: 25 SWIRE galaxy templates
    /// (Polletta et al.) sit alongside the 131 Pickles stellar ones, invisible to the white
    /// reference only because <c>PrecomputeSedBvIndex</c> excludes <c>GALAXY_*</c> from the B-V
    /// index. That exclusion is correct and must stay -- a galaxy template must never be matched to
    /// a STAR's colour index -- but the white reference is a by-name lookup, so it reaches them.
    /// The proxy was worth measuring and discarding: a B-V 0.70 stellar template gives band ratios
    /// (0.766, 0.586) against the Sb template's (0.872, 0.544), which on one SMC master was the
    /// difference between fitting (R 0.527, B 1.205) and (R 0.463, B 1.300).
    /// </para>
    /// </summary>
    /// <param name="Name">Human-readable label, for logs and UI.</param>
    /// <param name="SedName">
    /// Record name of the reference spectrum in <c>pickles_sed.gs.gz</c>, resolved through
    /// <see cref="FilterCurveDatabase.TryGetSedByName"/>. A REAL spectrum, not a colour index:
    /// the file carries the 25 SWIRE galaxy templates (Polletta et al.) alongside the 131 Pickles
    /// stellar ones, so both a galaxy and a star reference are exact.
    /// </param>
    public readonly record struct WhiteReference(string Name, string SedName)
    {
        /// <summary>
        /// PixInsight's and Siril's default SPCC white reference, and ours, so a TianWen render is
        /// comparable with a PixInsight one. <c>GALAXY_SB</c> is the SWIRE Sb template -- the middle
        /// of the spiral sequence, hence "average spiral".
        /// <para>
        /// The sub-type is the one judgement call here and it is worth a few percent. Through an
        /// IMX492 x Sony CFA system the reference band ratios run Sa (0.885, 0.530), Sb
        /// (0.872, 0.544), Sc (0.851, 0.562), and the fitted multipliers scale as the INVERSE of
        /// them -- a redder reference means a smaller red multiplier -- so on one SMC master the
        /// spiral sequence spans R 0.456-0.475 (4 %) and B 1.256-1.333 (6 %). Sb sits mid-sequence
        /// and within 0.4 % of the mean of Sa/Sb/Sc, so a single named template is used rather than
        /// an average: reproducible, and no arithmetic anyone has to trust. PixInsight's exact
        /// template is not published, so if a PI fit disagrees by a few percent the sub-type is the
        /// knob to reach for, not the maths.
        /// </para>
        /// </summary>
        public static WhiteReference AverageSpiralGalaxy => new("Average spiral galaxy (SWIRE Sb)", "GALAXY_SB");

        /// <summary>A solar-type star: under this reference the Sun renders exactly neutral. Useful
        /// for checking the calibration against intuition, and for matching a workflow that
        /// deliberately anchors on G2V rather than on a galaxy.</summary>
        public static WhiteReference SunG2V => new("Sun (G2V)", "G2V");

        /// <summary>Early-type spiral, the red end of the spiral sequence.</summary>
        public static WhiteReference SpiralSa => new("Spiral Sa (SWIRE)", "GALAXY_SA");

        /// <summary>Late-type spiral, the blue end of the spiral sequence.</summary>
        public static WhiteReference SpiralSc => new("Spiral Sc (SWIRE)", "GALAXY_SC");
    }

    /// <summary>
    /// Fraction of the frame's peak value above which an aperture pixel counts as clipped, and the
    /// star is dropped from the fit. A clipped star's channels are all pinned at the ceiling, so its
    /// measured colour is compressed toward neutral and it drags the whole fit toward identity --
    /// silently, because it is a perfectly well-formed photometry row.
    /// <para>
    /// Measured on a 3837x2619 SMC OSC master (621 matched stars): 101 stars carried a pixel above
    /// 80 % of peak, 88 above 95 %, 80 above 99 %, 62 above 99.9 %. Rejecting them moved the fit from
    /// (R 0.536, B 1.186) to (R 0.527, B 1.205) -- about 1.7 % -- and then held: cuts of 0.99, 0.95,
    /// 0.80 and 0.60 all landed within 0.4 % of each other. That plateau is the reason for trusting
    /// the rejection rather than the exact threshold, and why 0.98 is not a tuned number. The bias is
    /// small because the mag gate and the kappa-sigma pass already discard the worst offenders; this
    /// closes the rest, and costs nothing.
    /// </para>
    /// </summary>
    internal const float DefaultSaturationFraction = 0.98f;

    /// <summary>Default kappa for iterative SPCC rejection. 3.0 keeps ~99 %
    /// of a clean Gaussian sample while dropping the photometric outliers
    /// that bias the median (close-pair contamination, faint-star aperture
    /// noise, mismatched Tycho-2 catalog rows that passed the 5" coord
    /// gate). Looser than the hot-pixel sigma=8 because the WB fit's signal
    /// IS noisy by nature -- we want to drop only the obvious mismatches.</summary>
    private const float DefaultKappaSigma = 3.0f;

    /// <summary>Iteration cap; real data converges in 2-4 iters.</summary>
    private const int DefaultMaxIterations = 8;

    /// <summary>Minimum survivors before the iteration aborts. Below this,
    /// the kappa-sigma cut is on shaky statistical footing -- we'd rather
    /// keep an outlier than under-fit a 4-star sample.</summary>
    private const int MinSurvivors = 5;

    /// <summary>Minimum number of pass-1 (angular-only) matches required before
    /// we'll trust the median photometric zero-point enough to enable the mag
    /// gate in pass 2. Below this, the zero-point estimate is dominated by
    /// random photometry noise and could *worsen* matching by gating out
    /// genuine pairs. 20 is conservative: median of 20 floats with a clean
    /// inlier majority gives a ~0.1 mag standard error on the zero-point.</summary>
    private const int MinForZeroPoint = 20;

    /// <summary>Multiplier applied to the caller-supplied <c>matchRadiusArcsec</c>
    /// to choose the probe pass's generous tolerance. The probe needs to be
    /// loose enough to capture real matches even on distorted lenses (where
    /// per-star residuals are 10-20 arcsec) without admitting so much noise
    /// that the median residual itself becomes contaminated. 6x default 5"
    /// = 30" -- about 4-5x the median plate-solve residual seen on the SoL
    /// drizzle master, generous enough to bracket the real distribution.</summary>
    private const float ProbeRadiusMultiplier = 6.0f;

    /// <summary>Hard lower bound on the probe tolerance. Even when the caller
    /// passes a tiny matchRadiusArcsec (e.g. 1"), we need at least 30" of
    /// reach so the residual distribution we measure is *real* and not
    /// truncated by our own tolerance.</summary>
    private const float ProbeMinArcsec = 30f;

    /// <summary>Hard upper bound on the probe tolerance. Tycho-2 cells are sized
    /// so that 60" queries return useful candidate sets; beyond that, density
    /// shoots up and the nearest-neighbour scan starts admitting cross-matches
    /// (multiple plausible Tycho stars within range of one detection) that
    /// confuse the residual median. Loose-WCS masters legitimately *need* a
    /// final tolerance &gt;60", but we derive that from the probe stats, not
    /// from the probe radius itself.</summary>
    private const float ProbeMaxArcsec = 60f;

    /// <summary>Minimum probe matches before we trust the median + MAD enough
    /// to size the actual matching tolerance from them. Below this we keep
    /// the caller's matchRadiusArcsec and disable adaptive sizing -- the
    /// probe distribution can be dominated by the few stars that happen to
    /// be near a Tycho neighbour, biasing the median below the true RMS.</summary>
    private const int MinForAdaptiveTolerance = 20;

    /// <summary>Multiplier on the per-star residual sigma (1.4826 * MAD) when
    /// sizing the final tolerance. 3 captures ~99% of true matches under a
    /// Gaussian residual distribution; a real lens has heavier tails so we
    /// add the median to the budget to admit corner stars whose residuals
    /// pull median upward. The math: <c>tol = median + 3 * 1.4826 * MAD</c>.</summary>
    private const float AdaptiveSigmaMultiplier = 3.0f;

    /// <summary>Absolute floor on the adaptive tolerance, regardless of WCS
    /// quality. Tycho-2 catalog positions are accurate to ~0.1", proper-motion
    /// drift residual after PropagatePm is similar, centroid noise on bright
    /// stars is &lt;0.1". A 3" floor protects against pathologically tight
    /// adaptive estimates that would reject otherwise-fine matches sitting
    /// just outside an over-optimistic gate.</summary>
    private const float AdaptiveFloorArcsec = 3f;

    // Delegate type for computing expected RGB from a stellar B-V colour index.
    // Used by ExtractPhotometry to support both blackbody and SED-based approaches.
    private delegate (double R, double G, double B) ExpectedColorFunc(double bv);

    /// <summary>
    /// Computes per-channel white balance multipliers by matching detected stars to
    /// Tycho-2 and comparing observed photometry to B-V predicted colors.
    /// </summary>
    public static SpccWhiteBalanceResult? ComputeWhiteBalance(
        Image image,
        StarList stars,
        WCS wcs,
        ICelestialObjectDB db,
        int apertureRadius = 6,
        float matchRadiusArcsec = 5.0f,
        float maxMagDiff = 1.5f,
        int minStars = 5)
    {
        if (image.ChannelCount < 3 && image.ImageMeta.SensorType is not SensorType.RGGB) return null;

        var dtYr = ComputeDtJulianYears(image);
        var (matches, funnel) = MatchStars(stars, wcs, db, matchRadiusArcsec, maxMagDiff, dtYr,
            image.Width, image.Height);
        if (matches.Count < minStars) return null;

        // Same clipped-aperture rejection as the SED path; nothing about the blackbody model makes
        // a pinned channel measurable. NOTE: this overload has no white reference at all (raw
        // max-normalised blackbody ratios), so it targets the sensor's band ratios rather than
        // display-neutral -- it is the SED path's predecessor, currently with no callers in the
        // repo, kept because it is public surface on a published package.
        var photometry = ExtractPhotometry(image, matches, apertureRadius, DefaultSaturationFraction,
            bv => SyntheticStarFieldRenderer.BMinusVToRGB(bv));
        if (photometry.Count < minStars) return null;

        return ComputeMultipliers(photometry, funnel);
    }

    /// <summary>
    /// Spectrophotometric color calibration: matches detected stars to Tycho-2,
    /// maps each star's B-V to the closest Pickles stellar SED, integrates the SED
    /// through the per-channel system throughput curves to compute expected R/G/B
    /// ratios, then fits per-channel white balance multipliers from the observed
    /// aperture photometry.
    /// </summary>
    /// <param name="image">Unstretched RGB or Bayer image.</param>
    /// <param name="stars">Detected stars with centroids.</param>
    /// <param name="wcs">Plate-solve WCS solution.</param>
    /// <param name="db">Initialised celestial object database.</param>
    /// <param name="tsysR">System throughput for the red channel (QE × filter × CFA).</param>
    /// <param name="tsysG">System throughput for the green channel.</param>
    /// <param name="tsysB">System throughput for the blue channel.</param>
    /// <returns>White balance (R, G, B) multipliers, or null if insufficient matches.</returns>
    public static SpccWhiteBalanceResult? ComputeSpectrophotometricWhiteBalance(
        Image image,
        StarList stars,
        WCS wcs,
        ICelestialObjectDB db,
        FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB,
        int apertureRadius = 6,
        float matchRadiusArcsec = 5.0f,
        float maxMagDiff = 1.5f,
        int minStars = 5,
        WhiteReference? whiteReference = null,
        float saturationFraction = DefaultSaturationFraction,
        ILogger? logger = null)
    {
        if (image.ChannelCount < 3 && image.ImageMeta.SensorType is not SensorType.RGGB) return null;

        // Ensure SED database is loaded
        if (!FilterCurveDatabase.IsLoaded) return null;

        var dtYr = ComputeDtJulianYears(image);
        var (matches, funnel) = MatchStars(stars, wcs, db, matchRadiusArcsec, maxMagDiff, dtYr,
            image.Width, image.Height);

        // Both shortfalls below surface to the caller as the same "insufficient matches", which on
        // its own is a dead end -- it cannot distinguish "the WCS put the stars nowhere near the
        // catalog" from "they matched but the apertures were unusable". The funnel says which, so
        // log it here rather than making the next investigation re-derive it.
        if (matches.Count < minStars)
        {
            logger?.LogDebug("SPCC: {Matches} catalog matches < {MinStars} required; funnel {Funnel}",
                matches.Count, minStars, funnel);
            return null;
        }

        // Resolve the white reference ONCE, not per star: it is the same division for every row,
        // and re-integrating its SED 600 times would be pure waste.
        var whiteRef = whiteReference ?? WhiteReference.AverageSpiralGalaxy;
        var white = (R: 0.0, G: 1.0, B: 0.0);
        if (FilterCurveDatabase.TryGetSedByName(whiteRef.SedName, out var whiteSed)
            && IntegrateBandRatios(whiteSed, tsysR, tsysG, tsysB) is { } wr)
        {
            white = wr;
            logger?.LogDebug("SPCC: white reference '{Name}' [{Sed}] band ratios R={R:F4} G=1 B={B:F4}",
                whiteRef.Name, whiteRef.SedName, white.R, white.B);
        }
        else
        {
            // Degrade, do not fail: the fit is still worth having, it just lands on the sensor's
            // own band ratios instead of a display-neutral target, which renders green. Warn,
            // because that is a data/config fault (a renamed or missing SED record) and it is
            // invisible in the result -- the numbers look like a successful calibration.
            logger?.LogWarning("SPCC: white reference '{Name}' [{Sed}] did not resolve; falling back to raw instrumental band ratios, which will render green",
                whiteRef.Name, whiteRef.SedName);
        }

        var photometry = ExtractPhotometry(image, matches, apertureRadius, saturationFraction,
            bv => ComputeExpectedRgbFromSed(bv, tsysR, tsysG, tsysB, white), logger);
        if (photometry.Count < minStars)
        {
            logger?.LogDebug("SPCC: {Photometry} usable apertures of {Matches} matches < {MinStars} required; funnel {Funnel}",
                photometry.Count, matches.Count, minStars, funnel);
            return null;
        }

        // Stamped here rather than inside the fitter: the fitter is shared with the Tycho-2 B-V
        // path, which has no white reference at all, and an empty name there is the honest answer.
        return ComputeMultipliers(photometry, funnel) with { WhiteReferenceName = whiteRef.Name };
    }

    /// <summary>
    /// Maps a B-V colour index to the expected per-channel RGB a star of that colour should
    /// DISPLAY at, by integrating the closest Pickles SED through the system throughput and then
    /// dividing by the same integral for the white reference. G is exactly 1 by construction, so
    /// an object whose colour matches the reference comes out (1, 1, 1) and the fit that lands on
    /// it is a white balance rather than a statement about the sensor's band ratios.
    /// </summary>
    /// <param name="whiteReferenceRatios">
    /// Band ratios of the white reference from <see cref="IntegrateBandRatios"/>, resolved once by
    /// the caller. A non-positive R or B leaves the ratios untouched, so an unresolved reference
    /// degrades to raw instrumental ratios instead of dividing by zero.
    /// </param>
    private static (double R, double G, double B) ComputeExpectedRgbFromSed(
        double bv, FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB,
        (double R, double G, double B) whiteReferenceRatios)
    {
        var (r, _, b) = ComputeRawBandRatios(bv, tsysR, tsysG, tsysB);

        if (whiteReferenceRatios.R > 0) r /= whiteReferenceRatios.R;
        if (whiteReferenceRatios.B > 0) b /= whiteReferenceRatios.B;

        return (r, 1.0, b);
    }

    /// <summary>
    /// Integrates the Pickles SED closest to <paramref name="bv"/> through each channel's system
    /// throughput and returns the band ratios relative to green, i.e. how many electrons R and B
    /// collect for each one G collects. These are INSTRUMENTAL, not display, values: green is the
    /// widest and most sensitive band on an OSC, so both ratios come out well below 1 and using
    /// them directly as a display target renders everything green. Divide by the white reference's
    /// own ratios to get a display target -- see <see cref="ComputeExpectedRgbFromSed"/>.
    /// <para>
    /// Falls back to the blackbody approximation when the SED lookup fails or green integrates to
    /// nothing. That fallback is max-normalised rather than green-normalised, so it is re-scaled
    /// here to keep one convention across both paths; without the re-scale a star that missed the
    /// SED lookup would enter the fit on a different footing from its neighbours.
    /// </para>
    /// </summary>
    private static (double R, double G, double B) ComputeRawBandRatios(
        double bv, FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB)
    {
        if (FilterCurveDatabase.TryGetSedByBv(bv, out var sed)
            && IntegrateBandRatios(sed, tsysR, tsysG, tsysB) is { } ratios)
        {
            return ratios;
        }

        var (bbR, bbG, bbB) = SyntheticStarFieldRenderer.BMinusVToRGB(bv);
        return bbG > 0 ? (bbR / bbG, 1.0, bbB / bbG) : (bbR, bbG, bbB);
    }

    /// <summary>
    /// Integrates one spectrum through the three channel throughputs and returns
    /// <c>(R/G, 1, B/G)</c>, or <c>null</c> when green integrates to nothing (a spectrum with no
    /// overlap with the passband, which is a data fault rather than a faint star). One helper for
    /// both the per-star SED and the white reference, so the two can never be integrated by
    /// different arithmetic -- which matters more than it looks: SPCC divides one by the other, so
    /// any band-dependent factor common to both (the photon-vs-energy lambda weighting, for
    /// instance) cancels exactly, and that guarantee only holds while it is literally the same code.
    /// </summary>
    private static (double R, double G, double B)? IntegrateBandRatios(
        FilterCurve sed, FilterCurve tsysR, FilterCurve tsysG, FilterCurve tsysB)
    {
        var fluxG = FilterCurve.IntegrateSedThroughput(sed, tsysG);
        if (fluxG <= 0) return null;

        return (FilterCurve.IntegrateSedThroughput(sed, tsysR) / fluxG,
                1.0,
                FilterCurve.IntegrateSedThroughput(sed, tsysB) / fluxG);
    }

    /// <summary>
    /// Maps the image's FITS DATE-OBS to fractional Julian years since J2000.0
    /// for use as the dt input to <see cref="CoordinateUtils.PropagatePm"/>.
    /// Returns <c>0.0</c> (no propagation) when no plausible exposure date is
    /// available -- synthetic test frames, missing FITS header, or any
    /// pre-1900 value -- so the matcher keeps its prior behaviour exactly.
    /// </summary>
    private static double ComputeDtJulianYears(Image image)
    {
        var exposureStart = image.ImageMeta.ExposureStartTime;
        return exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;
    }

    /// <summary>
    /// Per-detected-star outcome of <see cref="FindBestMatch"/>. The terminal
    /// caller buckets are picked by inspecting these fields in order:
    /// <c>Match != null</c> -> Accepted; else <c>CandidatesInRadius == 0</c>
    /// -> TolMissed; else <c>RejectedByMagGate &gt; 0</c> -> RejMagDiff.
    /// This split lets the funnel distinguish "no Tycho star within tolerance"
    /// from "Tycho star(s) within tolerance but the photometric gate rejected
    /// them", which is the diagnostic we need to attribute SPCC losses to
    /// distortion vs close-pair mis-matching.
    /// </summary>
    private readonly record struct FindBestMatchResult(
        CelestialObject? Match,
        double DistanceDeg,
        int CandidatesInRadius,
        int RejectedByMagGate);

    /// <summary>
    /// Matches detected stars to Tycho-2 catalog entries. Catalog stars are
    /// projected from J2000 to the image epoch via proper motion before the
    /// angular-separation cut -- without this, the ~2.5% of Tycho-2 stars with
    /// |pm| > 30 mas/yr drift past the <paramref name="matchRadiusArcsec"/>
    /// tolerance over 26 years of catalog age, get mis-matched or dropped, and
    /// then survive into the photometric kappa-sigma pass as outliers that
    /// bias the white-balance fit.
    /// <para>
    /// Two-pass design: pass 1 runs angular-only matching exactly as the
    /// pre-mag-gate code did, then derives a photometric zero-point
    /// <c>zp = median(V_mag + 2.5 * log10(Flux))</c> from the survivors.
    /// Pass 2 re-runs matching with a per-star predicted magnitude
    /// <c>m_pred = zp - 2.5 * log10(Flux)</c> and rejects candidates whose
    /// <c>|V_mag - m_pred| &gt; maxMagDiff</c>. The gate catches the dense-
    /// field close-pair case where a bright Tycho star happens to be the
    /// angularly closest neighbour to a faint detection (or vice versa) and
    /// would otherwise be accepted on coordinates alone. When pass 1 produces
    /// fewer than <see cref="MinForZeroPoint"/> matches the zero-point can't
    /// be estimated reliably and we fall back to the pass-1 result with the
    /// gate disabled (<see cref="SpccFunnel.MagGateActive"/> = false).
    /// </para>
    /// </summary>
    internal static (List<(ImagedStar Star, CelestialObject Tycho)> Matches, SpccFunnel Funnel) MatchStars(
        StarList stars, WCS wcs, ICelestialObjectDB db,
        float matchRadiusArcsec, float maxMagDiff,
        double dtJulianYears,
        int imageWidth, int imageHeight)
    {
        var midX = imageWidth * 0.5f;
        var midY = imageHeight * 0.5f;

        // Probe pass: size the actual matching tolerance from the WCS's true
        // residual distribution on this image. Returns (effectiveRadiusArcsec,
        // probeMedian, probeMad) -- the latter two are NaN when the probe
        // sample was too small to trust, in which case effectiveRadius equals
        // the caller's input (legacy behaviour preserved).
        var (effectiveTolArcsec, probeMedianArcsec, probeMadArcsec) =
            ProbeAndSizeMatchTolerance(stars, wcs, db, matchRadiusArcsec, dtJulianYears);
        var effectiveTolDeg = effectiveTolArcsec / 3600.0;

        // Pass 1: angular-only match (mag gate disabled). The survivors here
        // seed the zero-point used by the mag gate in pass 2.
        var (coarseMatches, coarseFunnel) = RunMatchPass(
            stars, wcs, db, effectiveTolDeg, dtJulianYears,
            zeroPoint: float.NaN, maxMagDiff: float.PositiveInfinity,
            midX, midY,
            effectiveTolArcsec, probeMedianArcsec, probeMadArcsec);

        // Need a non-trivial sample to estimate the median zero-point. Below
        // the floor we keep pass 1 unchanged so we never regress matching
        // when the field is too sparse for the photometric gate to help.
        if (coarseMatches.Count < MinForZeroPoint
            || !TryComputeZeroPoint(coarseMatches, out var zp))
        {
            return (coarseMatches, coarseFunnel);
        }

        // Pass 2: re-match with the photometric gate. Throws away pass-1
        // results (some pass-1 accepted matches may flip to RejMagDiff and
        // vice versa). The funnel returned here is the canonical one --
        // counters reflect post-gate decisions, not pass-1's pre-gate state.
        return RunMatchPass(
            stars, wcs, db, effectiveTolDeg, dtJulianYears,
            zeroPoint: zp, maxMagDiff: maxMagDiff,
            midX, midY,
            effectiveTolArcsec, probeMedianArcsec, probeMadArcsec);
    }

    /// <summary>
    /// Probe the WCS quality on the actual image by running a generous-tolerance
    /// nearest-neighbour match and measuring the per-star residual distribution.
    /// Returns the tolerance that should be used for the real matching passes,
    /// plus the residual statistics for telemetry/logging.
    /// <para>
    /// The math: <c>tolerance = max(input, median + sigmaMultiplier * 1.4826 * MAD)</c>
    /// floored at <see cref="AdaptiveFloorArcsec"/>. Median + sigma captures
    /// ~99% of real matches under a Gaussian residual; adding the median (rather
    /// than just using sigma) admits corner stars whose residuals pull the
    /// median above the sigma-width of the central cluster.
    /// </para>
    /// <para>
    /// Returns the caller's input radius (with NaN probe stats) when the probe
    /// returned fewer than <see cref="MinForAdaptiveTolerance"/> matches --
    /// either the field is too sparse for adaptive sizing to be reliable, or
    /// the WCS is so far off that even a 60" probe can't find candidates.
    /// Either way, the legacy behaviour preserves the caller's choice.
    /// </para>
    /// </summary>
    private static (float EffectiveTolArcsec, float ProbeMedian, float ProbeMad) ProbeAndSizeMatchTolerance(
        StarList stars, WCS wcs, ICelestialObjectDB db,
        float inputRadiusArcsec, double dtJulianYears)
    {
        var probeTolArcsec = Math.Clamp(inputRadiusArcsec * ProbeRadiusMultiplier,
            ProbeMinArcsec, ProbeMaxArcsec);
        var probeTolDeg = probeTolArcsec / 3600.0;

        // One buffer for residuals (in arcsec). stackalloc when the star list
        // fits; otherwise an array allocation -- still cheap at ~10 KB for
        // a 5k-star field.
        Span<float> residuals = stars.Count <= 1024
            ? stackalloc float[stars.Count]
            : new float[stars.Count];
        var n = 0;

        foreach (var star in stars)
        {
            var sky = wcs.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            if (sky is not { } pos) continue;

            var candidates = db.CoordinateGrid[pos.RA, pos.Dec];
            if (candidates is not { Count: > 0 }) continue;

            var bestDistDeg = probeTolDeg;
            var found = false;
            foreach (var idx in candidates)
            {
                if (!db.TryLookupByIndex(idx, out var obj)) continue;
                if (obj.ObjectType is not ObjectType.Star) continue;
                if (Half.IsNaN(obj.BMinusV)) continue;

                double matchRa = obj.RA, matchDec = obj.Dec;
                if (dtJulianYears != 0.0
                    && db.TryGetTycho2Star(obj.Index, out var tyc)
                    && (tyc.PmRaTenthMasPerYr != 0 || tyc.PmDecTenthMasPerYr != 0))
                {
                    (matchRa, matchDec) = CoordinateUtils.PropagatePm(
                        obj.RA, obj.Dec,
                        tyc.PmRaMasPerYr, tyc.PmDecMasPerYr,
                        dtJulianYears);
                }

                var distDeg = AngularSeparation(pos.RA, pos.Dec, matchRa, matchDec);
                if (distDeg < bestDistDeg)
                {
                    bestDistDeg = distDeg;
                    found = true;
                }
            }
            if (found) residuals[n++] = (float)(bestDistDeg * 3600.0);
        }

        if (n < MinForAdaptiveTolerance)
        {
            return (inputRadiusArcsec, float.NaN, float.NaN);
        }

        var (probeMedian, probeMad) = StatisticsHelper.MedianAndMad(residuals[..n]);

        // Tolerance = max(input, median + K * sigma) with safety floor. Cap
        // at the probe radius -- we never trust an estimate beyond what we
        // actually observed.
        var sigmaArcsec = 1.4826f * probeMad;
        var adaptiveTol = probeMedian + AdaptiveSigmaMultiplier * sigmaArcsec;
        var effective = Math.Max(Math.Max(adaptiveTol, inputRadiusArcsec), AdaptiveFloorArcsec);
        effective = Math.Min(effective, probeTolArcsec);
        return (effective, probeMedian, probeMad);
    }

    /// <summary>
    /// One end-to-end pass over the detected star list. Pass <see cref="float.NaN"/>
    /// for <paramref name="zeroPoint"/> to disable the mag gate (pass 1
    /// behaviour); pass a finite value to enable it (pass 2).
    /// </summary>
    private static (List<(ImagedStar Star, CelestialObject Tycho)> Matches, SpccFunnel Funnel) RunMatchPass(
        StarList stars, WCS wcs, ICelestialObjectDB db,
        double matchRadiusDeg, double dtJulianYears,
        float zeroPoint, float maxMagDiff,
        float midX, float midY,
        float effectiveRadiusArcsec, float probeMedianArcsec, float probeMadArcsec)
    {
        var gateActive = !float.IsNaN(zeroPoint);
        var matches = new List<(ImagedStar, CelestialObject)>();

        // Per-gate counters. Every detected star ends in exactly one bucket;
        // sum == stars.Count by construction.
        int wcsFail = 0, noCand = 0, tolMissed = 0, rejMagDiff = 0, noBmv = 0, noVmag = 0;

        // Quadrant breakdown: split each detected star by (x, y) vs (W/2, H/2).
        // detected[k] is incremented for every star in quadrant k; tolMissed[k]
        // and accepted[k] only when those terminal buckets are hit. Lets the
        // caller spot e.g. "BR quadrant has 90% tol-miss vs centre at 65%" --
        // strong signal that lens distortion / SIP terms matter.
        Span<int> qDetected = stackalloc int[4];
        Span<int> qTolMissed = stackalloc int[4];
        Span<int> qRejMagDiff = stackalloc int[4];
        Span<int> qAccepted = stackalloc int[4];

        foreach (var star in stars)
        {
            // Quadrant index: 0=TL, 1=TR, 2=BL, 3=BR. y is the FITS row index;
            // we treat low y as "top" to match the rendered PNG orientation
            // that humans look at -- TL in the funnel output corresponds to
            // the upper-left of the master.png the renderer wrote.
            var qIdx = (star.XCentroid >= midX ? 1 : 0) | (star.YCentroid >= midY ? 2 : 0);
            qDetected[qIdx]++;

            var sky = wcs.PixelToSky(star.XCentroid + 1, star.YCentroid + 1);
            if (sky is not { } pos) { wcsFail++; continue; }

            var candidates = db.CoordinateGrid[pos.RA, pos.Dec];
            if (candidates is not { Count: > 0 }) { noCand++; continue; }

            // Predicted magnitude only meaningful when the gate is active AND
            // the detection has a positive flux. Negative/zero flux can occur
            // for borderline detections near the noise floor; we keep them
            // matchable in pass 2 by passing +Inf gate (effectively disabled
            // for this star), but they still get all the other filters.
            var predictedMag = gateActive && star.Flux > 0
                ? zeroPoint - 2.5f * MathF.Log10(star.Flux)
                : float.NaN;

            var result = FindBestMatch(star, pos, db, candidates, matchRadiusDeg,
                dtJulianYears, predictedMag, maxMagDiff);

            if (result.Match is not { } match)
            {
                if (result.CandidatesInRadius == 0) { tolMissed++; qTolMissed[qIdx]++; }
                else { rejMagDiff++; qRejMagDiff[qIdx]++; }
                continue;
            }
            if (Half.IsNaN(match.BMinusV)) { noBmv++; continue; }
            if (Half.IsNaN(match.V_Mag)) { noVmag++; continue; }

            matches.Add((star, match));
            qAccepted[qIdx]++;
        }

        var funnel = new SpccFunnel(
            Detected: stars.Count,
            WcsFail: wcsFail,
            NoCandidates: noCand,
            TolMissed: tolMissed,
            RejMagDiff: rejMagDiff,
            NoBmv: noBmv,
            NoVmag: noVmag,
            Accepted: matches.Count,
            MagGateActive: gateActive,
            ZeroPoint: gateActive ? zeroPoint : float.NaN,
            EffectiveRadiusArcsec: effectiveRadiusArcsec,
            ProbeMedianArcsec: probeMedianArcsec,
            ProbeMadArcsec: probeMadArcsec,
            TL: new SpccQuadrant(qDetected[0], qTolMissed[0], qRejMagDiff[0], qAccepted[0]),
            TR: new SpccQuadrant(qDetected[1], qTolMissed[1], qRejMagDiff[1], qAccepted[1]),
            BL: new SpccQuadrant(qDetected[2], qTolMissed[2], qRejMagDiff[2], qAccepted[2]),
            BR: new SpccQuadrant(qDetected[3], qTolMissed[3], qRejMagDiff[3], qAccepted[3]));
        return (matches, funnel);
    }

    /// <summary>
    /// Robust median zero-point estimator. For each (detection, catalog) pair
    /// with positive flux and finite V_mag, accumulates the per-star sample
    /// <c>V_mag + 2.5 * log10(Flux)</c>. The median over those samples is the
    /// zero-point that maps detection flux to apparent V magnitude:
    /// <c>m_pred = zp - 2.5 * log10(Flux)</c>. Median (not mean) is used so
    /// the inevitable ~10-15% of pass-1 mis-matches do not bias the estimate.
    /// Returns <c>false</c> if fewer than <see cref="MinForZeroPoint"/> usable
    /// samples survived, signalling the caller to keep the gate disabled.
    /// </summary>
    private static bool TryComputeZeroPoint(
        List<(ImagedStar Star, CelestialObject Tycho)> matches, out float zeroPoint)
    {
        Span<float> samples = matches.Count <= 256
            ? stackalloc float[matches.Count]
            : new float[matches.Count];
        var k = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var (s, t) = matches[i];
            if (s.Flux <= 0 || Half.IsNaN(t.V_Mag)) continue;
            samples[k++] = (float)t.V_Mag + 2.5f * MathF.Log10(s.Flux);
        }
        if (k < MinForZeroPoint) { zeroPoint = float.NaN; return false; }

        // MedianFast permutes in place (QuickSelect, partial sort) so we must
        // not rely on the buffer ordering after this call -- we just consume
        // the return value.
        zeroPoint = StatisticsHelper.MedianFast(samples[..k]);
        return true;
    }

    /// <summary>
    /// Picks the angularly-closest catalog candidate within
    /// <paramref name="matchRadiusDeg"/> that also passes the magnitude gate
    /// (when <paramref name="predictedMag"/> is finite). Tracks per-star
    /// diagnostic counters so the caller can attribute "no Tycho near" vs
    /// "Tycho near but photometrically inconsistent" to the right funnel
    /// bucket.
    /// </summary>
    private static FindBestMatchResult FindBestMatch(
        ImagedStar star, (double RA, double Dec) sky,
        ICelestialObjectDB db, IReadOnlyCollection<CatalogIndex> candidates,
        double matchRadiusDeg, double dtJulianYears,
        float predictedMag, float maxMagDiff)
    {
        CelestialObject? bestMatch = null;
        var bestDist = matchRadiusDeg;
        var gateActive = !float.IsNaN(predictedMag);
        var candidatesInRadius = 0;
        var rejectedByMagGate = 0;

        foreach (var idx in candidates)
        {
            if (!db.TryLookupByIndex(idx, out var obj)) continue;
            if (obj.ObjectType is not ObjectType.Star) continue;
            if (Half.IsNaN(obj.BMinusV)) continue;

            // Propagate Tycho-2 J2000 position to the image epoch. Cross-walked
            // HIP/HD indices arrive here with obj.Index already resolved to the
            // underlying TYC entry (TryLookupTycho2StarFromBinaryData constructs
            // the CelestialObject with the TYC index, not the original query),
            // so TryGetTycho2Star(obj.Index, ...) finds the pm whether the
            // caller queried by TYC, HIP, or HD. Truly non-Tycho-2 candidates
            // (no cross-ref to Tycho-2) skip the propagation -- there's no pm
            // to apply for them anyway.
            double matchRa = obj.RA, matchDec = obj.Dec;
            if (dtJulianYears != 0.0
                && db.TryGetTycho2Star(obj.Index, out var tyc)
                && (tyc.PmRaTenthMasPerYr != 0 || tyc.PmDecTenthMasPerYr != 0))
            {
                (matchRa, matchDec) = CoordinateUtils.PropagatePm(
                    obj.RA, obj.Dec,
                    tyc.PmRaMasPerYr, tyc.PmDecMasPerYr,
                    dtJulianYears);
            }

            var distDeg = AngularSeparation(sky.RA, sky.Dec, matchRa, matchDec);
            // Track angular-tolerance candidates regardless of the mag gate so
            // we can distinguish TolMissed (none in radius) from RejMagDiff
            // (some in radius, all gated out).
            if (distDeg >= matchRadiusDeg) continue;
            candidatesInRadius++;

            // Mag gate: when active, require |V_mag - predicted| <= maxMagDiff.
            // Candidates failing the gate are counted but not eligible to
            // become the bestMatch. NaN V_mag candidates skip the gate
            // entirely so they can still be the closest match -- a NaN V
            // is rare (the noVmag funnel bucket catches them later) and we
            // don't want a NaN to short-circuit a genuine angular match.
            if (gateActive && !Half.IsNaN(obj.V_Mag)
                && MathF.Abs((float)obj.V_Mag - predictedMag) > maxMagDiff)
            {
                rejectedByMagGate++;
                continue;
            }

            if (distDeg < bestDist)
            {
                bestDist = distDeg;
                bestMatch = obj;
            }
        }

        return new FindBestMatchResult(bestMatch, bestDist, candidatesInRadius, rejectedByMagGate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double AngularSeparation(double ra1, double dec1, double ra2, double dec2)
    {
        var dRa = (ra1 - ra2) * Math.PI / 180.0;
        var dDec = (dec1 - dec2) * Math.PI / 180.0;
        var a = Math.Sin(dDec / 2); a *= a;
        var b = Math.Sin(dRa / 2); b *= b;
        var c = Math.Cos(dec1 * Math.PI / 180.0) * Math.Cos(dec2 * Math.PI / 180.0);
        return 2.0 * Math.Asin(Math.Sqrt(a + c * b)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Extracts per-channel aperture photometry for each matched star.
    /// For Bayer (RGGB) images, samples raw sub-pixels to capture the true sensor
    /// channel response including the 2x green oversampling bias.
    /// <para>
    /// Stars whose aperture contains a clipped pixel are dropped -- see
    /// <see cref="DefaultSaturationFraction"/> for why and for the measurement.
    /// </para>
    /// </summary>
    private static List<StarMatch> ExtractPhotometry(
        Image image, List<(ImagedStar Star, CelestialObject Tycho)> matches, int apertureRadius,
        float saturationFraction, ExpectedColorFunc expectedColor, ILogger? logger = null)
    {
        var (channelCount, width, height) = image.Shape;
        var isBayer = channelCount == 1 && image.ImageMeta.SensorType is SensorType.RGGB;
        var result = new List<StarMatch>(matches.Count);

        // The ceiling is the frame's own peak, not a sensor full-scale card: an integrated master
        // has no meaningful ADU saturation level (the stack rescales), and clipped stars pile up
        // exactly at the peak whatever it happens to be. If nothing in the frame is clipped this
        // costs the single brightest star, which is immaterial against a few hundred matches.
        var clipLevel = saturationFraction > 0 ? saturationFraction * image.MaxValue : float.PositiveInfinity;
        var clipped = 0;

        foreach (var (star, tycho) in matches)
        {
            var (cx, cy) = (star.XCentroid, star.YCentroid);
            var annulusInner = apertureRadius + 2;
            var annulusOuter = apertureRadius + 5;

            var obsR = 0.0; var obsG = 0.0; var obsB = 0.0;
            var aperturePeak = float.NegativeInfinity;
            var apR = 0; var apG = 0; var apB = 0;
            var bgR = 0.0; var bgG = 0.0; var bgB = 0.0;
            var bgRc = 0; var bgGc = 0; var bgBc = 0;

            for (var y = (int)(cy - annulusOuter); y <= (int)(cy + annulusOuter); y++)
            {
                for (var x = (int)(cx - annulusOuter); x <= (int)(cx + annulusOuter); x++)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height) continue;
                    var dx = x - cx; var dy = y - cy;
                    var dist = Math.Sqrt(dx * dx + dy * dy);

                    var isAp = dist <= apertureRadius;
                    var isBg = !isAp && dist >= annulusInner && dist <= annulusOuter;
                    if (!isAp && !isBg) continue;

                    if (isBayer)
                    {
                        var v = image[0, y, x];
                        if (float.IsNaN(v)) continue;
                        var ch = BayerChannel(x, y);
                        if (isAp && v > aperturePeak) aperturePeak = v;
                        if (isAp) { AddChannel(ch, v, ref obsR, ref obsG, ref obsB, ref apR, ref apG, ref apB); }
                        else { AddChannel(ch, v, ref bgR, ref bgG, ref bgB, ref bgRc, ref bgGc, ref bgBc); }
                    }
                    else
                    {
                        if (float.IsNaN(image[0, y, x])) continue;
                        if (isAp)
                        {
                            var pr = image[0, y, x]; var pg = image[1, y, x]; var pb = image[2, y, x];
                            // Any channel clipping disqualifies the star: the ratio is what SPCC
                            // measures, so one pinned channel is enough to corrupt it.
                            if (pr > aperturePeak) aperturePeak = pr;
                            if (pg > aperturePeak) aperturePeak = pg;
                            if (pb > aperturePeak) aperturePeak = pb;
                            obsR += pr; obsG += pg; obsB += pb;
                            apR++;
                        }
                        else
                        {
                            bgR += image[0, y, x]; bgG += image[1, y, x]; bgB += image[2, y, x];
                            bgRc++;
                        }
                    }
                }
            }

            var apPixels = isBayer ? apR + apG + apB : apR;
            var bgPixels = isBayer ? bgRc + bgGc + bgBc : bgRc;
            if (apPixels < 3 || bgPixels < 5) continue;

            if (aperturePeak >= clipLevel)
            {
                clipped++;
                continue;
            }

            if (isBayer)
            {
                var netR = obsR - (bgR / Math.Max(bgRc, 1)) * apR;
                var netG = obsG - (bgG / Math.Max(bgGc, 1)) * apG;
                var netB = obsB - (bgB / Math.Max(bgBc, 1)) * apB;
                if (netR <= 0 || netG <= 0 || netB <= 0) continue;
                var (expR, expG, expB) = expectedColor((double)tycho.BMinusV);
                result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
            }
            else
            {
                var bgPerPixelR = bgR / bgPixels; var bgPerPixelG = bgG / bgPixels; var bgPerPixelB = bgB / bgPixels;
                var netR = obsR - bgPerPixelR * apPixels;
                var netG = obsG - bgPerPixelG * apPixels;
                var netB = obsB - bgPerPixelB * apPixels;
                if (netR <= 0 || netG <= 0 || netB <= 0) continue;
                var (expR, expG, expB) = expectedColor((double)tycho.BMinusV);
                result.Add(new StarMatch(netR, netG, netB, expR, expG, expB, (double)tycho.V_Mag));
            }
        }

        if (clipped > 0)
        {
            logger?.LogDebug("SPCC: dropped {Clipped} of {Matches} matched stars for a clipped aperture pixel (>= {Level:F4}, {Frac:P0} of frame peak {Peak:F4})",
                clipped, matches.Count, clipLevel, saturationFraction, image.MaxValue);
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddChannel(int ch, double v, ref double r, ref double g, ref double b, ref int rc, ref int gc, ref int bc)
    {
        switch (ch)
        {
            case 0: r += v; rc++; break;
            case 1: g += v; gc++; break;
            case 2: b += v; bc++; break;
        }
    }

    /// <summary>Returns 0=R, 1=G, 2=B for an RGGB Bayer pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BayerChannel(int x, int y) => ((y & 1) << 1) | (x & 1) switch
    {
        0 when (y & 1) == 0 => 0, // R at even, even
        1 when (y & 1) == 0 => 1, // G at odd, even
        0 when (y & 1) == 1 => 1, // G at even, odd
        _ => 2,                    // B at odd, odd
    };

    /// <summary>
    /// Computes per-channel white balance multipliers from observed and expected channel values.
    /// Exposed for testing. Internally runs iterative kappa-sigma rejection on the
    /// per-channel ratios; the returned wbR / wbB are the converged medians clamped
    /// to [0.5, 2.0] (values outside that range indicate sensor/model mismatch
    /// rather than correctable color cast).
    /// </summary>
    internal static (float R, float G, float B) ComputeMultipliers(
        ReadOnlySpan<float> obsR, ReadOnlySpan<float> obsG, ReadOnlySpan<float> obsB,
        ReadOnlySpan<float> expR, ReadOnlySpan<float> expG, ReadOnlySpan<float> expB)
    {
        var n = obsR.Length;
        var rRatios = new float[n];
        var bRatios = new float[n];

        var k = 0;
        for (var i = 0; i < n; i++)
        {
            if (obsG[i] <= 0 || expG[i] <= 0 || obsR[i] <= 0 || obsB[i] <= 0) continue;
            var norm = obsG[i] / expG[i];
            rRatios[k] = expR[i] / obsR[i] * norm;
            bRatios[k] = expB[i] / obsB[i] * norm;
            k++;
        }

        var result = FitIterativeKappaSigma(rRatios.AsSpan(0, k), bRatios.AsSpan(0, k));
        var wbR = Math.Clamp(result.R, 0.5f, 2.0f);
        var wbB = Math.Clamp(result.B, 0.5f, 2.0f);
        return (wbR, 1f, wbB);
    }

    /// <summary>
    /// Computes per-channel white balance multipliers via iterative kappa-sigma
    /// fit on the observed/expected ratios. Same algorithm as the public
    /// span overload, but returns the iteration outcome (initial vs. final
    /// match counts + iteration count) so the caller can log the funnel.
    /// </summary>
    private static SpccWhiteBalanceResult ComputeMultipliers(List<StarMatch> photometry, SpccFunnel funnel)
    {
        var rRatios = new float[photometry.Count];
        var bRatios = new float[photometry.Count];

        // Same valid-input filter as the span overload -- skip rows that
        // would produce a divide-by-zero or NaN in the ratio. Pack the
        // surviving rows into the front of the array so the iteration
        // works on a contiguous span.
        var k = 0;
        for (var i = 0; i < photometry.Count; i++)
        {
            var m = photometry[i];
            if (m.ObservedG <= 0 || m.ExpectedG <= 0 || m.ObservedR <= 0 || m.ObservedB <= 0) continue;
            var norm = (float)(m.ObservedG / m.ExpectedG);
            rRatios[k] = (float)(m.ExpectedR / m.ObservedR * norm);
            bRatios[k] = (float)(m.ExpectedB / m.ObservedB * norm);
            k++;
        }

        var fit = FitIterativeKappaSigma(rRatios.AsSpan(0, k), bRatios.AsSpan(0, k));
        // The reference name rides along because a bare triple cannot be judged: the same image
        // against Sa / Sb / Sc spans 4 % in R and 6 % in B (see WhiteReference), which is the single
        // largest term in the error budget. A number reported without it invites a comparison
        // against a PixInsight run that may have used a different reference entirely.
        return new SpccWhiteBalanceResult(
            R: Math.Clamp(fit.R, 0.1f, 10f),
            G: 1f,
            B: Math.Clamp(fit.B, 0.1f, 10f),
            InitialMatches: photometry.Count,
            FinalMatches: fit.FinalCount,
            Iterations: fit.Iterations,
            Funnel: funnel);
    }

    /// <summary>
    /// Iterative kappa-sigma fit on two correlated ratio streams (one per
    /// non-reference channel). Each iteration:
    /// <list type="number">
    ///   <item>Compute median of currently-live R / B ratios.</item>
    ///   <item>Compute MAD of (ratio - median) over those same live entries.</item>
    ///   <item>Reject rows where either channel exceeds
    ///   <c>kappa * 1.4826 * MAD</c> from the channel median.</item>
    ///   <item>Stop when no new rejections, or survivors would dip below
    ///   <see cref="MinSurvivors"/>, or max iterations hit.</item>
    /// </list>
    /// Rejection is OR-combined across channels (a star with bad R ratio
    /// AND good B ratio still drops -- it's likely a contaminated photometry
    /// entry overall, not just one channel). When MAD is 0 on a channel
    /// (uniform inlier subset) that channel contributes no rejection
    /// signal -- the loop continues on the other channel until convergence.
    /// </summary>
    private static (float R, float B, int FinalCount, int Iterations) FitIterativeKappaSigma(
        Span<float> rRatios, Span<float> bRatios,
        float kappa = DefaultKappaSigma, int maxIterations = DefaultMaxIterations)
    {
        var n = rRatios.Length;
        if (n == 0) return (1f, 1f, 0, 0);
        if (n < MinSurvivors)
        {
            // Sample too small for kappa-sigma to be statistically meaningful;
            // fall back to plain median. Tests with n=1 / n=5 hit this path.
            var rSorted = rRatios.ToArray(); Array.Sort(rSorted);
            var bSorted = bRatios.ToArray(); Array.Sort(bSorted);
            return (rSorted[n / 2], bSorted[n / 2], n, 0);
        }

        // Track survival via a parallel alive[] flag so we don't need to
        // physically compact the arrays on each iteration. Allocating once
        // up front is cheaper than per-iter compaction for the typical
        // 100-300 star photometry list.
        Span<bool> alive = stackalloc bool[n];
        alive.Fill(true);
        var liveCount = n;
        var medianR = 0f;
        var medianB = 0f;
        var iter = 0;

        // Work buffer reused per iteration to read out live values, sort,
        // and compute median/MAD.
        Span<float> workR = stackalloc float[n];
        Span<float> workB = stackalloc float[n];

        for (iter = 0; iter < maxIterations; iter++)
        {
            // Collect live values into the contiguous prefix of the work buf.
            var liveIdx = 0;
            for (var i = 0; i < n; i++)
            {
                if (alive[i])
                {
                    workR[liveIdx] = rRatios[i];
                    workB[liveIdx] = bRatios[i];
                    liveIdx++;
                }
            }
            workR[..liveIdx].Sort();
            workB[..liveIdx].Sort();
            medianR = workR[liveIdx / 2];
            medianB = workB[liveIdx / 2];

            // Compute MAD per channel against its own median. We can reuse
            // the work buffers because we've already consumed the sorted
            // values to read the medians.
            for (var i = 0; i < liveIdx; i++)
            {
                workR[i] = MathF.Abs(workR[i] - medianR);
                workB[i] = MathF.Abs(workB[i] - medianB);
            }
            workR[..liveIdx].Sort();
            workB[..liveIdx].Sort();
            var madR = workR[liveIdx / 2];
            var madB = workB[liveIdx / 2];

            // Kappa-sigma threshold; MAD * 1.4826 ≈ stddev for a Gaussian.
            // A zero MAD means the inlier subset is uniform on that channel,
            // which signals "no spread to threshold against" -- skip
            // rejection on that channel rather than rejecting everything
            // (threshold=0 would).
            var thresholdR = madR > 0f ? kappa * 1.4826f * madR : float.PositiveInfinity;
            var thresholdB = madB > 0f ? kappa * 1.4826f * madB : float.PositiveInfinity;

            // First pass: count the rejections this iteration would make.
            // We need this up front so the floor check (MinSurvivors) can
            // decide whether to commit them -- a single iteration that
            // would drop us below the floor would otherwise leave us in
            // an under-fit state.
            var wouldReject = 0;
            for (var i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                if (MathF.Abs(rRatios[i] - medianR) > thresholdR ||
                    MathF.Abs(bRatios[i] - medianB) > thresholdB)
                {
                    wouldReject++;
                }
            }
            if (wouldReject == 0) break;
            if (liveCount - wouldReject < MinSurvivors)
            {
                // Don't commit; we'd be fitting too thin a remainder.
                // Keep the current medians as the converged answer.
                break;
            }

            // Commit rejections.
            for (var i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                if (MathF.Abs(rRatios[i] - medianR) > thresholdR ||
                    MathF.Abs(bRatios[i] - medianB) > thresholdB)
                {
                    alive[i] = false;
                    liveCount--;
                }
            }
        }

        return (medianR, medianB, liveCount, iter);
    }
}
