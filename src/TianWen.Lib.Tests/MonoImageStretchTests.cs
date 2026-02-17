using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public class MonoImageStretchTests(ITestOutputHelper testOutputHelper) : StretchTestBase(testOutputHelper)
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16", 20, -5, 1)]
    [InlineData("Vela_SNR_Panel_8_1-Multi-NB-mono-Hydrogen-alpha-Oxygen_III-crop", 15, -5, 1)]
    public Task GivenFitsFileWhenStretchingItShouldDisplay(string name, int stretchPct, int clippingSigma, uint expectedChannelCount)
        => StretchTest(name, DebayerAlgorithm.None, stretchPct, clippingSigma, false, expectedChannelCount);
}
