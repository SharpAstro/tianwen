using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TianWen.Lib.Tests;

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
    // HD cross-reference first 10
    [InlineData("HD000001", ObjectType.Star, CatalogIndex.HD000001, Constellation.Cepheus, 0.0857878625d, 67.8400115967d)]
    [InlineData("HD000002", ObjectType.Star, CatalogIndex.HD000002, Constellation.Cassiopeia, 0.0849866346d, 57.7703285217d)]
    [InlineData("HD000003", ObjectType.Star, CatalogIndex.HD000003, Constellation.Andromeda, 0.0860434994d, 45.2290306091d)]
    [InlineData("HD000004", ObjectType.Star, CatalogIndex.HD000004, Constellation.Pegasus, 0.0858989134d, 30.3290309906d)]
    [InlineData("HD000005", ObjectType.Star, CatalogIndex.HD000005, Constellation.Pisces, 0.0861614272d, 2.3972029686d)]
    [InlineData("HD000006", ObjectType.Star, CatalogIndex.HD000006, Constellation.Pisces, 0.0843953714d, -0.5030353069d)]
    [InlineData("HD000007", ObjectType.Star, CatalogIndex.HD000007, Constellation.Pisces, 0.0862675384d, -1.8536438942d)]
    [InlineData("HD000008", ObjectType.Star, CatalogIndex.HD000008, Constellation.Pisces, 0.0860361978d, -4.0346636772d)]
    [InlineData("HD000009", ObjectType.Star, CatalogIndex.HD000009, Constellation.Cetus, 0.0854400545d, -20.6129074097d)]
    [InlineData("HD000010", ObjectType.Star, CatalogIndex.HD000010, Constellation.Phoenix, 0.0852333978d, -42.5678710938d)]
    // HD cross-reference multi-TYC entries (collisions resolved via JSON sidecar)
    [InlineData("HD023068", ObjectType.Star, CatalogIndex.HD023068, Constellation.Eridanus, 3.6885507107d, -22.9102020264d)]
    [InlineData("HD037703", ObjectType.Star, CatalogIndex.HD037703, Constellation.Lepus, 5.6608538628d, -21.6159019470d)]
    [InlineData("HD045900", ObjectType.Star, CatalogIndex.HD045900, Constellation.Gemini, 6.5192265511d, 21.7192802429d)]
    [InlineData("HD063846", ObjectType.Star, CatalogIndex.HD063846, Constellation.Puppis, 7.8356800079d, -20.8451938629d)]
    [InlineData("HD086269", ObjectType.Star, CatalogIndex.HD086269, Constellation.Carina, 9.9250593185d, -57.9546508789d)]
    // HD cross-reference last 10
    [InlineData("HD359074", ObjectType.Star, CatalogIndex.HD359074, Constellation.Capricornus, 21.4737014771d, -16.6873950958d)]
    [InlineData("HD359075", ObjectType.Star, CatalogIndex.HD359075, Constellation.Capricornus, 21.4681739807d, -17.0429763794d)]
    [InlineData("HD359076", ObjectType.Star, CatalogIndex.HD359076, Constellation.Capricornus, 21.4684009552d, -17.1383743286d)]
    [InlineData("HD359077", ObjectType.Star, CatalogIndex.HD359077, Constellation.Capricornus, 21.4480381012d, -17.4419612885d)]
    [InlineData("HD359078", ObjectType.Star, CatalogIndex.HD359078, Constellation.Capricornus, 21.4434127808d, -17.3815097809d)]
    [InlineData("HD359079", ObjectType.Star, CatalogIndex.HD359079, Constellation.Capricornus, 21.4300880432d, -17.3793792725d)]
    [InlineData("HD359080", ObjectType.Star, CatalogIndex.HD359080, Constellation.Capricornus, 21.4426498413d, -17.4391345978d)]
    [InlineData("HD359081", ObjectType.Star, CatalogIndex.HD359081, Constellation.Capricornus, 21.4565372467d, -19.3454189301d)]
    [InlineData("HD359082", ObjectType.Star, CatalogIndex.HD359082, Constellation.Capricornus, 21.4612331390d, -19.5093517303d)]
    [InlineData("HD359083", ObjectType.Star, CatalogIndex.HD359083, Constellation.Capricornus, 21.4717769623d, -20.2923583984d)]
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
        celestialObject.RA.ShouldBeInRange(expectedRaDeg - 0.001d, expectedRaDeg + 0.001d);
        celestialObject.Dec.ShouldBeInRange(expectedDecDeg - 0.001d, expectedDecDeg + 0.001d);
    }

    [Theory]
    [InlineData("Antennae Galaxies", CatalogIndex.NGC4038, CatalogIndex.NGC4039)]
    [InlineData("Eagle Nebula", CatalogIndex.IC4703, CatalogIndex.NGC6611, CatalogIndex.M016)]
    [InlineData("Hercules Globular Cluster", CatalogIndex.NGC6205, CatalogIndex.M013)]
    [InlineData("Large Magellanic Cloud", CatalogIndex.ESO056_115)]
    [InlineData("Tarantula Nebula", CatalogIndex.NGC2070)]
    [InlineData("30 Dor Cluster", CatalogIndex.NGC2070)]
    [InlineData("Orion Nebula", CatalogIndex.NGC1976, CatalogIndex.M042)]
    [InlineData("Great Orion Nebula", CatalogIndex.NGC1976, CatalogIndex.M042)]
    [InlineData("Hyades", CatalogIndex.C041)]
    [InlineData("Pleiades", CatalogIndex.Mel022, CatalogIndex.M045)]
    [InlineData("Keyhole", CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData("Car Nebula", CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData("Carina Nebula", CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData("eta Car Nebula", CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData("Keyhole Nebula", CatalogIndex.NGC3372, CatalogIndex.GUM033, CatalogIndex.RCW_0053)]
    [InlineData("Cave Nebula", CatalogIndex.C009, CatalogIndex.Ced0201, CatalogIndex.DG0179)]
    [InlineData("Coalsack Nebula", CatalogIndex.C099)]
    [InlineData("tet01 Eri", CatalogIndex.HD018622, CatalogIndex.HR0897)]
    [InlineData("Trifid Nebula", CatalogIndex.NGC6514, CatalogIndex.M020, CatalogIndex.Cr360)]
    [InlineData("Ran", CatalogIndex.HIP016537, CatalogIndex.HD022049, CatalogIndex.HR1084)]
    [InlineData("18 Eri", CatalogIndex.HIP016537, CatalogIndex.HD022049, CatalogIndex.HR1084)]
    [InlineData("eps Eri", CatalogIndex.HIP016537, CatalogIndex.HD022049, CatalogIndex.HR1084)]
    [InlineData("Electra", CatalogIndex.HIP017499, CatalogIndex.HD023302, CatalogIndex.HR1142, CatalogIndex.vdB0020)]
    [InlineData("Erakis", CatalogIndex.HIP107259, CatalogIndex.HD206936, CatalogIndex.HR8316)]
    [InlineData("eta Ori", CatalogIndex.HIP025281, CatalogIndex.HD035411, CatalogIndex.HR1788)]
    [InlineData("28 Ori", CatalogIndex.HIP025281, CatalogIndex.HD035411, CatalogIndex.HR1788)]
    public async Task GivenANameWhenLookingItUpThenAnObjIsReturned(string name, params CatalogIndex[] expectedMatches)
    {
        // given
        var db = await InitDBAsync();

        // when
        var found = db.TryResolveCommonName(name, out var matches);

        // then
        found.ShouldBeTrue();
        matches.ShouldNotBeNull();
        matches.ShouldBe(expectedMatches);
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
    // eta Ori: Bayer designation and Flamsteed number should be common names on all cross-refs
    [InlineData(CatalogIndex.HR1788, "28 Ori", "eta Ori")]
    [InlineData(CatalogIndex.HIP025281, "28 Ori", "eta Ori")]
    [InlineData(CatalogIndex.HD035411, "28 Ori", "eta Ori")]
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
    [InlineData(CatalogIndex.HR8316, CatalogIndex.HIP107259)]
    // eta Ori cross-references (HIP/HD/HR should all be linked)
    [InlineData(CatalogIndex.HIP025281, CatalogIndex.HD035411, CatalogIndex.HR1788)]
    [InlineData(CatalogIndex.HD035411, CatalogIndex.HIP025281, CatalogIndex.HR1788)]
    [InlineData(CatalogIndex.HR1788, CatalogIndex.HIP025281, CatalogIndex.HD035411)]
    [InlineData(CatalogIndex.HIP000424, CatalogIndex.HR0001, CatalogIndex.HD000003)]
    [InlineData(CatalogIndex.HR0264, CatalogIndex.vdB0005, CatalogIndex.HIP004427, CatalogIndex.HD005394)]
    [InlineData(CatalogIndex.HR1142, CatalogIndex.vdB0020, CatalogIndex.HIP017499, CatalogIndex.HD023302)]
    [InlineData(CatalogIndex.vdB0005, CatalogIndex.HR0264, CatalogIndex.HIP004427, CatalogIndex.HD005394)]
    [InlineData(CatalogIndex.vdB0020, CatalogIndex.HR1142, CatalogIndex.HIP017499, CatalogIndex.HD023302)]
    [InlineData(CatalogIndex.vdB0094, CatalogIndex.GUM003, CatalogIndex.HIP034178)]
    [InlineData(CatalogIndex.NGC6514, CatalogIndex.M020, CatalogIndex.Cr360)]
    public async Task GivenACatalogIndexWhenTryingToGetCrossIndicesThenTheyAreFound(CatalogIndex catalogIndex, params CatalogIndex[] expectedCrossIndices)
    {
        // given
        var db = await InitDBAsync();

        var found = db.TryGetCrossIndices(catalogIndex, out var matches);

        found.ShouldBe(expectedCrossIndices.Length > 0);
        expectedCrossIndices.ShouldBeSubsetOf(matches);
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

    [Theory]
    // HD→TYC multi-match (collision) entries
    [InlineData(CatalogIndex.HD023068, CatalogIndex.Tyc_6447_45_1, CatalogIndex.Tyc_6447_45_2)]
    [InlineData(CatalogIndex.HD037703, CatalogIndex.Tyc_5929_1314_1, CatalogIndex.Tyc_5929_1314_2)]
    [InlineData(CatalogIndex.HD045900, CatalogIndex.Tyc_1340_1326_1, CatalogIndex.Tyc_1340_2546_1)]
    [InlineData(CatalogIndex.HD063846, CatalogIndex.Tyc_5993_1105_1, CatalogIndex.Tyc_5993_1105_2)]
    [InlineData(CatalogIndex.HD086269, CatalogIndex.Tyc_8606_795_1, CatalogIndex.Tyc_8606_795_2)]
    // HIP→TYC multi-match (collision) entries
    [InlineData(CatalogIndex.HIP000040, CatalogIndex.Tyc_4026_566_1, CatalogIndex.Tyc_4026_566_2)]
    [InlineData(CatalogIndex.HIP000096, CatalogIndex.Tyc_600_1507_1, CatalogIndex.Tyc_600_1507_2)]
    [InlineData(CatalogIndex.HIP000110, CatalogIndex.Tyc_2785_116_1, CatalogIndex.Tyc_2785_116_2)]
    [InlineData(CatalogIndex.HIP000114, CatalogIndex.Tyc_2259_1286_1, CatalogIndex.Tyc_2259_1286_2)]
    [InlineData(CatalogIndex.HIP000178, CatalogIndex.Tyc_6415_65_1, CatalogIndex.Tyc_6415_65_2)]
    public async Task GivenStarWithMultipleTycMatchesWhenLookingUpThenAllResolveNearby(CatalogIndex sourceIndex, params CatalogIndex[] expectedTycIndices)
    {
        // given
        var db = await InitDBAsync();

        // when — source star resolves (position from first TYC match)
        var sourceFound = db.TryLookupByIndex(sourceIndex, out var sourceObj);

        // then
        sourceFound.ShouldBeTrue($"{sourceIndex.ToCanonical()} should be found");
        sourceObj.ObjectType.ShouldBe(ObjectType.Star);

        foreach (var tycIndex in expectedTycIndices)
        {
            var tycFound = db.TryLookupByIndex(tycIndex, out var tycObj);
            tycFound.ShouldBeTrue($"{tycIndex.ToCanonical()} should be found");
            tycObj.ObjectType.ShouldBe(ObjectType.Star);

            // Double star components should be within ~0.1° of each other
            tycObj.RA.ShouldBeInRange(sourceObj.RA - 0.1d, sourceObj.RA + 0.1d, $"{tycIndex.ToCanonical()} RA");
            tycObj.Dec.ShouldBeInRange(sourceObj.Dec - 0.1d, sourceObj.Dec + 0.1d, $"{tycIndex.ToCanonical()} Dec");
        }
    }

    [Theory]
    // HD→TYC: each TYC in a collision pair should cross-ref back to its HD source
    [InlineData(CatalogIndex.Tyc_6447_45_1, CatalogIndex.HD023068)]
    [InlineData(CatalogIndex.Tyc_6447_45_2, CatalogIndex.HD023068)]
    [InlineData(CatalogIndex.Tyc_5929_1314_1, CatalogIndex.HD037703)]
    [InlineData(CatalogIndex.Tyc_5929_1314_2, CatalogIndex.HD037703)]
    [InlineData(CatalogIndex.Tyc_1340_1326_1, CatalogIndex.HD045900)]
    [InlineData(CatalogIndex.Tyc_1340_2546_1, CatalogIndex.HD045900)]
    [InlineData(CatalogIndex.Tyc_5993_1105_1, CatalogIndex.HD063846)]
    [InlineData(CatalogIndex.Tyc_5993_1105_2, CatalogIndex.HD063846)]
    [InlineData(CatalogIndex.Tyc_8606_795_1, CatalogIndex.HD086269)]
    [InlineData(CatalogIndex.Tyc_8606_795_2, CatalogIndex.HD086269)]
    // HIP→TYC: each TYC in a collision pair should cross-ref back to its HIP source
    [InlineData(CatalogIndex.Tyc_4026_566_1, CatalogIndex.HIP000040)]
    [InlineData(CatalogIndex.Tyc_4026_566_2, CatalogIndex.HIP000040)]
    [InlineData(CatalogIndex.Tyc_600_1507_1, CatalogIndex.HIP000096)]
    [InlineData(CatalogIndex.Tyc_600_1507_2, CatalogIndex.HIP000096)]
    [InlineData(CatalogIndex.Tyc_2785_116_1, CatalogIndex.HIP000110)]
    [InlineData(CatalogIndex.Tyc_2785_116_2, CatalogIndex.HIP000110)]
    [InlineData(CatalogIndex.Tyc_2259_1286_1, CatalogIndex.HIP000114)]
    [InlineData(CatalogIndex.Tyc_2259_1286_2, CatalogIndex.HIP000114)]
    [InlineData(CatalogIndex.Tyc_6415_65_1, CatalogIndex.HIP000178)]
    [InlineData(CatalogIndex.Tyc_6415_65_2, CatalogIndex.HIP000178)]
    public async Task GivenTycStarFromCollisionWhenLookingUpCrossIndicesThenSourceIsFound(CatalogIndex tycIndex, CatalogIndex expectedSourceIndex)
    {
        // given
        var db = await InitDBAsync();

        // when
        var tycFound = db.TryLookupByIndex(tycIndex, out var tycObj);
        var hasCrossIndices = db.TryGetCrossIndices(tycIndex, out var crossIndices);

        // then
        tycFound.ShouldBeTrue($"{tycIndex.ToCanonical()} should be found");
        hasCrossIndices.ShouldBeTrue($"{tycIndex.ToCanonical()} should have cross-indices");
        crossIndices.ShouldContain(expectedSourceIndex, $"{tycIndex.ToCanonical()} should cross-ref to {expectedSourceIndex.ToCanonical()}");
    }

    [Theory]
    [InlineData(CatalogIndex.Tyc_6447_45_1)]
    [InlineData(CatalogIndex.Tyc_6447_45_2)]
    [InlineData(CatalogIndex.Tyc_5929_1314_1)]
    [InlineData(CatalogIndex.Tyc_5929_1314_2)]
    [InlineData(CatalogIndex.Tyc_1340_1326_1)]
    [InlineData(CatalogIndex.Tyc_1340_2546_1)]
    [InlineData(CatalogIndex.Tyc_5993_1105_1)]
    [InlineData(CatalogIndex.Tyc_5993_1105_2)]
    [InlineData(CatalogIndex.Tyc_8606_795_1)]
    [InlineData(CatalogIndex.Tyc_8606_795_2)]
    [InlineData(CatalogIndex.Tyc_4026_566_1)]
    [InlineData(CatalogIndex.Tyc_4026_566_2)]
    [InlineData(CatalogIndex.Tyc_600_1507_1)]
    [InlineData(CatalogIndex.Tyc_600_1507_2)]
    [InlineData(CatalogIndex.Tyc_2785_116_1)]
    [InlineData(CatalogIndex.Tyc_2785_116_2)]
    [InlineData(CatalogIndex.Tyc_2259_1286_1)]
    [InlineData(CatalogIndex.Tyc_2259_1286_2)]
    [InlineData(CatalogIndex.Tyc_6415_65_1)]
    [InlineData(CatalogIndex.Tyc_6415_65_2)]
    public async Task GivenTycStarWhenLookingUpByRaDecThenItIsFoundInCoordinateGrid(CatalogIndex tycIndex)
    {
        // given
        var db = await InitDBAsync();

        // when — look up the star to get its coordinates
        var found = db.TryLookupByIndex(tycIndex, out var tycObj);

        // then — the star should exist and be findable in the coordinate grid
        found.ShouldBeTrue($"{tycIndex.ToCanonical()} should be found");
        tycObj.ObjectType.ShouldBe(ObjectType.Star);

        var grid = db.CoordinateGrid[tycObj.RA, tycObj.Dec];
        grid.ShouldNotBeEmpty($"{tycIndex.ToCanonical()} at RA={tycObj.RA:F4} Dec={tycObj.Dec:F4} should have grid entries");
        grid.ShouldContain(tycIndex, $"{tycIndex.ToCanonical()} should be in the coordinate grid at its own RA/Dec");
    }

    [Theory]
    [InlineData(CatalogIndex.NGC7331, 9.27, 3.76, 170.0)]   // NGC 7331
    [InlineData(CatalogIndex.NGC5194, 13.71, 11.67, 163.0)] // M51 Whirlpool
    [InlineData(CatalogIndex.NGC5457, 23.99, 23.07, 28.0)]  // M101 Pinwheel
    public async Task GivenGalaxyWhenTryGetShapeThenShapeDataIsReturned(CatalogIndex catalogIndex, double expectedMajAx, double expectedMinAx, double expectedPA)
    {
        // given
        var db = await InitDBAsync();

        // when
        var found = db.TryGetShape(catalogIndex, out var shape);

        // then
        found.ShouldBeTrue($"{catalogIndex.ToCanonical()} should have shape data");
        ((double)shape.MajorAxis).ShouldBeInRange(expectedMajAx - 0.1, expectedMajAx + 0.1);
        ((double)shape.MinorAxis).ShouldBeInRange(expectedMinAx - 0.1, expectedMinAx + 0.1);
        ((double)shape.PositionAngle).ShouldBeInRange(expectedPA - 1.0, expectedPA + 1.0);
    }

    [Fact]
    public async Task GivenStarWhenTryGetShapeThenNoShapeData()
    {
        // given
        var db = await InitDBAsync();

        // when
        var found = db.TryGetShape(CatalogIndex.HD000001, out _);

        // then
        found.ShouldBeFalse();
    }

    [Fact]
    public async Task GivenDBWhenCreateAutoCompleteListThenItContainsAllCommonNamesAndDesignations()
    {
        // given
        var db = await InitDBAsync();

        // when
        var list = db.CreateAutoCompleteList();

        // then
        list.Length.ShouldBeGreaterThanOrEqualTo(db.CommonNames.Count + db.AllObjectIndices.Count);

        db.CommonNames.ShouldBeSubsetOf(list);
        db.AllObjectIndices.Select(p => p.ToCanonical()).ShouldBeSubsetOf(list);
    }

    [Fact]
    public async Task GivenElectraWhenLookingUpTycho2EntryThenPositionMatchesHIP()
    {
        // Regression test: Tycho-2 type 'X' entries (no astrometric solution) had ICRS
        // coordinates incorrectly precessed, producing ~426" error for Electra (TYC 1799-1441-1).
        // See Get-Tycho2Catalogs.ps1 fix — ICRS positions must NOT be precessed.

        var db = await InitDBAsync();

        // Electra via HIP — this is our reference position
        db.TryLookupByIndex(CatalogIndex.HIP017499, out var electraHip).ShouldBeTrue("HIP 17499 should be found");

        // HIP position should be within 10" of Stellarium J2000 (RA=3.747764h, Dec=24.112833°)
        var hipDeltaRaArcsec = (electraHip.RA - 3.747764) * 15.0 * Math.Cos(Math.PI / 180.0 * electraHip.Dec) * 3600.0;
        var hipDeltaDecArcsec = (electraHip.Dec - 24.112833) * 3600.0;
        var hipDistArcsec = Math.Sqrt(hipDeltaRaArcsec * hipDeltaRaArcsec + hipDeltaDecArcsec * hipDeltaDecArcsec);
        hipDistArcsec.ShouldBeLessThan(10.0, "HIP 17499 should be within 10\" of Stellarium J2000 position");

        // TYC 1799-1441-1 direct lookup — should exist and match HIP position within 5"
        db.TryLookupByIndex("TYC 1799-1441-1", out var tycObj).ShouldBeTrue("TYC 1799-1441-1 should be found");

        var cosDec = Math.Cos(Math.PI / 180.0 * electraHip.Dec);
        var tycDeltaRaArcsec = (tycObj.RA - electraHip.RA) * 15.0 * cosDec * 3600.0;
        var tycDeltaDecArcsec = (tycObj.Dec - electraHip.Dec) * 3600.0;
        var tycDistArcsec = Math.Sqrt(tycDeltaRaArcsec * tycDeltaRaArcsec + tycDeltaDecArcsec * tycDeltaDecArcsec);
        tycDistArcsec.ShouldBeLessThan(5.0, "TYC 1799-1441-1 should be within 5\" of HIP 17499 (was 426\" before ICRS precession fix)");

        // Magnitude should be ~3.7
        ((double)tycObj.V_Mag).ShouldBeInRange(3.5, 3.9, "Electra V magnitude");

        // TYC should also appear in spatial grid near Electra
        var grid = db.CoordinateGrid[electraHip.RA, electraHip.Dec];
        grid.ShouldNotBeEmpty("Grid cell at Electra's position should have entries");
    }

}
