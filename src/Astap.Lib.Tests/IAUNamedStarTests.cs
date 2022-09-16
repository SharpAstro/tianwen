using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.EnumHelper;

namespace Astap.Lib.Tests;

public class IAUNamedStarTests
{
    [Fact]
    public async Task ReadStarsTest()
    {
        var (processed, failed) = await new IAUNamedStarDB().ReadEmbeddedDataFileAsync();

        processed.ShouldBe(451);
        failed.ShouldBe(0);
    }

    [Theory]
    [InlineData("Achernar", ObjectType.Star, "HR 472")]
    [InlineData("Geminga", ObjectType.Pulsar, "PSR B0633+17")]
    public async Task GivenAnIAUStarNameWhenGettingNamedStarThenItIsFound(string iauName, ObjectType expectedObjectType, string expectedDesignation)
    {
        var db = new IAUNamedStarDB();
        _ = await db.ReadEmbeddedDataFileAsync();
        Utils.TryGetCleanedUpCatalogName(expectedDesignation, out var designationCatIndex).ShouldBeTrue();

        var actualFound = db.TryGetStellarObjectByIAUName(iauName, out var namedStar);

        actualFound.ShouldBeTrue();
        namedStar.ShouldNotBeNull();
        namedStar.Index.ShouldBe(designationCatIndex);
        namedStar.ObjectType.ShouldBe(expectedObjectType);
    }
}
