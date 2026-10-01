using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The Galilean moons by Meeus' low-accuracy theory against his worked example 44.a (1992 December 16, 0h UT).</summary>
public class GalileanMoonsTests
{
    [Theory]
    [InlineData(0, -3.44, 0.21)]
    [InlineData(1, 7.44, 0.25)]
    [InlineData(2, 1.24, 0.65)]
    [InlineData(3, 7.08, 1.10)]
    public void TheMoonsMatchMeeusExample44a(int index, double x, double y)
    {
        var (moons, _) = GalileanMoons.At(2_448_972.50068);
        moons[index].X.ShouldBe(x, 0.006);
        moons[index].Y.ShouldBe(y, 0.006);
    }
}
