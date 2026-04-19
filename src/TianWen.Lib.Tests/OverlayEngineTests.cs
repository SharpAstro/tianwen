using System;
using System.Collections.Generic;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class OverlayEngineTests
{
    // --- IsExtendedObjectType ---

    [Theory]
    [InlineData(ObjectType.Galaxy, true)]
    [InlineData(ObjectType.PairG, true)]
    [InlineData(ObjectType.GroupG, true)]
    [InlineData(ObjectType.OpenCluster, true)]
    [InlineData(ObjectType.GlobCluster, true)]
    [InlineData(ObjectType.PlanetaryNeb, true)]
    [InlineData(ObjectType.DarkNeb, true)]
    [InlineData(ObjectType.SNRemnant, true)]
    [InlineData(ObjectType.Association, true)]
    [InlineData(ObjectType.Star, false)]
    [InlineData(ObjectType.DoubleStar, false)]
    [InlineData(ObjectType.Mira, false)]
    public void IsExtendedObjectType_ClassifiesCorrectly(ObjectType ot, bool expected)
    {
        OverlayEngine.IsExtendedObjectType(ot).ShouldBe(expected);
    }

    // --- IsStarType ---

    [Theory]
    [InlineData(ObjectType.Star, true)]
    [InlineData(ObjectType.DoubleStar, true)]
    [InlineData(ObjectType.Mira, true)]
    [InlineData(ObjectType.Nova, true)]
    [InlineData(ObjectType.RRLyrae, true)]
    [InlineData(ObjectType.WolfRayetStar, true)]
    [InlineData(ObjectType.Galaxy, false)]
    [InlineData(ObjectType.OpenCluster, false)]
    [InlineData(ObjectType.PlanetaryNeb, false)]
    public void IsStarType_ClassifiesCorrectly(ObjectType ot, bool expected)
    {
        OverlayEngine.IsStarType(ot).ShouldBe(expected);
    }

    // --- GetNamePriority ---

    [Theory]
    [InlineData("Sirius", 1)]         // IAU proper name
    [InlineData("Whirlpool Galaxy", 1)] // Named DSO
    [InlineData("eta Ori", 2)]         // Bayer designation
    [InlineData("alf CMa", 2)]         // Bayer designation
    [InlineData("28 Ori", 3)]          // Flamsteed number
    [InlineData("7 CMa", 3)]           // Flamsteed number
    [InlineData("", 100)]              // Empty
    public void GetNamePriority_ReturnsExpectedScore(string name, int expected)
    {
        OverlayEngine.GetNamePriority(name).ShouldBe(expected);
    }

    [Fact]
    public void GetNamePriority_IAU_BeforesBayer_BeforeFlamsteed()
    {
        var iauPriority = OverlayEngine.GetNamePriority("Betelgeuse");
        var bayerPriority = OverlayEngine.GetNamePriority("alf Ori");
        var flamsteedPriority = OverlayEngine.GetNamePriority("58 Ori");

        iauPriority.ShouldBeLessThan(bayerPriority);
        bayerPriority.ShouldBeLessThan(flamsteedPriority);
    }

    // --- GetOverlayColor ---

    [Theory]
    [InlineData(ObjectType.Galaxy)]
    [InlineData(ObjectType.PairG)]
    [InlineData(ObjectType.GroupG)]
    public void GetOverlayColor_Galaxies_AreCyan(ObjectType ot)
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ot);
        r.ShouldBe(0.0f);
        g.ShouldBe(0.8f);
        b.ShouldBe(0.8f);
    }

    [Theory]
    [InlineData(ObjectType.OpenCluster)]
    [InlineData(ObjectType.GlobCluster)]
    [InlineData(ObjectType.Association)]
    public void GetOverlayColor_Clusters_AreYellow(ObjectType ot)
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ot);
        r.ShouldBe(1.0f);
        g.ShouldBe(0.8f);
        b.ShouldBe(0.0f);
    }

    [Theory]
    [InlineData(ObjectType.Star)]
    [InlineData(ObjectType.DoubleStar)]
    [InlineData(ObjectType.Mira)]
    public void GetOverlayColor_Stars_AreWhite(ObjectType ot)
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ot);
        r.ShouldBe(1.0f);
        g.ShouldBe(1.0f);
        b.ShouldBe(1.0f);
    }

    [Fact]
    public void GetOverlayColor_PlanetaryNeb_IsPurple()
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ObjectType.PlanetaryNeb);
        r.ShouldBe(0.6f);
        g.ShouldBe(0.3f);
        b.ShouldBe(1.0f);
    }

    [Fact]
    public void GetOverlayColor_DarkNeb_IsGray()
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ObjectType.DarkNeb);
        r.ShouldBe(0.6f);
        g.ShouldBe(0.6f);
        b.ShouldBe(0.6f);
    }

    // --- GetExtendedMagCutoff ---

    [Theory]
    [InlineData(400.0, 8.0)]    // > 5 degrees FOV
    [InlineData(301.0, 8.0)]    // Just above 300
    [InlineData(100.0, 12.0)]   // 1-5 degrees
    [InlineData(61.0, 12.0)]    // Just above 60
    [InlineData(30.0, 20.0)]    // < 1 degree
    [InlineData(10.0, 20.0)]    // Narrow field
    public void GetExtendedMagCutoff_ReturnsCorrectCutoff(double fovArcmin, double expected)
    {
        OverlayEngine.GetExtendedMagCutoff(fovArcmin).ShouldBe(expected);
    }

    // --- GetStarMagCutoff ---

    [Theory]
    [InlineData(400.0, 1.0)]    // > 5 degrees
    [InlineData(200.0, 2.5)]    // 2-5 degrees
    [InlineData(90.0, 4.0)]     // 1-2 degrees
    [InlineData(45.0, 5.5)]     // 0.5-1 degrees
    [InlineData(10.0, 7.0)]     // < 0.5 degrees
    public void GetStarMagCutoff_ReturnsCorrectCutoff(double fovArcmin, double expected)
    {
        OverlayEngine.GetStarMagCutoff(fovArcmin).ShouldBe(expected);
    }

    // --- BuildOverlayLabel ---

    private static CelestialObject MakeObject(
        CatalogIndex index,
        ObjectType objectType = ObjectType.Galaxy,
        IReadOnlySet<string>? commonNames = null)
    {
        return new CelestialObject(
            Index: index,
            ObjectType: objectType,
            RA: 5.0,
            Dec: -2.0,
            Constellation: Constellation.Orion,
            V_Mag: (Half)8.0,
            SurfaceBrightness: Half.NaN,
            BMinusV: Half.NaN,
            CommonNames: commonNames ?? new HashSet<string>()
        );
    }

    [Fact]
    public void BuildOverlayLabel_ZoomedOut_ShowsBestNameOnly()
    {
        var obj = MakeObject(CatalogIndex.NGC1976, commonNames: new HashSet<string> { "Orion Nebula", "42 Ori" });
        var db = new FakeDB(obj);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.NGC1976, db, zoom: 0.3f);

        lines.Count.ShouldBe(1);
        lines[0].ShouldBe("Orion Nebula"); // IAU name (priority 1) beats Flamsteed (priority 3)
    }

    [Fact]
    public void BuildOverlayLabel_ZoomedOut_NoName_ShowsCanonical()
    {
        var obj = MakeObject(CatalogIndex.NGC1976);
        var db = new FakeDB(obj);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.NGC1976, db, zoom: 0.3f);

        lines.Count.ShouldBe(1);
        lines[0].ShouldBe(CatalogIndex.NGC1976.ToCanonical());
    }

    [Fact]
    public void BuildOverlayLabel_MediumZoom_ShowsNameAndCanonical()
    {
        var obj = MakeObject(CatalogIndex.NGC1976, commonNames: new HashSet<string> { "Orion Nebula" });
        var db = new FakeDB(obj);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.NGC1976, db, zoom: 0.7f);

        lines.Count.ShouldBe(2);
        lines[0].ShouldBe("Orion Nebula");
        lines[1].ShouldBe(CatalogIndex.NGC1976.ToCanonical());
    }

    [Fact]
    public void BuildOverlayLabel_MediumZoom_NoName_ShowsCanonicalOnce()
    {
        var obj = MakeObject(CatalogIndex.NGC1976);
        var db = new FakeDB(obj);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.NGC1976, db, zoom: 0.7f);

        lines.Count.ShouldBe(1);
        lines[0].ShouldBe(CatalogIndex.NGC1976.ToCanonical());
    }

    [Fact]
    public void BuildOverlayLabel_FullZoom_ShowsAllNamesAndCrossIndices()
    {
        var names = new HashSet<string> { "Orion Nebula", "42 Ori" };
        var obj = MakeObject(CatalogIndex.NGC1976, commonNames: names);
        var crossIndices = new HashSet<CatalogIndex> { CatalogIndex.M042 };
        var db = new FakeDB(obj, crossIndices);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.NGC1976, db, zoom: 1.5f);

        // Should contain: "Orion Nebula" (best), "42 Ori" (second), canonical, cross-index
        lines.Count.ShouldBeGreaterThanOrEqualTo(3);
        lines[0].ShouldBe("Orion Nebula"); // IAU name first
        lines.ShouldContain("42 Ori");
        lines.ShouldContain(CatalogIndex.M042.ToCanonical());
    }

    [Fact]
    public void BuildOverlayLabel_FullZoom_SortsNamesByPriority()
    {
        // Bayer should come before Flamsteed
        var names = new HashSet<string> { "28 Ori", "eta Ori" };
        var obj = MakeObject(CatalogIndex.HIP025281, ObjectType.Star, names);
        var db = new FakeDB(obj);

        var lines = OverlayEngine.BuildOverlayLabel(obj, CatalogIndex.HIP025281, db, zoom: 1.5f);

        lines[0].ShouldBe("eta Ori"); // Bayer (priority 2) before Flamsteed (priority 3)
        lines[1].ShouldBe("28 Ori");
    }

    // --- ComputeScreenPA ---

    [Fact]
    public void ComputeScreenPA_NaN_ReturnsZero()
    {
        var wcs = MakeSimpleWCS();
        OverlayEngine.ComputeScreenPA(wcs, 5.0, -2.0, Half.NaN).ShouldBe(0f);
    }

    [Fact]
    public void ComputeScreenPA_Zero_ReturnsFiniteAngle()
    {
        var wcs = MakeSimpleWCS();
        var pa = OverlayEngine.ComputeScreenPA(wcs, 5.0, -2.0, (Half)0.0);
        float.IsFinite(pa).ShouldBeTrue();
    }

    [Fact]
    public void ComputeScreenPA_90_ReturnsFiniteAngle()
    {
        var wcs = MakeSimpleWCS();
        var pa = OverlayEngine.ComputeScreenPA(wcs, 5.0, -2.0, (Half)90.0);
        float.IsFinite(pa).ShouldBeTrue();
    }

    // --- GetOverlayColor edge cases ---

    [Fact]
    public void GetOverlayColor_EmissionObject_IsOrange()
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ObjectType.EmObj);
        r.ShouldBe(1.0f);
        g.ShouldBe(0.4f);
        b.ShouldBe(0.25f);
    }

    [Fact]
    public void GetOverlayColor_HIIRegion_IsOrange()
    {
        var (r, g, b) = OverlayEngine.GetOverlayColor(ObjectType.HIIReg);
        r.ShouldBe(1.0f);
        g.ShouldBe(0.4f);
        b.ShouldBe(0.25f);
    }

    // --- GetNamePriority edge cases ---

    [Fact]
    public void GetNamePriority_SpecialChar_ReturnsFallback()
    {
        // Names starting with non-letter non-digit characters get fallback score
        OverlayEngine.GetNamePriority("*foo").ShouldBe(50);
        OverlayEngine.GetNamePriority("+bar").ShouldBe(50);
    }

    // --- ComputeOverlays integration ---

    [Fact]
    public void ComputeOverlays_EmptyImage_ReturnsEmpty()
    {
        var layout = new ViewportLayout(1920, 1080, 0, 0, 1.0f, (0, 0), 0, 40, 1920, 1000, 1.0f);
        var wcs = MakeSimpleWCS();
        var db = new FakeDB();

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeOverlays_GalaxyInView_ReturnsEllipseMarker()
    {
        var wcs = MakeSimpleWCS();
        var obj = MakeObject(CatalogIndex.NGC1976, commonNames: new HashSet<string> { "Orion Nebula" });
        var shape = new CelestialObjectShape((Half)7.0, (Half)5.0, (Half)45.0);
        var db = new FakeDB(obj, shape: shape, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBeGreaterThanOrEqualTo(1);
        items[0].Marker.Kind.ShouldBe(OverlayMarkerKind.Ellipse);
        items[0].LabelLines.Count.ShouldBeGreaterThanOrEqualTo(1);
        items[0].Color.ShouldBe((0.0f, 0.8f, 0.8f)); // cyan for galaxies
    }

    [Fact]
    public void ComputeOverlays_StarInView_ReturnsCrossMarker()
    {
        var wcs = MakeSimpleWCS();
        var names = new HashSet<string> { "Betelgeuse" };
        var obj = new CelestialObject(
            CatalogIndex.HIP025281, ObjectType.Star, 5.0, -2.0,
            Constellation.Orion, (Half)0.5, Half.NaN, (Half)0.65, names);
        var db = new FakeDB(obj, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBeGreaterThanOrEqualTo(1);
        items[0].Marker.Kind.ShouldBe(OverlayMarkerKind.Cross);
        items[0].Color.ShouldBe((1.0f, 1.0f, 1.0f)); // white for stars
    }

    [Fact]
    public void ComputeOverlays_ExtendedWithoutShape_ReturnsCircleMarker()
    {
        var wcs = MakeSimpleWCS();
        var obj = MakeObject(CatalogIndex.NGC1976);
        // No shape data
        var db = new FakeDB(obj, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBeGreaterThanOrEqualTo(1);
        items[0].Marker.Kind.ShouldBe(OverlayMarkerKind.Circle);
    }

    [Fact]
    public void ComputeOverlays_FaintStar_FilteredByMagnitudeCutoff()
    {
        // Wide FOV (starMagCutoff ~1.0), star at mag 5 should be filtered out
        var wcs = MakeSimpleWCS();
        var obj = new CelestialObject(
            CatalogIndex.HIP025281, ObjectType.Star, 5.0, -2.0,
            Constellation.Orion, (Half)5.0, Half.NaN, (Half)0.65, new HashSet<string>());
        var db = new FakeDB(obj, gridRA: 5.0, gridDec: -2.0);

        // Very small zoom = very wide FOV → star mag cutoff is ~1.0
        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.01f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeOverlays_TinyEllipse_IsSkipped()
    {
        var wcs = MakeSimpleWCS();
        var obj = MakeObject(CatalogIndex.NGC1976);
        // Very tiny shape — will be < 3px at normal zoom
        var shape = new CelestialObjectShape((Half)0.01, (Half)0.005, (Half)0.0);
        var db = new FakeDB(obj, shape: shape, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        // Object should be skipped because its ellipse is < 3px
        items.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeOverlays_CrossIndexDuplicate_IsDeduped()
    {
        var wcs = MakeSimpleWCS();

        // Two catalog entries for the same star, cross-referenced
        var obj1 = new CelestialObject(
            CatalogIndex.HIP025281, ObjectType.Star, 5.0, -2.0,
            Constellation.Orion, (Half)2.0, Half.NaN, (Half)0.65, new HashSet<string> { "eta Ori" });
        var obj2 = new CelestialObject(
            CatalogIndex.HD035411, ObjectType.Star, 5.0, -2.0,
            Constellation.Orion, (Half)2.0, Half.NaN, (Half)0.65, new HashSet<string> { "eta Ori" });

        var crossIndices = new HashSet<CatalogIndex> { CatalogIndex.HIP025281, CatalogIndex.HD035411 };
        var db = new FakeDB();
        db.AddObject(obj1, crossIndices: crossIndices, gridRA: 5.0, gridDec: -2.0);
        db.AddObject(obj2, crossIndices: crossIndices, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        // Should only get one item despite two catalog entries
        items.Count.ShouldBe(1);
    }

    [Fact]
    public void ComputeOverlays_MultipleObjects_SortedByMagnitude()
    {
        var wcs = MakeSimpleWCS();

        // Faint object
        var faint = new CelestialObject(
            CatalogIndex.NGC1976, ObjectType.Galaxy, 5.0, -2.0,
            Constellation.Orion, (Half)12.0, Half.NaN, Half.NaN, new HashSet<string>());
        // Bright object
        var bright = new CelestialObject(
            CatalogIndex.M042, ObjectType.Galaxy, 5.001, -2.001,
            Constellation.Orion, (Half)4.0, Half.NaN, Half.NaN, new HashSet<string> { "Orion Nebula" });

        var db = new FakeDB();
        db.AddObject(faint, gridRA: 5.0, gridDec: -2.0);
        db.AddObject(bright, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBe(2);
        // Brightest first
        items[0].LabelLines.ShouldContain("Orion Nebula");
    }

    [Fact]
    public void ComputeOverlays_BrightNamedObject_HasHighLabelPriority()
    {
        var wcs = MakeSimpleWCS();
        var obj = new CelestialObject(
            CatalogIndex.HIP025281, ObjectType.Star, 5.0, -2.0,
            Constellation.Orion, (Half)0.5, Half.NaN, (Half)0.65, new HashSet<string> { "Betelgeuse" });
        var db = new FakeDB(obj, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBe(1);
        // Priority = 6 (named) + max(0, 15 - 0.5) = 20.5 → definitely > 15
        items[0].LabelPriority.ShouldBeGreaterThan(15f);
    }

    [Fact]
    public void ComputeOverlays_FaintUnnamedObject_HasLowerPriority()
    {
        var wcs = MakeSimpleWCS();
        var obj = MakeObject(CatalogIndex.NGC1976); // V_Mag = 8.0, no common name in test helper
        var db = new FakeDB(obj, gridRA: 5.0, gridDec: -2.0);

        var layout = new ViewportLayout(1920, 1080, 1000, 1000, 0.5f, (0, 0), 0, 40, 1920, 1000, 1.0f);

        var items = OverlayEngine.ComputeOverlays(layout, wcs, db, (_, _) => 50f, 18f);

        items.Count.ShouldBe(1);
        // Priority <= 15 (no name bonus, mag 8 → ~7 brightness contribution + size)
        items[0].LabelPriority.ShouldBeLessThan(15f);
    }

    // --- Helpers ---

    private static WCS MakeSimpleWCS()
    {
        // Simple WCS centered at RA=5h, Dec=-2° with ~6"/px scale
        return new WCS(5.0, -2.0)
        {
            CRPix1 = 500.5,
            CRPix2 = 500.5,
            CD1_1 = -0.001658,  // ~6"/px
            CD1_2 = 0.0,
            CD2_1 = 0.0,
            CD2_2 = 0.001658,
        };
    }

    /// <summary>
    /// Minimal fake DB for testing overlay logic without loading the full catalog.
    /// </summary>
    private sealed class FakeDB : ICelestialObjectDB
    {
        private readonly Dictionary<CatalogIndex, CelestialObject> _objects = new Dictionary<CatalogIndex, CelestialObject>();
        private readonly Dictionary<CatalogIndex, IReadOnlySet<CatalogIndex>> _crossIndices = new Dictionary<CatalogIndex, IReadOnlySet<CatalogIndex>>();
        private readonly Dictionary<CatalogIndex, CelestialObjectShape> _shapes = new Dictionary<CatalogIndex, CelestialObjectShape>();
        private readonly FakeGrid _grid;

        public FakeDB()
        {
            _grid = new FakeGrid();
        }

        public FakeDB(CelestialObject obj, IReadOnlySet<CatalogIndex>? crossIndices = null,
            CelestialObjectShape? shape = null, double gridRA = 0, double gridDec = 0)
        {
            _grid = new FakeGrid();
            AddObject(obj, crossIndices, shape, gridRA, gridDec);
        }

        public void AddObject(CelestialObject obj, IReadOnlySet<CatalogIndex>? crossIndices = null,
            CelestialObjectShape? shape = null, double gridRA = 0, double gridDec = 0)
        {
            _objects[obj.Index] = obj;
            if (crossIndices is not null)
            {
                _crossIndices[obj.Index] = crossIndices;
            }
            if (shape is { } s)
            {
                _shapes[obj.Index] = s;
            }
            _grid.Add(obj.Index, gridRA, gridDec);
        }

        public IReadOnlySet<CatalogIndex> AllObjectIndices => new HashSet<CatalogIndex>(_objects.Keys);
        public IReadOnlySet<Catalog> Catalogs => new HashSet<Catalog>();
        public IReadOnlyCollection<string> CommonNames => Array.Empty<string>();
        public IRaDecIndex CoordinateGrid => _grid;
        public IRaDecIndex DeepSkyCoordinateGrid => _grid;

        public System.Threading.Tasks.Task<(int Processed, int Failed)> InitDBAsync(
            System.Threading.CancellationToken cancellationToken)
        {
            return System.Threading.Tasks.Task.FromResult((0, 0));
        }

        public bool TryGetCrossIndices(CatalogIndex catalogIndex, out IReadOnlySet<CatalogIndex> crossIndices)
        {
            return _crossIndices.TryGetValue(catalogIndex, out crossIndices!);
        }

        public bool TryGetShape(CatalogIndex index, out CelestialObjectShape shape)
        {
            return _shapes.TryGetValue(index, out shape);
        }

        public int HipStarCount => 0;

        public bool TryLookupHIP(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
        {
            ra = 0;
            dec = 0;
            vMag = float.NaN;
            bv = float.NaN;
            return false;
        }

        public int Tycho2StarCount => 0;

        public int CopyTycho2Stars(Span<Tycho2StarLite> destination, int startIndex = 0) => 0;

        public bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject)
        {
            return _objects.TryGetValue(index, out celestialObject);
        }

        public bool TryResolveCommonName(string name, out IReadOnlyList<CatalogIndex> matches)
        {
            matches = Array.Empty<CatalogIndex>();
            return false;
        }

        private sealed class FakeGrid : IRaDecIndex
        {
            private readonly List<(CatalogIndex Index, double RA, double Dec)> _entries = new List<(CatalogIndex, double, double)>();

            public FakeGrid() { }

            public void Add(CatalogIndex index, double ra, double dec)
            {
                _entries.Add((index, ra, dec));
            }

            public IReadOnlyCollection<CatalogIndex> this[double raHours, double decDeg]
            {
                get
                {
                    var result = new List<CatalogIndex>();
                    foreach (var (idx, ra, dec) in _entries)
                    {
                        if (Math.Abs(Math.Floor(raHours * 15.0) / 15.0 - Math.Floor(ra * 15.0) / 15.0) < 0.1 &&
                            Math.Abs(Math.Floor(decDeg) - Math.Floor(dec)) < 1.1)
                        {
                            result.Add(idx);
                        }
                    }
                    return result;
                }
            }
        }
    }
}
