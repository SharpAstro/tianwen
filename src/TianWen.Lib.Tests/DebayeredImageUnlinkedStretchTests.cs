using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class DebayeredImageUnlinkedStretchTests(ITestOutputHelper testOutputHelper) : StretchTestBase(testOutputHelper)
{
    [Theory]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 15, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 20, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.BilinearMono, 20, 1)]
    public Task GivenFitsFileWhenStretchingItShouldDisplay(string name, DebayerAlgorithm algorithm, int stretchPct, uint expectedChannelCount)
        => StretchTest(name, algorithm, stretchPct, -3, false, expectedChannelCount);
}
