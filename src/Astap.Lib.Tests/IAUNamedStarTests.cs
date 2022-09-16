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
    [InlineData("Achernar", "HR 472")]
    public async Task GivenAnIAUStarNameWhenGettingNamedStarThenItIsFound(string iauName, string expectedCatIndex)
    {
        var db = new IAUNamedStarDB();
        _ = await db.ReadEmbeddedDataFileAsync();

        var actualFound = db.TryGetStellarObjectByIAUName(iauName, out var namedStar);

        actualFound.ShouldBeTrue();
        namedStar.ShouldNotBeNull();
        namedStar.Index.ShouldBe(AbbreviationToEnumMember<CatalogIndex>(expectedCatIndex));
    }
}
