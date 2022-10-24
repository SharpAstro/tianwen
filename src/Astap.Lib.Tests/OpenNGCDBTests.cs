﻿using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class OpenNGCDBTests
{
    [Theory]
    [InlineData("C041", ObjectType.OpenCluster, C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("C099", ObjectType.DarkNebula, C099, Constellation.Crux, 12.521944444444445d, -63.74333333333333d)]
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
    [InlineData("M13", ObjectType.GlobularCluster, NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("M40", ObjectType.DoubleStar, M040, Constellation.UrsaMajor, 12.37113888888889d, 58.08444444444444d)]
    [InlineData("M45", ObjectType.OpenCluster, Mel022, Constellation.Taurus, 3.7912777777777777d, 24.10527777777778d)]
    [InlineData("Mel025", ObjectType.OpenCluster, C041, Constellation.Taurus, 4.448333333333333d, 15.866666666666667d)]
    [InlineData("NGC0056", ObjectType.Other, NGC0056, Constellation.Pisces, 0.2557388888888889d, 12.444527777777777d)]
    [InlineData("NGC4913", ObjectType.Galaxy, NGC4913, Constellation.ComaBerenices, 13.02871388888889d, 29.044666666666668d)]
    [InlineData("NGC5457", ObjectType.Galaxy, NGC5457, Constellation.UrsaMajor, 14.053483333333334d, 54.34894444444445d)]
    [InlineData("NGC6205", ObjectType.GlobularCluster, NGC6205, Constellation.Hercules, 16.694897222222224d, 36.46130555555556d)]
    [InlineData("NGC7293", ObjectType.PlanetaryNebula, NGC7293, Constellation.Aquarius, 22.494047222222225d, -20.837333333333333d)]
    [InlineData("NGC7331", ObjectType.Galaxy, NGC7331, Constellation.Pegasus, 22.617780555555555d, 34.415527777777775d)]
    [InlineData("UGC00468", ObjectType.Galaxy, IC0049, Constellation.Cetus, 0.7322583333333333d, 1.850277777777778d)]
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
        ICelestialObjectDB db = new OpenNGCDB();
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
    public async Task GivenANameWhenLookingItUpThenAnObjIsReturned(string name, params CatalogIndex[] expectedMatches)
    {
        // given
        var db = new OpenNGCDB();
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

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = new OpenNGCDB();
        var (processed, failed) = await db.InitDBAsync();
        var idxs = db.ObjectIndices;
        var catalogs = db.Catalogs;

        // when
        Parallel.ForEach(idxs, idx =>
        {
            db.TryLookupByIndex(idx, out _).ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}] was not found!");
            catalogs.ShouldContain(idx.ToCatalog());
        });

        // then
        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }
}
