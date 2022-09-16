using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

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
}
