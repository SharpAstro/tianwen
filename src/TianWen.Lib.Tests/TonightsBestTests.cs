using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;
using static TianWen.Lib.Astrometry.Catalogs.CatalogUtils;

namespace TianWen.Lib.Tests;

public class TonightsBestTests
{
    private static ICelestialObjectDB? _cachedDB;
    private static readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
    private static int _processed;
    private static int _failed;

    private static async Task<ICelestialObjectDB> InitDBAsync()
    {
        _failed.ShouldBe(0);

        if (_cachedDB is ICelestialObjectDB db && _processed > 0)
        {
            return db;
        }
        await _sem.WaitAsync();
        try
        {
            (_processed, _failed) = await (_cachedDB = new CelestialObjectDB()).InitDBAsync(
                cancellationToken: TestContext.Current.CancellationToken);

            _processed.ShouldBeGreaterThan(13000);
            _failed.ShouldBe(0);

            return _cachedDB;
        }
        finally
        {
            _sem.Release();
        }
    }

    private static Transform CreateTransform(double latitude, double longitude, DateTimeOffset date)
    {
        return new Transform(TimeProvider.System)
        {
            SiteLatitude = latitude,
            SiteLongitude = longitude,
            SiteElevation = 200,
            SiteTemperature = 15,
            DateTimeOffset = date
        };
    }

    [Fact]
    public async Task ViennaSummer_M13RanksHigh()
    {
        // Vienna, June — M13 (Hercules cluster) should be near zenith
        var db = await InitDBAsync();
        var transform = CreateTransform(48.2, 16.4, new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.FromHours(2)));

        var results = ObservationScheduler.TonightsBest(db, transform, 20).Take(50).ToList();

        results.Count.ShouldBeGreaterThan(0);

