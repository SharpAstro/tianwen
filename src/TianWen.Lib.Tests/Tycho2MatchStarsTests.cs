using NSubstitute;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Direct unit tests of <see cref="Tycho2ColorCalibration.MatchStars"/> covering
/// the two-pass magnitude-gated matching pipeline. Uses an NSubstitute-backed
/// <see cref="ICelestialObjectDB"/> with hand-crafted synthetic candidates so we
/// can construct close-pair contamination scenarios that would be impossible to
/// reliably elicit from the real Tycho-2 catalog.
/// <para>
/// The synthetic WCS is a linear TAN with 1 arcsec/pixel scale centred at
/// (RA=12h, Dec=0deg); CRPix is at the centre of a 1001x1001 image. Detected
/// stars are placed on a 5x5 grid so that pass-1 gives MatchStars enough
/// matches (~24) to seed a stable zero-point. One detected star carries a
/// close-pair contaminant that pass-1 picks (closer angularly) and pass-2
/// rescues (correct one passes the mag gate, contaminant fails).
/// </para>
/// </summary>
[Collection("Astrometry")]
public class Tycho2MatchStarsTests
{
    private const int ImageWidth = 1001;
    private const int ImageHeight = 1001;
    private const double PixelScaleDeg = 1.0 / 3600.0;          // 1 arcsec/pixel
    private const double CenterRaHours = 12.0;
    private const double CenterDecDeg = 0.0;
    private const float ZeroPointTrue = 23.0f;                  // V_mag + 2.5*log10(Flux)
    private const float MatchRadiusArcsec = 5.0f;
    private const float MaxMagDiff = 1.5f;

    /// <summary>
    /// Build a simple linear TAN WCS with 1 arcsec/pixel scale, centred on the
    /// frame. Caller can use <see cref="WCS.PixelToSky"/> to derive the RA/Dec
    /// that the synthetic catalog must agree with.
    /// </summary>
    private static WCS BuildTestWcs()
    {
        return new WCS(CenterRA: CenterRaHours, CenterDec: CenterDecDeg)
        {
            CRPix1 = (ImageWidth + 1) * 0.5,
            CRPix2 = (ImageHeight + 1) * 0.5,
            CD1_1 = -PixelScaleDeg,  // east is to the left (standard sky convention)
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = PixelScaleDeg,
        };
    }

    /// <summary>Compute the expected detection flux for a star with apparent V=<paramref name="vMag"/>
    /// under the test zero-point. Inverts <c>V = zp - 2.5*log10(F)</c>.</summary>
    private static float FluxFor(float vMag) => MathF.Pow(10f, (ZeroPointTrue - vMag) / 2.5f);

    /// <summary>
    /// Minimum scaffolding to back an <see cref="ICelestialObjectDB"/> with a
    /// predefined RA/Dec -> candidate list map and an index -> CelestialObject
    /// lookup. CoordinateGrid is keyed off a small (1 arcmin) cell so test
    /// detections in adjacent pixels share a candidate list.
    /// </summary>
    private sealed class FakeCatalog
    {
        private readonly Dictionary<CatalogIndex, CelestialObject> _byIndex = new();
        private readonly Dictionary<(int RaCell, int DecCell), List<CatalogIndex>> _byCell = new();
        private ulong _nextIndex = 1;

        public CatalogIndex Add(double raHours, double decDeg, float vMag, float bMinusV)
        {
            var idx = (CatalogIndex)_nextIndex++;
            _byIndex[idx] = new CelestialObject(
                Index: idx,
                ObjectType: ObjectType.Star,
                RA: raHours,
                Dec: decDeg,
                Constellation: Constellation.Virgo,
                V_Mag: (Half)vMag,
                SurfaceBrightness: Half.NaN,
                BMinusV: (Half)bMinusV,
                CommonNames: new HashSet<string>());

            // Bin into 1 arcmin grid cells. Detected stars within ~1' of a
            // catalog star will find it via CoordinateGrid (more than enough
            // for the 5" matching radius the test uses).
            var key = CellKey(raHours, decDeg);
            if (!_byCell.TryGetValue(key, out var list))
                _byCell[key] = list = new List<CatalogIndex>();
            list.Add(idx);
            return idx;
        }

