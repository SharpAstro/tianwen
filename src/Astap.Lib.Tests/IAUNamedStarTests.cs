using Astap.Lib.Astrometry;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class IAUNamedStarTests
{
    [Fact]
    public async Task ReadStarsTest()
    {
        await new IAUNamedStarReader().ReadEmbeddedDataFileAsync();
    }
}