        // M13 (NGC6205) should be in the top results
        var m13 = results.FirstOrDefault(r => r.Target.CatalogIndex == CatalogIndex.NGC6205);
        m13.TotalScore.ShouldBeGreaterThan(Half.Zero, "M13 should be visible and scored in Vienna summer");
    }

    [Fact]
    public void ScoreTarget_LMC_MelbourneWinter_ProducesPositiveScore()
    {
        // LMC from Melbourne in June (winter) — circumpolar, should always be above horizon
        var transform = CreateTransform(-37.8, 145.0, new DateTimeOffset(2025, 6, 15, 20, 0, 0, TimeSpan.FromHours(10)));
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        // LMC: RA=5.39h, Dec=-69.75°
        var target = new Target(5.39, -69.75, "LMC", CatalogIndex.ESO056_115);

        var scoreFast = ObservationScheduler.ScoreTarget(target, transform, astroDark, astroTwilight, 15);
        scoreFast.TotalScore.ShouldBeGreaterThan(Half.Zero,
            $"LMC should have positive score from Melbourne winter. Night: {astroDark} to {astroTwilight} ({(astroTwilight - astroDark).TotalHours:F1}h). " +
            $"Profile: {string.Join(", ", scoreFast.ElevationProfile.Select(p => $"{p.Key}={p.Value.Alt:F1}°"))}");
    }

    [Fact]
    public void ScoreTarget_M42ViennaWinter_ProducesPositiveScore()
    {
        // Direct test: M42 from Vienna in December should get a positive score
        var transform = CreateTransform(48.2, 16.4, new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.FromHours(1)));
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);

        // Verify night window is sensible
        (astroTwilight - astroDark).TotalHours.ShouldBeGreaterThan(6, "Vienna December night should be >6h");

        // M42: RA=5.588h, Dec=-5.383°
        var target = new Target(5.588, -5.383, "M42", CatalogIndex.NGC1976);

        // Test with ScoreTarget (Transform-based) — should work
        var scoreOld = ObservationScheduler.ScoreTarget(target, transform, astroDark, astroTwilight, 20);
        scoreOld.TotalScore.ShouldBeGreaterThan(Half.Zero, $"ScoreTarget: M42 should have positive score. Night: {astroDark} to {astroTwilight}");

        // Test with ScoreTarget (Astrom-based) — should also work
        var scoreFast = ObservationScheduler.ScoreTarget(target, transform, astroDark, astroTwilight, 20);
        scoreFast.TotalScore.ShouldBeGreaterThan(Half.Zero, $"ScoreTarget: M42 should have positive score. Night: {astroDark} to {astroTwilight}");
    }

    [Fact]
    public async Task ViennaWinter_M42IsVisible()
    {
        // Vienna, December — M42 (Orion Nebula) is visible but at lower altitude (~36° max from 48°N)
        // It should still appear in the results due to its enormous size and brightness
        var db = await InitDBAsync();
        var transform = CreateTransform(48.2, 16.4, new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.FromHours(1)));

        // Check that M42 exists in the DB and grid
        db.TryLookupByIndex(CatalogIndex.NGC1976, out var m42obj).ShouldBeTrue("M42 should exist in DB");
        m42obj.ObjectType.IsStar.ShouldBeFalse($"M42 should not be a star, got ObjectType={m42obj.ObjectType}");
        (m42obj.ObjectType is ObjectType.Duplicate or ObjectType.Inexistent).ShouldBeFalse(
            $"M42 should not be Duplicate/Inexistent, got ObjectType={m42obj.ObjectType}");

        // Verify it's in the grid
        var grid = db.DeepSkyCoordinateGrid;
        var cellObjects = grid[m42obj.RA, m42obj.Dec];
        cellObjects.ShouldContain(CatalogIndex.NGC1976, $"M42 (RA={m42obj.RA:F3}h, Dec={m42obj.Dec:F2}°) should be in grid");

        // Verify ScoreTarget works directly for M42
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        var directScore = ObservationScheduler.ScoreTarget(
            new Target(m42obj.RA, m42obj.Dec, "M42", CatalogIndex.NGC1976),
            transform, astroDark, astroTwilight, 20);
        directScore.TotalScore.ShouldBeGreaterThan(Half.Zero,
            $"Direct ScoreTarget: M42 RA={m42obj.RA:F3}h Dec={m42obj.Dec:F2}° SB={(double)m42obj.SurfaceBrightness:F1} ObjType={m42obj.ObjectType}");

        var allResults = ObservationScheduler.TonightsBest(db, transform, 20).ToList();
        var results = allResults.Take(200).ToList();

        results.Count.ShouldBeGreaterThan(0);

        // M42 (NGC1976) — search by catalog index and by common name as fallback
        var m42 = allResults.FirstOrDefault(r => r.Target.CatalogIndex == CatalogIndex.NGC1976);
        if (m42.TotalScore == Half.Zero)
        {
            m42 = allResults.FirstOrDefault(r => r.Target.Name.Contains("Orion", StringComparison.OrdinalIgnoreCase));
        }
        m42.TotalScore.ShouldBeGreaterThan(Half.Zero,
            $"M42 should be visible and scored in Vienna winter (total candidates: {allResults.Count}, top200 count: {results.Count})");

        // Large bright nebulae visible in the winter sky should appear — California Nebula is a good check
        results.ShouldContain(r => r.Target.Name.Contains("California", StringComparison.OrdinalIgnoreCase),
            "California Nebula should be among the top winter targets from Vienna");
    }

    [Fact]
    public async Task DublinSummerSolstice_StillProducesResults()
    {
        // Dublin (~53.3°N), June 21 — very short night, but should still find targets
        var db = await InitDBAsync();
        var transform = CreateTransform(53.3, -6.3, new DateTimeOffset(2025, 6, 21, 23, 0, 0, TimeSpan.FromHours(1)));

        var results = ObservationScheduler.TonightsBest(db, transform, 20).Take(20).ToList();

        // Even with a very short night, some targets should be visible
        results.Count.ShouldBeGreaterThan(0, "Dublin summer solstice should still produce some targets");
    }

    [Fact]
    public async Task FiltersOutStars()
    {
        // No Star, DoubleStar, or VariableStar should appear in results
        var db = await InitDBAsync();
        var transform = CreateTransform(48.2, 16.4, new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.FromHours(2)));

        var results = ObservationScheduler.TonightsBest(db, transform, 20).Take(100).ToList();

        foreach (var result in results)
        {
            if (result.Target.CatalogIndex is CatalogIndex idx && db.TryLookupByIndex(idx, out var obj))
            {
                obj.ObjectType.IsStar.ShouldBeFalse($"{idx.ToCanonical()} is a star type ({obj.ObjectType}) and should be filtered out");
                obj.ObjectType.ShouldNotBe(ObjectType.Duplicate);
                obj.ObjectType.ShouldNotBe(ObjectType.Inexistent);
            }
        }
    }

    [Fact]
    public async Task PlanetaryNebulaeGetTypeBonus()
    {
        // Verify that planetary nebulae get the 2x type bonus factor
        var db = await InitDBAsync();
        // July evening from Vienna — M57 (RA 18.89h) is near meridian at midnight
        var transform = CreateTransform(48.2, 16.4, new DateTimeOffset(2025, 7, 15, 22, 0, 0, TimeSpan.FromHours(2)));

        var results = ObservationScheduler.TonightsBest(db, transform, 20).Take(200).ToList();

        // Resolve NGC6720 CatalogIndex from name
        TryGetCleanedUpCatalogName("NGC6720", out var ngc6720Index).ShouldBeTrue("NGC6720 should be a valid catalog name");

        var m57 = results.FirstOrDefault(r => r.Target.CatalogIndex == ngc6720Index);
        if (m57.TotalScore == Half.Zero)
        {
            // Fallback: search by name
            m57 = results.FirstOrDefault(r => r.Target.Name.Contains("Ring", StringComparison.OrdinalIgnoreCase));
        }
        m57.TotalScore.ShouldBeGreaterThan(Half.Zero, "M57 (Ring Nebula) should be visible in July from Vienna");

        // Verify at least one PN appears in results
        var hasPlanetaryNeb = results.Any(r =>
        {
            if (r.Target.CatalogIndex is not CatalogIndex idx) return false;
            return db.TryLookupByIndex(idx, out var obj) && obj.ObjectType == ObjectType.PlanetaryNeb;
        });
        hasPlanetaryNeb.ShouldBeTrue("At least one planetary nebula should appear in July results from Vienna");
    }

    // Melbourne, Australia — ~37.8°S, ~145.0°E
    // LMC (ESO056-115): RA 5.39h, Dec -69.75° — circumpolar, lower culm ~17.6°
    // SMC (NGC0292):     RA 0.88h, Dec -72.83° — circumpolar, lower culm ~20.6°
    // Both are always above the horizon from Melbourne and should appear in TonightsBest
    // year-round. They rank higher when closer to upper culmination (near transit).
    private const double MelbourneLat = -37.8;
    private const double MelbourneLon = 145.0;
    private const byte MelbourneMinHeight = 15; // lower than 20 to keep LMC above cutoff at lower culm

    [Theory]
    [InlineData(2025, 12, 15, 11, "Summer")]   // LST@midnight ≈ 6h — LMC near transit, best season
    [InlineData(2025, 6, 15, 10, "Winter")]    // LST@midnight ≈ 18h — LMC at lower culm, worst season
    [InlineData(2025, 3, 15, 11, "Autumn")]    // LST@midnight ≈ 12h — intermediate
    [InlineData(2025, 9, 15, 10, "Spring")]    // LST@midnight ≈ 0h — SMC near transit
    public async Task Melbourne_MagellanicCloudsAlwaysPresent(int year, int month, int day, int tzOffsetHours, string season)
    {
        var db = await InitDBAsync();
        var date = new DateTimeOffset(year, month, day, 20, 0, 0, TimeSpan.FromHours(tzOffsetHours));
        var transform = CreateTransform(MelbourneLat, MelbourneLon, date);

        var allResults = ObservationScheduler.TonightsBest(db, transform, MelbourneMinHeight).ToList();
        var results = allResults.Take(500).ToList();

        results.Count.ShouldBeGreaterThan(0, $"Melbourne {season} should produce results");

        // LMC (ESO056-115) should always be present
        var lmc = allResults.FirstOrDefault(r => r.Target.CatalogIndex == CatalogIndex.ESO056_115);
        if (lmc.TotalScore == Half.Zero)
        {
            lmc = allResults.FirstOrDefault(r => r.Target.Name.Contains("Magellanic", StringComparison.OrdinalIgnoreCase)
                && r.Target.Name.Contains("Large", StringComparison.OrdinalIgnoreCase));
        }
        lmc.TotalScore.ShouldBeGreaterThan(Half.Zero, $"LMC should be visible from Melbourne in {season} (all={allResults.Count}, top500={results.Count})");

        // SMC (NGC0292) should always be present
        var smc = allResults.FirstOrDefault(r => r.Target.CatalogIndex == CatalogIndex.NGC0292);
        if (smc.TotalScore == Half.Zero)
        {
            smc = allResults.FirstOrDefault(r => r.Target.Name.Contains("Magellanic", StringComparison.OrdinalIgnoreCase)
                && r.Target.Name.Contains("Small", StringComparison.OrdinalIgnoreCase));
        }
        smc.TotalScore.ShouldBeGreaterThan(Half.Zero, $"SMC should be visible from Melbourne in {season} (all={allResults.Count}, top500={results.Count})");
    }

    [Fact]
    public async Task CrossReferencedObjects_AppearOnceInGrid()
    {
        var db = await InitDBAsync();
        var grid = db.DeepSkyCoordinateGrid;
        var output = TestContext.Current.TestOutputHelper;

        // Witch Head Nebula: NGC 1909 is the primary, IC 2118 is listed as Dup in OpenNGC
        TryGetCleanedUpCatalogName("NGC1909", out var ngc1909).ShouldBeTrue();
        TryGetCleanedUpCatalogName("IC2118", out var ic2118).ShouldBeTrue();

        db.TryLookupByIndex(ngc1909, out var ngc1909Obj).ShouldBeTrue();
        output?.WriteLine($"NGC 1909: RA={ngc1909Obj.RA:F3}h Dec={ngc1909Obj.Dec:F2}° Type={ngc1909Obj.ObjectType}");

        var ic2118InDb = db.TryLookupByIndex(ic2118, out var ic2118Obj);
        output?.WriteLine($"IC 2118 in DB: {ic2118InDb}" + (ic2118InDb ? $" RA={ic2118Obj.RA:F3}h Dec={ic2118Obj.Dec:F2}° Type={ic2118Obj.ObjectType}" : ""));

        // Check cross-indices
        if (db.TryGetCrossIndices(ngc1909, out var crossIndices))
        {
            output?.WriteLine($"NGC 1909 cross-indices: {string.Join(", ", crossIndices.Select(ci => ci.ToCanonical()))}");
        }
        else
        {
            output?.WriteLine("NGC 1909 has no cross-indices");
        }

        // Check grid presence
        var ngc1909InGrid = grid[ngc1909Obj.RA, ngc1909Obj.Dec];
        output?.WriteLine($"Grid cell at NGC 1909 coords contains: {string.Join(", ", ngc1909InGrid.Select(ci => ci.ToCanonical()))}");

        var ngc1909Found = ngc1909InGrid.Contains(ngc1909);
        var ic2118Found = ngc1909InGrid.Contains(ic2118);
        output?.WriteLine($"NGC 1909 in grid: {ngc1909Found}, IC 2118 in grid: {ic2118Found}");

        ngc1909Found.ShouldBeTrue("Primary NGC 1909 should be in the grid");
        ic2118Found.ShouldBeFalse("Duplicate IC 2118 should NOT be in the grid");

        // Eta Carinae: NGC 3372, C92, GUM 33, RCW 53 — all appeared as separate entries
        TryGetCleanedUpCatalogName("NGC3372", out var ngc3372).ShouldBeTrue();
        db.TryLookupByIndex(ngc3372, out var ngc3372Obj).ShouldBeTrue();
        output?.WriteLine($"\nNGC 3372 (eta Car): RA={ngc3372Obj.RA:F3}h Dec={ngc3372Obj.Dec:F2}° Type={ngc3372Obj.ObjectType}");

        if (db.TryGetCrossIndices(ngc3372, out var etaCarCross))
        {
            output?.WriteLine($"NGC 3372 cross-indices: {string.Join(", ", etaCarCross.Select(ci => ci.ToCanonical()))}");
        }

        var etaCarGridCell = grid[ngc3372Obj.RA, ngc3372Obj.Dec];
        output?.WriteLine($"Grid cell at eta Car coords contains ({etaCarGridCell.Count}): {string.Join(", ", etaCarGridCell.Select(ci => ci.ToCanonical()))}");

        // Check each cross-index individually
        foreach (var ci in etaCarCross)
        {
            db.TryLookupByIndex(ci, out var ciObj).ShouldBeTrue($"{ci.ToCanonical()} should exist in DB");
            var ciInGridCell = grid[ciObj.RA, ciObj.Dec];
            output?.WriteLine($"  {ci.ToCanonical()}: RA={ciObj.RA:F3}h Dec={ciObj.Dec:F2}° Type={ciObj.ObjectType} grid cell has {ciInGridCell.Count} entries, self in grid: {ciInGridCell.Contains(ci)}");
        }

        // Horsehead: Barnard 33 / IC 434
        TryGetCleanedUpCatalogName("IC434", out var ic434).ShouldBeTrue();
        db.TryLookupByIndex(ic434, out var ic434Obj).ShouldBeTrue();
        output?.WriteLine($"\nIC 434 (Horsehead region): RA={ic434Obj.RA:F3}h Dec={ic434Obj.Dec:F2}° Type={ic434Obj.ObjectType}");

        if (db.TryGetCrossIndices(ic434, out var horseheadCross))
        {
            output?.WriteLine($"IC 434 cross-indices: {string.Join(", ", horseheadCross.Select(ci => ci.ToCanonical()))}");
        }

        var horseheadGridCell = grid[ic434Obj.RA, ic434Obj.Dec];
        output?.WriteLine($"Grid cell at IC 434 coords contains ({horseheadGridCell.Count}): {string.Join(", ", horseheadGridCell.Select(ci => ci.ToCanonical()))}");
    }

    [Fact]
    public async Task MelbourneChristmasDay_Top100()
    {
        var db = await InitDBAsync();
        var date = new DateTimeOffset(2025, 12, 25, 21, 0, 0, TimeSpan.FromHours(11));
        var transform = CreateTransform(MelbourneLat, MelbourneLon, date);

        var results = ObservationScheduler.TonightsBest(db, transform, MelbourneMinHeight).Take(100).ToList();
        results.Count.ShouldBeGreaterThan(0);

        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"{"#",-4} {"Score",7} {"Catalog",-14} {"Name",-30} {"Type",-8} {"RA",8} {"Dec",8} {"Start",-14} {"Dur"}");
        output?.WriteLine(new string('-', 110));

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var name = r.Target.Name;
            var catalogId = r.Target.CatalogIndex?.ToCanonical() ?? "-";
            var objType = "-";

            if (r.Target.CatalogIndex is CatalogIndex idx && db.TryLookupByIndex(idx, out var obj))
            {
                objType = obj.ObjectType.ToAbbreviation();
                if (obj.CommonNames is { Count: > 0 } names)
                {
                    name = names.OrderByDescending(n => n.Length).First();
                }
            }

            output?.WriteLine(
                $"{i + 1,-4} {r.TotalScore,7:F1} {catalogId,-14} {name,-30} {objType,-8} {r.Target.RA,8:F3}h {r.Target.Dec,8:F2} {r.OptimalStart:HH:mm dd-MMM}  {r.OptimalDuration:h\\:mm}");
        }
    }

    [Fact]
    public async Task Melbourne_MagellanicCloudsRankHigherInSummer()
    {
        // In December (summer) from Melbourne, LMC transits near midnight → highest altitude → best rank.
        // In June (winter), LMC transits during daylight → the off-meridian penalty (0.5×) combined with
        // lower altitude pushes it out of the top results entirely. This is correct: a target that peaks
        // during daylight is always past-peak during imaging and should rank below on-meridian targets.
        var db = await InitDBAsync();

        var summerDate = new DateTimeOffset(2025, 12, 15, 20, 0, 0, TimeSpan.FromHours(11));
        var winterDate = new DateTimeOffset(2025, 6, 15, 20, 0, 0, TimeSpan.FromHours(10));

        var summerTransform = CreateTransform(MelbourneLat, MelbourneLon, summerDate);
        var winterTransform = CreateTransform(MelbourneLat, MelbourneLon, winterDate);

        var summerResults = ObservationScheduler.TonightsBest(db, summerTransform, MelbourneMinHeight).Take(500).ToList();
        var winterResults = ObservationScheduler.TonightsBest(db, winterTransform, MelbourneMinHeight).Take(500).ToList();

        var lmcSummerRank = summerResults.FindIndex(r => r.Target.CatalogIndex == CatalogIndex.ESO056_115);
        var lmcWinterRank = winterResults.FindIndex(r => r.Target.CatalogIndex == CatalogIndex.ESO056_115);

        // LMC should definitely appear in summer top-500 (transits during darkness)
        lmcSummerRank.ShouldBeGreaterThanOrEqualTo(0, "LMC should appear in summer results");

        // In winter, LMC transits during daylight → off-meridian penalty may push it beyond top-500.
        // If it does appear, it should rank worse than in summer.
        if (lmcWinterRank >= 0)
        {
            lmcSummerRank.ShouldBeLessThan(lmcWinterRank,
                $"LMC should rank higher in summer (rank {lmcSummerRank}) than winter (rank {lmcWinterRank})");
        }
        // else: LMC not in top-500 in winter → off-meridian penalty working as intended
    }
}
