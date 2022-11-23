﻿using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.NOVA;
using CommunityToolkit.HighPerformance;
using Shouldly;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CelestialObjectDBTests
{
    [Theory]
    [InlineData("C041", ObjectType.OpenCluster, C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("C099", ObjectType.DarkNeb, C099, Constellation.Crux, 12.521944444444445d, -63.74333333333333d)]
    [InlineData("C30", ObjectType.Galaxy, NGC7331, Constellation.Pegasus, 22.617780555555555d, 34.415527777777775d)]
    [InlineData("ESO056-115", ObjectType.Galaxy, ESO056_115, Constellation.Dorado, 5.392916666666667d, -69.75611111111111d)]
    [InlineData("IC0048", ObjectType.Galaxy, IC0048, Constellation.Cetus, 0.7262416666666667d, -8.1865d)]
    [InlineData("IC0381", ObjectType.Galaxy, NGC1530_A, Constellation.Camelopardalis, 4.74125d, 75.63975d)]
    [InlineData("IC0458", ObjectType.Galaxy, IC0458, Constellation.Lynx, 7.176152777777778d, 50.118916666666664d)]
    [InlineData("IC0715NW", ObjectType.Galaxy, IC0715NW, Constellation.Crater, 11.615058333333334d, -8.375805555555557d)]
    [InlineData("IC0843", ObjectType.Galaxy, NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("IC1000", ObjectType.Galaxy, IC1000, Constellation.Bootes, 14.327863888888889d, 17.854694444444444d)]
    [InlineData("IC1577", ObjectType.Galaxy, IC0048, Constellation.Cetus, 0.7262416666666667d, -8.1865d)]
    [InlineData("IC4088", ObjectType.Galaxy, NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("M102", ObjectType.Galaxy, NGC5457, Constellation.UrsaMajor, 14.053483333333334d, 54.34894444444445d)]
    [InlineData("M13", ObjectType.GlobCluster, NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("M40", ObjectType.DoubleStar, M040, Constellation.UrsaMajor, 12.37113888888889d, 58.08444444444444d)]
    [InlineData("M45", ObjectType.OpenCluster, Mel022, Constellation.Taurus, 3.7912777777777777d, 24.10527777777778d)]
    [InlineData("Mel025", ObjectType.OpenCluster, C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("NGC0056", ObjectType.Unknown, NGC0056, Constellation.Pisces, 0.2557388888888889d, 12.444527777777777d)]
    [InlineData("NGC4913", ObjectType.Galaxy, NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("NGC5457", ObjectType.Galaxy, NGC5457, Constellation.UrsaMajor, 14.053483333333334d, 54.34894444444445d)]
    [InlineData("NGC6205", ObjectType.GlobCluster, NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("NGC7293", ObjectType.PlanetaryNeb, NGC7293, Constellation.Aquarius, 22.494047222222225d, -20.837333333333333d)]
    [InlineData("NGC7331", ObjectType.Galaxy, NGC7331, Constellation.Pegasus, 22.617780555555555d, 34.415527777777775d)]
    [InlineData("UGC00468", ObjectType.Galaxy, IC0049, Constellation.Cetus, 0.7322583333333333d, 1.850277777777778d)]
    [InlineData("NGC3372", ObjectType.HIIReg, NGC3372, Constellation.Carina, 10.752369444444444d, -59.86669444444445d)]
    [InlineData("GUM033", ObjectType.HIIReg, NGC3372, Constellation.Carina, 10.752369444444444d, -59.86669444444445d)]
    [InlineData("GUM016", ObjectType.HIIReg, GUM016, Constellation.Vela, 8.553333333333335d, -44.1d)]
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
        ICelestialObjectDB db = new CelestialObjectDB();
        var (actualRead, actualFailed) = await db.InitDBAsync();

        // when
        var found = db.TryLookupByIndex(indexEntry, out var celestialObject);

        // then
        actualRead.ShouldBeGreaterThan(13000);
        actualFailed.ShouldBe(0);
        found.ShouldBeTrue();
        celestialObject.Index.ShouldBe(expectedCatalogIindex);
        celestialObject.ObjectType.ShouldBe(expectedObjType);
        celestialObject.Constellation.ShouldBe(expectedConstellation);
        celestialObject.RA.ShouldBe(expectedRaDeg);
        celestialObject.Dec.ShouldBe(expectedDecDeg);
    }

    [Theory]
    [InlineData("Antennae Galaxies", NGC4038, NGC4039)]
    [InlineData("Eagle Nebula", IC4703, NGC6611)]
    [InlineData("Hercules Globular Cluster", NGC6205)]
    [InlineData("Large Magellanic Cloud", ESO056_115)]
    [InlineData("Tarantula Nebula", NGC2070)]
    [InlineData("30 Dor Cluster", NGC2070)]
    [InlineData("Orion Nebula", NGC1976)]
    [InlineData("Great Orion Nebula", NGC1976)]
    [InlineData("Pleiades", Mel022)]
    [InlineData("Keyhole", NGC3372)]
    [InlineData("Car Nebula", NGC3372)]
    [InlineData("Carina Nebula", NGC3372)]
    [InlineData("eta Car Nebula", NGC3372)]
    [InlineData("Keyhole Nebula", NGC3372)]
    public async Task GivenANameWhenLookingItUpThenAnObjIsReturned(string name, params CatalogIndex[] expectedMatches)
    {
        // given
        var db = new CelestialObjectDB();
        var (actualRead, actualFailed) = await db.InitDBAsync();

        // when
        var found = db.TryResolveCommonName(name, out var matches);

        // then
        actualRead.ShouldBeGreaterThan(13000);
        actualFailed.ShouldBe(0);
        found.ShouldBeTrue();
        matches.ShouldNotBeNull();
        matches.ShouldBeEquivalentTo(expectedMatches);
    }

    [Theory]
    [InlineData("Coalsack Nebula", C099)]
    [InlineData("tet01 Eri", HR0897)]
    [InlineData("Ran", HR1084)]
    [InlineData("18 Eri", HR1084)]
    [InlineData("eps Eri", HR1084)]
    [InlineData("Car Nebula", NGC3372)]
    [InlineData("Carina Nebula", NGC3372)]
    [InlineData("eta Car Nebula", NGC3372)]
    [InlineData("Keyhole", NGC3372)]
    [InlineData("Keyhole Nebula", NGC3372)]
    public async Task GivenANameWhenTryingToResolveItIsFound(string name, params CatalogIndex[] expectedIndices)
    {
        var db = new CelestialObjectDB();
        _ = await db.InitDBAsync();

        var found = db.TryResolveCommonName(name, out var matches);

        found.ShouldBeTrue();
        matches.ShouldNotBeNull().Count.ShouldBe(expectedIndices.Length);
        matches.ShouldBe(expectedIndices);
    }

    [Theory]
    [InlineData(HR0897, "tet01 Eri")]
    [InlineData(HR1084, "18 Eri", "eps Eri", "Ran")]
    [InlineData(C099, "Coalsack Nebula")]
    [InlineData(M042, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(NGC1976, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(M051, "Whirlpool Galaxy")]
    [InlineData(NGC3372, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    [InlineData(GUM033, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    [InlineData(NGC6302, "Bug Nebula", "Butterfly Nebula")]
    public async Task GivenACatalogIndexWhenTryingToGetCommonNamesThenTheyAreFound(CatalogIndex catalogIndex, params string[] expectedNames)
    {
        var db = new CelestialObjectDB();
        _ = await db.InitDBAsync();

        var found = db.TryLookupByIndex(catalogIndex, out var match);

        found.ShouldBeTrue();
        match.CommonNames.Count.ShouldBe(expectedNames.Length);
        match.CommonNames.ShouldBe(expectedNames, ignoreOrder: true);
    }

    [Theory]
    [InlineData(C099)]
    [InlineData(M042, NGC1976)]
    [InlineData(NGC1976, M042)]
    [InlineData(M051, NGC5194)]
    [InlineData(M054, NGC6715)]
    [InlineData(NGC6715, M054)]
    [InlineData(GUM020, RCW_0036)]
    [InlineData(NGC3372, C092, GUM033, RCW_0053)]
    [InlineData(C092, NGC3372)]
    [InlineData(GUM033, NGC3372)]
    [InlineData(RCW_0053, NGC3372)]
    [InlineData(NGC6302, C069, GUM060, RCW_0124, Sh2_006)]
    [InlineData(C069, NGC6302)]
    [InlineData(GUM060, NGC6302)]
    [InlineData(RCW_0124, NGC6302)]
    [InlineData(Sh2_006, NGC6302)]
    public async Task GivenACatalogIndexWhenTryingToGetCrossIndicesThenTheyAreFound(CatalogIndex catalogIndex, params CatalogIndex[] expectedCrossIndices)
    {
        var db = new CelestialObjectDB();
        _ = await db.InitDBAsync();

        var found = db.TryGetCrossIndices(catalogIndex, out var matches);

        found.ShouldBe(expectedCrossIndices.Length > 0);
        matches.Count.ShouldBe(expectedCrossIndices.Length);
        matches.ShouldBe(expectedCrossIndices);
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
    [InlineData(Constellation.Fornax, "alf For")]
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
        var db = new CelestialObjectDB();
        var (processed, failed) = await db.InitDBAsync();

        // when
        var star = constellation.ToBrighestStar();

        // then
        db.TryLookupByIndex(star, out var starObj).ShouldBeTrue();
        constellation.IsContainedWithin(starObj.Constellation).ShouldBeTrue();
        starObj.CommonNames.Contains(expectedName).ShouldBeTrue();

        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = new CelestialObjectDB();
        var (processed, failed) = await db.InitDBAsync();
        var idxs = db.ObjectIndices;
        var catalogs = db.Catalogs;

        // when
        Parallel.ForEach(idxs, idx =>
        {
            db.TryLookupByIndex(idx, out var obj).ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}] was not found!");
            catalogs.ShouldContain(idx.ToCatalog());
            var couldCalculate = ConstellationBoundary.TryFindConstellation(obj.RA, obj.Dec, out var calculatedConstellation);

            if (obj.ObjectType != ObjectType.Inexistent || obj.Constellation != 0)
            {
                couldCalculate.ShouldBeTrue();
                obj.Constellation.ShouldNotBe((Constellation)0, $"{idx.ToAbbreviation()} [{idx}]: Constellation should not be 0");
                calculatedConstellation.ShouldNotBe((Constellation)0, $"{idx.ToAbbreviation()} [{idx}]: Calculated constellation should not be 0");

                if (!obj.Constellation.IsContainedWithin(calculatedConstellation))
                {
                    var ra_s = CoordinateUtils.ConditionRA(obj.RA - 0.001);
                    var ra_l = CoordinateUtils.ConditionRA(obj.RA + 0.001);

                    var isBordering =
                        ConstellationBoundary.TryFindConstellation(ra_s, obj.Dec, out var const_s)
                        && ConstellationBoundary.TryFindConstellation(ra_l, obj.Dec, out var const_l)
                        && (obj.Constellation.IsContainedWithin(const_s) || const_l.IsContainedWithin(const_l));

                    isBordering.ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}]: {obj.Constellation} is not contained within {calculatedConstellation} or any bordering");
                }
            }
        });

        // then
        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }

    [Fact]
    public async Task GivenDBWhenCreateAutoCompleteListThenItContainsAllCommonNamesAndDesignations()
    {
        // given
        var db = new CelestialObjectDB();
        await db.InitDBAsync();

        // when
        var list = db.CreateAutoCompleteList();

        // then
        list.Length.ShouldBe(db.CommonNames.Count + db.ObjectIndices.Count);

        db.CommonNames.ShouldBeSubsetOf(list);
        db.ObjectIndices.Select(p => p.ToCanonical()).ShouldBeSubsetOf(list);
    }
}