using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests;

public class ObjectTypeTests
{
    [Theory]
    [InlineData(ObjectType.RefNeb, "Reflection Nebula")]
    [InlineData(ObjectType.GalNeb, "Galactic Nebula")]
    [InlineData(ObjectType.GlobCluster, "Globular Cluster")]
    [InlineData(ObjectType.EmObj, "Emmission Object")]
    [InlineData(ObjectType.GroupG, "Group of Galaxies")]
    [InlineData(ObjectType.HIIReg, "HII Region")]
    [InlineData(ObjectType.Association, "Association")]
    public void GivenObjectTypeWhenToNameThenPascalCaseNameIsSplitNaturally(ObjectType objectType, string expectedToName)
    {
        objectType.ToName().ShouldBe(expectedToName);
    }
}