        public ICelestialObjectDB Build()
        {
            var grid = Substitute.For<IRaDecIndex>();
            grid[Arg.Any<double>(), Arg.Any<double>()].Returns(call =>
            {
                var ra = (double)call[0]; var dec = (double)call[1];
                var key = CellKey(ra, dec);
                // Return entries from a 3x3 neighbourhood so a query at one
                // cell still finds entries that landed in adjacent cells due
                // to a few-arcsec WCS offset. Mirrors the real IRaDecIndex
                // which uses overlap-buffered cells; without this, the 15"
                // systematic-offset test would lose any detection whose Dec
                // happens to straddle a cell boundary.
                var result = new List<CatalogIndex>();
                for (var dRa = -1; dRa <= 1; dRa++)
                    for (var dDec = -1; dDec <= 1; dDec++)
                    {
                        if (_byCell.TryGetValue((key.Item1 + dRa, key.Item2 + dDec), out var list))
                            result.AddRange(list);
                    }
                return (IReadOnlyCollection<CatalogIndex>)result;
            });

            var db = Substitute.For<ICelestialObjectDB>();
            db.CoordinateGrid.Returns(grid);
            db.TryLookupByIndex(Arg.Any<CatalogIndex>(), out Arg.Any<CelestialObject>())
                .Returns(call =>
                {
                    var idx = (CatalogIndex)call[0];
                    if (_byIndex.TryGetValue(idx, out var obj))
                    {
                        call[1] = obj;
                        return true;
                    }
                    call[1] = default(CelestialObject);
                    return false;
                });
            return db;
        }

        private static (int, int) CellKey(double raHours, double decDeg)
            // 6' RA cell at the equator, 6' Dec cell. Big enough that detected
            // stars and their catalog partners stay in the same cell even when
            // tests synthesise tens of arcsec of WCS bias (the systematic-offset
            // adaptive test moves catalog stars 15" off the detection).
            => ((int)Math.Floor(raHours * 60.0 * 15.0 / 6.0), (int)Math.Floor(decDeg * 60.0 / 6.0));
    }

    /// <summary>
    /// Helper: place detection at pixel (px, py) with magnitude <paramref name="vMag"/>
    /// and add a matching catalog star at exactly the WCS-derived RA/Dec.
    /// </summary>
    private static (ImagedStar Det, CatalogIndex Cat) AddCleanPair(
        WCS wcs, ConcurrentBag<ImagedStar> dets, FakeCatalog cat,
        float px, float py, float vMag, float bMinusV)
    {
        var sky = wcs.PixelToSky(px, py)!.Value;
        var idx = cat.Add(sky.RA, sky.Dec, vMag, bMinusV);
        var det = new ImagedStar(
            HFD: 2.5f, StarFWHM: 3.0f, SNR: 50f,
            Flux: FluxFor(vMag),
            // MatchStars adds +1 to centroid to convert 0-based detection to
            // 1-based FITS pixel for the WCS call, so we subtract 1 here.
            XCentroid: px - 1, YCentroid: py - 1,
            Ellipticity: 0.05f);
        dets.Add(det);
        return (det, idx);
    }

    [Fact]
    public void GivenSparseFieldThenMagGateInactiveAndFunnelDisables()
    {
        // With < MinForZeroPoint (=20) matches, the zero-point can't be
        // estimated; pass-1 results are returned verbatim and RejMagDiff=0.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();
        for (var i = 0; i < 10; i++)
        {
            AddCleanPair(wcs, dets, cat, 100f + i * 80f, 500f, vMag: 10f, bMinusV: 0.5f);
        }
        var stars = new StarList(dets);

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            stars, wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        matches.Count.ShouldBe(10);
        funnel.MagGateActive.ShouldBeFalse();
        funnel.ZeroPoint.ShouldBe(float.NaN);
        funnel.RejMagDiff.ShouldBe(0);
        funnel.Accepted.ShouldBe(10);
    }

    [Fact]
    public void GivenDenseFieldThenMagGateActivatesAndReportsZeroPoint()
    {
        // 25 clean pairs >= MinForZeroPoint; the gate must activate and the
        // reported zero-point lands close to the synthetic ZeroPointTrue
        // (some scatter from the discrete Half-precision V_mag storage).
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
            {
                AddCleanPair(wcs, dets, cat,
                    100f + col * 200f, 100f + row * 200f,
                    vMag: 9.0f + (row + col) * 0.2f,  // V 9..10.6
                    bMinusV: 0.5f);
            }

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.MagGateActive.ShouldBeTrue();
        funnel.ZeroPoint.ShouldBe(ZeroPointTrue, 0.05f);
        funnel.RejMagDiff.ShouldBe(0, "all 25 pairs are clean -- nothing to reject");
        matches.Count.ShouldBe(25);
    }

