using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class StretchTests_DebayeredImageUnlinked(ITestOutputHelper testOutputHelper) : StretchTestBase(testOutputHelper)
{
    [Theory]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 15, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.VNG, 20, 3)]
    [InlineData("RGGB_frame_bx0_by0_top_down", DebayerAlgorithm.BilinearMono, 20, 1)]
    [InlineData("2026-02-15_00-56-23__-5.00_60.00s_0058", DebayerAlgorithm.AHD, 15, 3)]
    public Task GivenFitsFileWhenStretchingItShouldDisplay(string name, DebayerAlgorithm algorithm, int stretchPct, uint expectedChannelCount)
        => StretchTest(name, algorithm, stretchPct, -3, false, expectedChannelCount);
}