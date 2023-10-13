using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class CelestialObjectDBTests
{
    private static ICelestialObjectDB? _cachedDB;
    private static readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
    private static int _processed = 0;
    private static int _failed = 0;

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
            (_processed, _failed) = await (_cachedDB = new CelestialObjectDB()).InitDBAsync();

            _processed.ShouldBeGreaterThan(13000);
            _failed.ShouldBe(0);

            return _cachedDB;
        }
        finally
        {
            _sem.Release();
        }
    }

    [Theory]
    [InlineData("C041", ObjectType.OpenCluster, CatalogIndex.C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("C099", ObjectType.DarkNeb, CatalogIndex.C099, Constellation.Crux, 12.521944444444445d, -63.74333333333333d)]
    [InlineData("C30", ObjectType.Galaxy, CatalogIndex.NGC7331, Constellation.Pegasus, 22.617780555555555d, 34.415527777777775d)]
    [InlineData("CG4", ObjectType.DarkNeb, CatalogIndex.CG0004, Constellation.Puppis, 7.5691999999999995d, -46.905d)]
    [InlineData("Cr050", ObjectType.OpenCluster, CatalogIndex.C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("ESO056-115", ObjectType.Galaxy, CatalogIndex.ESO056_115, Constellation.Dorado, 5.392916666666667d, -69.75611111111111d)]
    [InlineData("IC0048", ObjectType.Galaxy, CatalogIndex.IC0048, Constellation.Cetus, 0.7262416666666667d, -8.1865d)]
    [InlineData("IC0381", ObjectType.Galaxy, CatalogIndex.NGC1530_A, Constellation.Camelopardalis, 4.74125d, 75.63975d)]
    [InlineData("IC0458", ObjectType.Galaxy, CatalogIndex.IC0458, Constellation.Lynx, 7.176152777777778d, 50.118916666666664d)]
    [InlineData("IC0715NW", ObjectType.Galaxy, CatalogIndex.IC0715NW, Constellation.Crater, 11.615058333333334d, -8.375805555555557d)]
    [InlineData("IC0843", ObjectType.Galaxy, CatalogIndex.NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("IC1000", ObjectType.Galaxy, CatalogIndex.IC1000, Constellation.Bootes, 14.327863888888889d, 17.854694444444444d)]
    [InlineData("IC1577", ObjectType.Galaxy, CatalogIndex.IC0048, Constellation.Cetus, 0.7262416666666667d, -8.1865d)]
    [InlineData("IC4088", ObjectType.Galaxy, CatalogIndex.NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("M102", ObjectType.Galaxy, CatalogIndex.NGC5457, Constellation.UrsaMajor, 14.053483333333334d, 54.34894444444445d)]
    [InlineData("M13", ObjectType.GlobCluster, CatalogIndex.NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("M40", ObjectType.DoubleStar, CatalogIndex.M040, Constellation.UrsaMajor, 12.37113888888889d, 58.08444444444444d)]
    [InlineData("M45", ObjectType.OpenCluster, CatalogIndex.Mel022, Constellation.Taurus, 3.7912777777777777d, 24.10527777777778d)]
    [InlineData("Mel025", ObjectType.OpenCluster, CatalogIndex.C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("NGC0056", ObjectType.Unknown, CatalogIndex.NGC0056, Constellation.Pisces, 0.2557388888888889d, 12.444527777777777d)]
    [InlineData("NGC4913", ObjectType.Galaxy, CatalogIndex.NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("NGC5457", ObjectType.Galaxy, CatalogIndex.NGC5457, Constellation.UrsaMajor, 14.053483333333334d, 54.34894444444445d)]
    [InlineData("NGC6205", ObjectType.GlobCluster, CatalogIndex.NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("NGC7293", ObjectType.PlanetaryNeb, CatalogIndex.NGC7293, Constellation.Aquarius, 22.494047222222225d, -20.837333333333333d)]
    [InlineData("NGC7331", ObjectType.Galaxy, CatalogIndex.NGC7331, Constellation.Pegasus, 22.617780555555555d, 34.415527777777775d)]
    [InlineData("UGC00468", ObjectType.Galaxy, CatalogIndex.IC0049, Constellation.Cetus, 0.7322583333333333d, 1.850277777777778d)]
    [InlineData("NGC3372", ObjectType.HIIReg, CatalogIndex.NGC3372, Constellation.Carina, 10.752369444444444d, -59.86669444444445d)]
    [InlineData("GUM033", ObjectType.HIIReg, CatalogIndex.NGC3372, Constellation.Carina, 10.752369444444444d, -59.86669444444445d)]
    [InlineData("GUM016", ObjectType.HIIReg, CatalogIndex.GUM016, Constellation.Vela, 8.553333333333335d, -44.1d)]
    [InlineData("C009", ObjectType.HIIReg, CatalogIndex.C009, Constellation.Cepheus, 22.965d, 62.51833333333333d)]
    // TODO: VDB has these listed as Be*, but in HIP we only know stars (*)
    [InlineData("vdB0005", ObjectType.Star, CatalogIndex.HIP004427, Constellation.Cassiopeia, 0.9451477026666667d, 60.71674038d)]
    [InlineData("vdB0020", ObjectType.Star, CatalogIndex.HIP017499, Constellation.Taurus, 3.747927033333333d, 24.11333922d)]
    [InlineData("HIP120404", ObjectType.Star, CatalogIndex.HIP120404, Constellation.Carina, 7.967475347333333d, -60.61478539d)]
    public async Task GivenObjectIdWhenLookingItUpThenAnObjIsReturned(
        string indexEntry,
        ObjectType expectedObjType,
        CatalogIndex expectedCatalogIindex,
        Constellation expectedConstellation,
        double expectedRaDeg,
        double expectedDecDeg
    )
    {
        // given
        var db = await InitDBAsync();

        // when
        var found = db.TryLookupByIndex(indexEntry, out var celestialObject);

        // then
        found.ShouldBeTrue();
        celestialObject.Index.ShouldBe(expectedCatalogIindex);
        celestialObject.ObjectType.ShouldBe(expectedObjType);
        celestialObject.Constellation.ShouldBe(expectedConstellation);
        celestialObject.RA.ShouldBeInRange(expectedRaDeg - 0.000000000000001d, expectedRaDeg + 0.000000000000001d);
        celestialObject.Dec.ShouldBeInRange(expectedDecDeg - 0.000000000000001d, expectedDecDeg + 0.000000000000001d);
    }

    [Theory]
    [InlineData("Antennae Galaxies", CatalogIndex.NGC4038, CatalogIndex.NGC4039)]
    [InlineData("Eagle Nebula", CatalogIndex.IC4703, CatalogIndex.NGC6611)]
    [InlineData("Hercules Globular Cluster", CatalogIndex.NGC6205)]
    [InlineData("Large Magellanic Cloud", CatalogIndex.ESO056_115)]
    [InlineData("Tarantula Nebula", CatalogIndex.NGC2070)]
    [InlineData("30 Dor Cluster", CatalogIndex.NGC2070)]
    [InlineData("Orion Nebula", CatalogIndex.NGC1976)]
    [InlineData("Great Orion Nebula", CatalogIndex.NGC1976)]
    [InlineData("Hyades", CatalogIndex.C041)]
    [InlineData("Pleiades", CatalogIndex.Mel022)]
    [InlineData("Keyhole", CatalogIndex.NGC3372)]
    [InlineData("Car Nebula", CatalogIndex.NGC3372)]
    [InlineData("Carina Nebula", CatalogIndex.NGC3372)]
    [InlineData("eta Car Nebula", CatalogIndex.NGC3372)]
    [InlineData("Keyhole Nebula", CatalogIndex.NGC3372)]
    [InlineData("Cave Nebula", CatalogIndex.C009, CatalogIndex.Ced0201)]
    [InlineData("Coalsack Nebula", CatalogIndex.C099)]
    [InlineData("tet01 Eri", CatalogIndex.HR0897)]
    [InlineData("Trifid Nebula", CatalogIndex.NGC6514)]
    [InlineData("Ran", CatalogIndex.HIP016537)]
    [InlineData("18 Eri", CatalogIndex.HIP016537)]
    [InlineData("eps Eri", CatalogIndex.HIP016537)]
    [InlineData("Electra", CatalogIndex.HIP017499)]
    public async Task GivenANameWhenLookingItUpThenAnObjIsReturned(string name, params CatalogIndex[] expectedMatches)
    {
        // given
        var db = await InitDBAsync();

        // when
        var found = db.TryResolveCommonName(name, out var matches);

        // then
        found.ShouldBeTrue();
        matches.ShouldNotBeNull();
        matches.ShouldBeEquivalentTo(expectedMatches);
    }

    [Theory]
    [InlineData(CatalogIndex.HR0897, "tet01 Eri")]
    [InlineData(CatalogIndex.HR1084, "18 Eri", "eps Eri", "Ran")]
    [InlineData(CatalogIndex.C099, "Coalsack Nebula")]
    [InlineData(CatalogIndex.M042, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(CatalogIndex.NGC1976, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(CatalogIndex.M051, "Whirlpool Galaxy")]
    [InlineData(CatalogIndex.NGC3372, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    [InlineData(CatalogIndex.GUM033, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    [InlineData(CatalogIndex.NGC6302, "Bug Nebula", "Butterfly Nebula")]
    [InlineData(CatalogIndex.C009, "Cave Nebula")]
    [InlineData(CatalogIndex.Sh2_0155, "Cave Nebula")]
    [InlineData(CatalogIndex.Ced0201, "Cave Nebula")]
    public async Task GivenACatalogIndexWhenTryingToGetCommonNamesThenTheyAreFound(CatalogIndex catalogIndex, params string[] expectedNames)
    {
        // given
        var db = await InitDBAsync();

        var found = db.TryLookupByIndex(catalogIndex, out var match);

        found.ShouldBeTrue();
        match.CommonNames.Count.ShouldBe(expectedNames.Length);
        match.CommonNames.ShouldBe(expectedNames, ignoreOrder: true);
    }

    [Theory]
    [InlineData(CatalogIndex.C099)]
    [InlineData(CatalogIndex.NGC1333, CatalogIndex.DG0018, CatalogIndex.Ced0016)]
    [InlineData(CatalogIndex.DG0018, CatalogIndex.Ced0016, CatalogIndex.NGC1333)]
    [InlineData(CatalogIndex.Ced0016, CatalogIndex.DG0018, CatalogIndex.NGC1333)]
    [InlineData(CatalogIndex.DG0017, CatalogIndex.Ced0014)]
    [InlineData(CatalogIndex.Ced0014, CatalogIndex.DG0017)]
    [InlineData(CatalogIndex.DOBASHI_0222, CatalogIndex.LDN00146)]
    [InlineData(CatalogIndex.M042, CatalogIndex.NGC1976)]
    [InlineData(CatalogIndex.NGC1976, CatalogIndex.M042)]
    [InlineData(CatalogIndex.M051, CatalogIndex.NGC5194, CatalogIndex.UGC08493)]
    [InlineData(CatalogIndex.NGC5194, CatalogIndex.M051, CatalogIndex.UGC08493)]
    [InlineData(CatalogIndex.UGC08493, CatalogIndex.M051, CatalogIndex.NGC5194)]
    [InlineData(CatalogIndex.M054, CatalogIndex.NGC6715)]
    [InlineData(CatalogIndex.M045, CatalogIndex.Mel022)]
    [InlineData(CatalogIndex.Mel022, CatalogIndex.M045)]
    [InlineData(CatalogIndex.Cr050, CatalogIndex.C041, CatalogIndex.Mel025)]
    [InlineData(CatalogIndex.Mel025, CatalogIndex.C041, CatalogIndex.Cr050)]
    [InlineData(CatalogIndex.C041, CatalogIndex.Cr050, CatalogIndex.Mel025)]
    [InlineData(CatalogIndex.NGC0869, CatalogIndex.Mel013, CatalogIndex.Cr024)]
    [InlineData(CatalogIndex.NGC6715, CatalogIndex.M054)]
    [InlineData(CatalogIndex.GUM020, CatalogIndex.RCW_0036)]
    [InlineData(CatalogIndex.NGC6164, CatalogIndex.GUM052, CatalogIndex.RCW_0107, CatalogIndex.Ced135a, CatalogIndex.Ced135b, CatalogIndex.NGC6165, CatalogIndex.HIP081100)]
    [InlineData(CatalogIndex.NGC6165, CatalogIndex.GUM052, CatalogIndex.RCW_0107, CatalogIndex.Ced135a, CatalogIndex.Ced135b, CatalogIndex.NGC6164, CatalogIndex.HIP081100)]
    [InlineData(CatalogIndex.GUM052, CatalogIndex.NGC6164, CatalogIndex.NGC6165, CatalogIndex.HIP081100, CatalogIndex.Ced135a, CatalogIndex.Ced135b, CatalogIndex.RCW_0107)]
    [InlineData(CatalogIndex.Ced135a, CatalogIndex.NGC6164, CatalogIndex.NGC6165, CatalogIndex.HIP081100, CatalogIndex.GUM052, CatalogIndex.Ced135b, CatalogIndex.RCW_0107)]
    [InlineData(CatalogIndex.Ced135b, CatalogIndex.NGC6164, CatalogIndex.NGC6165, CatalogIndex.HIP081100, CatalogIndex.GUM052, CatalogIndex.Ced135a, CatalogIndex.RCW_0107)]
    [InlineData(CatalogIndex.RCW_0107, CatalogIndex.NGC6164, CatalogIndex.NGC6165, CatalogIndex.HIP081100, CatalogIndex.GUM052, CatalogIndex.Ced135a, CatalogIndex.Ced135b)]
    [InlineData(CatalogIndex.NGC3372, CatalogIndex.C092, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData(CatalogIndex.C092, CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData(CatalogIndex.GUM033, CatalogIndex.NGC3372, CatalogIndex.C092, CatalogIndex.RCW_0053)]
    [InlineData(CatalogIndex.RCW_0053, CatalogIndex.NGC3372, CatalogIndex.C092, CatalogIndex.GUM033)]
    [InlineData(CatalogIndex.NGC6302, CatalogIndex.C069, CatalogIndex.GUM060, CatalogIndex.RCW_0124, CatalogIndex.Sh2_0006)]
    [InlineData(CatalogIndex.C069, CatalogIndex.NGC6302, CatalogIndex.GUM060, CatalogIndex.RCW_0124, CatalogIndex.Sh2_0006)]
    [InlineData(CatalogIndex.GUM060, CatalogIndex.NGC6302, CatalogIndex.C069, CatalogIndex.RCW_0124, CatalogIndex.Sh2_0006)]
    [InlineData(CatalogIndex.RCW_0124, CatalogIndex.C069, CatalogIndex.NGC6302, CatalogIndex.GUM060, CatalogIndex.Sh2_0006)]
    [InlineData(CatalogIndex.Sh2_0006, CatalogIndex.C069, CatalogIndex.NGC6302, CatalogIndex.GUM060, CatalogIndex.RCW_0124)] 
    [InlineData(CatalogIndex.Sh2_0155, CatalogIndex.C009)]
    [InlineData(CatalogIndex.C009, CatalogIndex.Sh2_0155)]
    [InlineData(CatalogIndex.HIP034178, CatalogIndex.GUM003, CatalogIndex.vdB0094)]
    [InlineData(CatalogIndex.HIP107259, CatalogIndex.HR8316)]
    [InlineData(CatalogIndex.HIP000424, CatalogIndex.HR0001, CatalogIndex.HD000003)]
    [InlineData(CatalogIndex.HR0264, CatalogIndex.vdB0005, CatalogIndex.HIP004427, CatalogIndex.HD005394)]
    [InlineData(CatalogIndex.HR1142, CatalogIndex.vdB0020, CatalogIndex.HIP017499)]
    [InlineData(CatalogIndex.vdB0005, CatalogIndex.HR0264, CatalogIndex.HIP004427, CatalogIndex.HD005394)]
    [InlineData(CatalogIndex.vdB0020, CatalogIndex.HR1142, CatalogIndex.HIP017499)]
    [InlineData(CatalogIndex.vdB0094, CatalogIndex.GUM003, CatalogIndex.HIP034178)]
    [InlineData(CatalogIndex.NGC6514, CatalogIndex.M020, CatalogIndex.Cr360)]
    public async Task GivenACatalogIndexWhenTryingToGetCrossIndicesThenTheyAreFound(CatalogIndex catalogIndex, params CatalogIndex[] expectedCrossIndices)
    {
        // given
        var db = await InitDBAsync();

        var found = db.TryGetCrossIndices(catalogIndex, out var matches);

        found.ShouldBe(expectedCrossIndices.Length > 0);
        matches.ShouldBe(expectedCrossIndices, ignoreOrder: true);
    }

    [Theory]
    [InlineData(Constellation.Andromeda, "Alpheratz")]
    [InlineData(Constellation.Antlia, "alf Ant")]
    [InlineData(Constellation.Apus, "alf Aps")]
    [InlineData(Constellation.Aquarius, "Sadalsuud")]
    [InlineData(Constellation.Aquila, "Altair")]
    [InlineData(Constellation.Ara, "bet Ara")]
    [InlineData(Constellation.Aries, "Hamal")]
    [InlineData(Constellation.Auriga, "Capella")]
    [InlineData(Constellation.Bootes, "Arcturus")]
    [InlineData(Constellation.Caelum, "alf Cae")]
    [InlineData(Constellation.Camelopardalis, "bet Cam")]
    [InlineData(Constellation.Cancer, "Tarf")]
    [InlineData(Constellation.CanesVenatici, "Cor Caroli")]
    [InlineData(Constellation.CanisMajor, "Sirius")]
    [InlineData(Constellation.CanisMinor, "Procyon")]
    [InlineData(Constellation.Capricornus, "Deneb Algedi")]
    [InlineData(Constellation.Carina, "Canopus")]
    [InlineData(Constellation.Cassiopeia, "Schedar")]
    [InlineData(Constellation.Centaurus, "Rigil Kentaurus")]
    [InlineData(Constellation.Cepheus, "Alderamin")]
    [InlineData(Constellation.Cetus, "Diphda")]
    [InlineData(Constellation.Chamaeleon, "alf Cha")]
    [InlineData(Constellation.Circinus, "alf Cir")]
    [InlineData(Constellation.Columba, "Phact")]
    [InlineData(Constellation.ComaBerenices, "bet Com")]
    [InlineData(Constellation.CoronaAustralis, "Meridiana")]
    [InlineData(Constellation.CoronaBorealis, "Alphecca")]
    [InlineData(Constellation.Corvus, "Gienah")]
    [InlineData(Constellation.Crater, "del Crt")]
    [InlineData(Constellation.Crux, "alf01 Cru")]
    [InlineData(Constellation.Cygnus, "Deneb")]
    [InlineData(Constellation.Delphinus, "Rotanev")]
    [InlineData(Constellation.Dorado, "alf Dor")]
    [InlineData(Constellation.Draco, "Eltanin")]
    [InlineData(Constellation.Equuleus, "Kitalpha")]
    [InlineData(Constellation.Eridanus, "Achernar")]
    [InlineData(Constellation.Fornax, "Dalim")]
    [InlineData(Constellation.Gemini, "Pollux")]
    [InlineData(Constellation.Grus, "Alnair")]
    [InlineData(Constellation.Hercules, "Kornephoros")]
    [InlineData(Constellation.Horologium, "alf Hor")]
    [InlineData(Constellation.Hydra, "Alphard")]
    [InlineData(Constellation.Hydrus, "bet Hyi")]
    [InlineData(Constellation.Indus, "alf Ind")]
    [InlineData(Constellation.Lacerta, "alf Lac")]
    [InlineData(Constellation.Leo, "Regulus")]
    [InlineData(Constellation.LeoMinor, "Praecipua")]
    [InlineData(Constellation.Lepus, "Arneb")]
    [InlineData(Constellation.Libra, "Zubeneschamali")]
    [InlineData(Constellation.Lupus, "alf Lup")]
    [InlineData(Constellation.Lynx, "alf Lyn")]
    [InlineData(Constellation.Lyra, "Vega")]
    [InlineData(Constellation.Mensa, "alf Men")]
    [InlineData(Constellation.Microscopium, "gam Mic")]
    [InlineData(Constellation.Monoceros, "bet Mon A")]
    [InlineData(Constellation.Musca, "alf Mus")]
    [InlineData(Constellation.Norma, "gam02 Nor")]
    [InlineData(Constellation.Octans, "nu. Oct")]
    [InlineData(Constellation.Ophiuchus, "Rasalhague")]
    [InlineData(Constellation.Orion, "Rigel")]
    [InlineData(Constellation.Pavo, "Peacock")]
    [InlineData(Constellation.Pegasus, "Enif")]
    [InlineData(Constellation.Perseus, "Mirfak")]
    [InlineData(Constellation.Phoenix, "Ankaa")]
    [InlineData(Constellation.Pictor, "alf Pic")]
    [InlineData(Constellation.Pisces, "Alpherg")]
    [InlineData(Constellation.PiscisAustrinus, "Fomalhaut")]
    [InlineData(Constellation.Puppis, "Naos")]
    [InlineData(Constellation.Pyxis, "alf Pyx")]
    [InlineData(Constellation.Reticulum, "alf Ret")]
    [InlineData(Constellation.Sagitta, "gam Sge")]
    [InlineData(Constellation.Sagittarius, "Kaus Australis")]
    [InlineData(Constellation.Scorpius, "Antares")]
    [InlineData(Constellation.Sculptor, "alf Scl")]
    [InlineData(Constellation.Scutum, "alf Sct")]
    [InlineData(Constellation.Serpens, "Unukalhai")]
    [InlineData(Constellation.SerpensCaput, "Unukalhai")]
    [InlineData(Constellation.SerpensCauda, "eta Ser")]
    [InlineData(Constellation.Sextans, "alf Sex")]
    [InlineData(Constellation.Taurus, "Aldebaran")]
    [InlineData(Constellation.Telescopium, "alf Tel")]
    [InlineData(Constellation.Triangulum, "bet Tri")]
    [InlineData(Constellation.TriangulumAustrale, "Atria")]
    [InlineData(Constellation.Tucana, "alf Tuc")]
    [InlineData(Constellation.UrsaMajor, "Alioth")]
    [InlineData(Constellation.UrsaMinor, "Polaris")]
    [InlineData(Constellation.Vela, "gam02 Vel")]
    [InlineData(Constellation.Virgo, "Spica")]
    [InlineData(Constellation.Volans, "bet Vol")]
    [InlineData(Constellation.Vulpecula, "Anser")]
    public async Task GivenAConstellationWhenToBrightestStarThenItIsReturned(Constellation constellation, string expectedName)
    {
        // given
        var db = await InitDBAsync();

        // when
        var star = constellation.GetBrighestStar();

        // then
        db.TryLookupByIndex(star, out var starObj).ShouldBeTrue();
        constellation.IsContainedWithin(starObj.Constellation).ShouldBeTrue();
        starObj.CommonNames.Contains(expectedName).ShouldBeTrue();
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = await InitDBAsync();
        var idxs = db.AllObjectIndices;
        var catalogs = db.Catalogs;

        foreach (var idx in idxs)
        {
            var desc = $"{idx.ToCanonical()} [{idx}]";
            var isInIndex = db.TryLookupByIndex(idx, out var obj);

            isInIndex.ShouldBeTrue($"{desc} was not found!");
            var cat = idx.ToCatalog();
            catalogs.ShouldContain(cat);
            var couldCalculate = ConstellationBoundary.TryFindConstellation(obj.RA, obj.Dec, out var calculatedConstellation);

            if (cat is not Catalog.Pl && obj.ObjectType is not ObjectType.Inexistent)
            {
                couldCalculate.ShouldBeTrue();

                obj.Constellation.ShouldNotBe((Constellation)0, $"{desc}: Constellation should not be 0");
                calculatedConstellation.ShouldNotBe((Constellation)0, $"{desc}: Calculated constellation should not be 0");

                if (!obj.Constellation.IsContainedWithin(calculatedConstellation))
                {
                    var isBordering = ConstellationBoundary.IsBordering(obj.Constellation, obj.RA, obj.Dec);

                    isBordering.ShouldBeTrue($"{desc}: {obj.Constellation} is not contained within {calculatedConstellation} or any bordering");
                }

                var grid = db.CoordinateGrid[obj.RA, obj.Dec];
                grid.ShouldNotBeEmpty();

                if (!grid.Contains(idx))
                {
                    db.TryGetCrossIndices(idx, out var crossIndices).ShouldBeTrue($"{desc}: Could not find cross indices");
                    crossIndices.ShouldNotBeEmpty($"{desc}: Did not expect cross indices to be empty");

                    grid.Any(crossIndices.Contains).ShouldBeTrue($"{desc}: At least one cross index should be contained in the grid");
                }
            }
        }
    }

    [Fact]
    public async Task GivenDBWhenCreateAutoCompleteListThenItContainsAllCommonNamesAndDesignations()
    {
        // given
        var db = await InitDBAsync();

        // when
        var list = db.CreateAutoCompleteList();

        // then
        list.Length.ShouldBeGreaterThan(db.CommonNames.Count + db.AllObjectIndices.Count);

        db.CommonNames.ShouldBeSubsetOf(list);
        db.AllObjectIndices.Select(p => p.ToCanonical()).ShouldBeSubsetOf(list);
    }
}