    [Fact]
    public void GivenClosePairContaminantThenMagGateRescuesCorrectMatch()
    {
        // 24 clean pairs seed the zero-point. The 25th detected star has a
        // contaminant: a bright Tycho candidate (V=6) sits 2" away, while the
        // correct V=10 candidate sits 3.5" away. Without the mag gate the
        // bright contaminant wins on angular distance; with the gate it
        // fails |6 - 10|=4 > maxMagDiff=1.5 and the correct one wins.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();

        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
            {
                if (row == 4 && col == 4) continue;  // skip slot for the contaminant scenario
                AddCleanPair(wcs, dets, cat,
                    100f + col * 200f, 100f + row * 200f,
                    vMag: 10.0f, bMinusV: 0.5f);
            }

        // Contaminant scenario at pixel (900, 900): detected at V=10, but a
        // V=6 catalog star sits at offset 2", and a V=10 catalog star sits
        // at offset 3.5". Both are within the 5" matching radius.
        const float detPx = 900f, detPy = 900f;
        var detSky = wcs.PixelToSky(detPx, detPy)!.Value;
        // 2" offset in RA at Dec=0 is 2/3600 hours; we place it east.
        var contaminantRa = detSky.RA + 2.0 / 3600.0 / 15.0;  // 2" east, deg->h /15
        cat.Add(contaminantRa, detSky.Dec, vMag: 6.0f, bMinusV: 1.5f);
        // 3.5" north for the correct match.
        cat.Add(detSky.RA, detSky.Dec + 3.5 / 3600.0, vMag: 10.0f, bMinusV: 0.5f);
        dets.Add(new ImagedStar(
            HFD: 2.5f, StarFWHM: 3.0f, SNR: 50f,
            Flux: FluxFor(10.0f),
            XCentroid: detPx - 1, YCentroid: detPy - 1,
            Ellipticity: 0.05f));

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.MagGateActive.ShouldBeTrue();
        funnel.Accepted.ShouldBe(25, "all 25 detections matched -- 24 clean, 1 rescued by gate");

        // The accepted match for the contaminant detection is the V=10 one, not the V=6 one.
        var rescued = matches.Find(m => m.Star.XCentroid == detPx - 1 && m.Star.YCentroid == detPy - 1);
        rescued.Tycho.V_Mag.ShouldBe((Half)10.0f, "mag gate must reject the V=6 contaminant");
    }

    [Fact]
    public void GivenContaminantWithoutCorrectMatchThenRejectedInRejMagDiffBucket()
    {
        // 24 clean pairs seed the gate; the 25th detection has ONLY a bright
        // V=6 candidate within radius (no correct partner). The gate rejects
        // it, and the funnel must attribute the loss to RejMagDiff, not
        // TolMissed -- otherwise we'd lose the diagnostic that says "you have
        // close-pair contamination, not lens distortion".
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();

        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
            {
                if (row == 4 && col == 4) continue;
                AddCleanPair(wcs, dets, cat,
                    100f + col * 200f, 100f + row * 200f,
                    vMag: 10.0f, bMinusV: 0.5f);
            }

        const float detPx = 900f, detPy = 900f;
        var detSky = wcs.PixelToSky(detPx, detPy)!.Value;
        cat.Add(detSky.RA + 2.0 / 3600.0 / 15.0, detSky.Dec, vMag: 6.0f, bMinusV: 1.5f);
        dets.Add(new ImagedStar(
            HFD: 2.5f, StarFWHM: 3.0f, SNR: 50f,
            Flux: FluxFor(10.0f),
            XCentroid: detPx - 1, YCentroid: detPy - 1,
            Ellipticity: 0.05f));

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.MagGateActive.ShouldBeTrue();
        funnel.Accepted.ShouldBe(24);
        funnel.TolMissed.ShouldBe(0, "the V=6 candidate WAS in the radius, just photometrically wrong");
        funnel.RejMagDiff.ShouldBe(1, "the V=6 candidate must end up in the RejMagDiff bucket");
        funnel.BR.RejMagDiff.ShouldBe(1, "quadrant tally must point at the bottom-right where the contaminant lives");
    }

    [Fact]
    public void GivenZeroFluxDetectionThenGateBypassedForThatStar()
    {
        // A detection with Flux <= 0 can't yield a predicted magnitude
        // (log10 undefined). Those stars must still be matchable on angle
        // alone -- the gate is bypassed per-star, not aborted globally.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();

        for (var i = 0; i < 24; i++)
        {
            AddCleanPair(wcs, dets, cat,
                100f + (i % 6) * 150f, 100f + (i / 6) * 200f,
                vMag: 10.0f, bMinusV: 0.5f);
        }

        // Zero-flux detection (degenerate but possible in real data near the
        // noise floor). The catalog candidate for it sits at the correct
        // angular position with a normal V mag.
        const float detPx = 900f, detPy = 900f;
        var detSky = wcs.PixelToSky(detPx, detPy)!.Value;
        cat.Add(detSky.RA, detSky.Dec, vMag: 11.0f, bMinusV: 0.5f);
        dets.Add(new ImagedStar(
            HFD: 2.5f, StarFWHM: 3.0f, SNR: 5f, Flux: 0f,
            XCentroid: detPx - 1, YCentroid: detPy - 1, Ellipticity: 0.05f));

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.MagGateActive.ShouldBeTrue();
        funnel.Accepted.ShouldBe(25, "zero-flux star bypasses gate and still matches on angle");
    }

