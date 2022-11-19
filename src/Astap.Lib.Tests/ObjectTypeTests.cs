using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests;

public class ObjectTypeTests
{
    [Theory]
    [InlineData(ObjectType.RefNeb, "Ref Neb")]
    [InlineData(ObjectType.GalNeb, "Gal Neb")]
    [InlineData(ObjectType.GlobCluster, "Glob Cluster")]
    [InlineData(ObjectType.EmObj, "Em Obj")]
    [InlineData(ObjectType.GroupG, "Group of Galaxies")]
    [InlineData(ObjectType.HIIReg, "HII Reg")]
    [InlineData(ObjectType.Association, "Association")]
    public void GivenObjectTypeWhenToNameThenPascalCaseNameIsSplitNaturally(ObjectType objectType, string expectedToName)
    {
        objectType.ToName().ShouldBe(expectedToName);
    }
}
