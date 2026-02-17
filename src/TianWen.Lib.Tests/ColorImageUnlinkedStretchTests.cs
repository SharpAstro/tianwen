using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class ColorImageLinkedStretchTests(ITestOutputHelper testOutputHelper) : StretchTestBase(testOutputHelper)
{
    [Theory]
    [InlineData("Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop", 15, -5, 3)]
    public Task GivenFitsFileWhenStretchingLinkedThenItShouldDisplay(string name, int stretchPct, int clippingSigma, uint expectedChannelCount)
        => StretchTest(name, DebayerAlgorithm.None, stretchPct, clippingSigma, true, expectedChannelCount);
}