    [Fact]
    public void GivenCleanFieldThenAdaptiveToleranceFloorsAtInput()
    {
        // Every catalog candidate is placed exactly at the WCS-predicted
        // position, so the probe pass measures ~0 residual. With nothing
        // suggesting the WCS is loose, the adaptive tolerance must NOT
        // exceed the caller's input -- the input is the floor.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
            {
                AddCleanPair(wcs, dets, cat,
                    100f + col * 200f, 100f + row * 200f,
                    vMag: 10.0f, bMinusV: 0.5f);
            }

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.EffectiveRadiusArcsec.ShouldBe(MatchRadiusArcsec, 0.01f, "clean field must not widen the tolerance");
        funnel.ProbeMedianArcsec.ShouldBeLessThan(0.5f, "probe should measure sub-arcsec residuals on a clean field");
        funnel.Accepted.ShouldBe(25);
    }

    [Fact]
    public void GivenSystematicWcsOffsetThenAdaptiveToleranceWidens()
    {
        // Simulate a master with WCS error that puts catalog stars
        // systematically ~15" away from detections. The probe must measure
        // that and widen the tolerance to accept them, even though the
        // caller's input was the 5" default. The mag gate stays active so
        // we know the probe pass produced enough survivors to seed zp.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();

        // 15" Dec offset plus a few arcsec of per-star noise. The probe's
        // median + 3*MAD math needs the residual distribution to have some
        // spread; an EXACT 15" offset on every star leaves MAD ~= 0, which
        // would push the adaptive tolerance right onto the boundary and
        // strict less-than in FindBestMatch rejects boundary residuals. A
        // couple of arcsec of jitter is what real plate-solve residuals
        // look like anyway.
        const double offsetDecDeg = 15.0 / 3600.0;
        var rng = new Random(1234);
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 5; col++)
            {
                var px = 100f + col * 200f;
                var py = 100f + row * 200f;
                var sky = wcs.PixelToSky(px, py)!.Value;
                var jitterArcsec = (rng.NextDouble() - 0.5) * 4.0;   // ~uniform [-2, +2]
                var jitterDeg = jitterArcsec / 3600.0;
                cat.Add(sky.RA, sky.Dec + offsetDecDeg + jitterDeg, vMag: 10.0f, bMinusV: 0.5f);
                dets.Add(new ImagedStar(
                    HFD: 2.5f, StarFWHM: 3.0f, SNR: 50f,
                    Flux: FluxFor(10.0f),
                    XCentroid: px - 1, YCentroid: py - 1,
                    Ellipticity: 0.05f));
            }

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.EffectiveRadiusArcsec.ShouldBeGreaterThan(MatchRadiusArcsec, "15\" WCS bias must widen the 5\" input tolerance");
        funnel.ProbeMedianArcsec.ShouldBeInRange(14f, 16f, "probe must recover the synthetic 15\" offset");
        funnel.Accepted.ShouldBe(25, "all 25 detections accepted after the tolerance widens to absorb the WCS offset");
    }

    [Fact]
    public void GivenSparseFieldThenAdaptiveProbeFallsBackToInput()
    {
        // Fewer than MinForAdaptiveTolerance (=20) probe matches means the
        // residual stats aren't trustworthy -- fall back to the caller's
        // tolerance and report NaN probe stats so the funnel log can show
        // "probe=off" rather than misleading numbers.
        var wcs = BuildTestWcs();
        var cat = new FakeCatalog();
        var dets = new ConcurrentBag<ImagedStar>();
        for (var i = 0; i < 10; i++)
        {
            AddCleanPair(wcs, dets, cat, 100f + i * 80f, 500f, vMag: 10f, bMinusV: 0.5f);
        }

        var (matches, funnel) = Tycho2ColorCalibration.MatchStars(
            new StarList(dets), wcs, cat.Build(),
            MatchRadiusArcsec, MaxMagDiff, dtJulianYears: 0,
            ImageWidth, ImageHeight);

        funnel.EffectiveRadiusArcsec.ShouldBe(MatchRadiusArcsec, 0.01f, "probe fallback must preserve the caller's tolerance");
        funnel.ProbeMedianArcsec.ShouldBe(float.NaN, "probe stats NaN signals fallback-to-input mode");
        funnel.ProbeMadArcsec.ShouldBe(float.NaN);
    }
}
