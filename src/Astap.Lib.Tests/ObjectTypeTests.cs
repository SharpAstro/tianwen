using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests;

public class ObjectTypeTests
{
    [Theory]
    [InlineData(ObjectType.ReflectionNebula, "Reflection Nebula")]
    [InlineData(ObjectType.Nebula, "Nebula")]
    [InlineData(ObjectType.GlobularCluster, "Globular Cluster")]
    [InlineData(ObjectType.ClusterAndNebula, "Cluster And Nebula")]
    [InlineData(ObjectType.GroupOfGalaxies, "Group Of Galaxies")]
    [InlineData(ObjectType.HIIRegion, "HII Region")]
    [InlineData(ObjectType.AssociationOfStars, "Association Of Stars")]
    public void GivenObjectTypeWhenToNameThenPascalCaseNameIsSplitNaturally(ObjectType objectType, string expectedToName)
    {
        objectType.ToName().ShouldBe(expectedToName);
    }
}
