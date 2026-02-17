using TianWen.Lib.Imaging;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

public class SensorTypeTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, 1, 2)] // RGGB: R G / G B
    [InlineData(1, 0, 1, 0, 2, 1)] // GRBG: G R / B G
    [InlineData(0, 1, 1, 2, 0, 1)] // GBRG: G B / R G
    [InlineData(1, 1, 2, 1, 1, 0)] // BGGR: B G / G R
    public void GetBayerPatternMatrix_WithRGGB_ReturnsCorrectPattern(
        int offsetX, int offsetY,
        int topLeft, int topRight,
        int bottomLeft, int bottomRight)
    {
        var pattern = SensorType.RGGB.GetBayerPatternMatrix(offsetX, offsetY);

        pattern.ShouldNotBeNull();
        pattern.GetLength(0).ShouldBe(2);
        pattern.GetLength(1).ShouldBe(2);
        pattern[0, 0].ShouldBe(topLeft);
        pattern[0, 1].ShouldBe(topRight);
        pattern[1, 0].ShouldBe(bottomLeft);
        pattern[1, 1].ShouldBe(bottomRight);
    }

    [Fact]
    public void GetBayerPatternMatrix_Monochrome_ReturnsRGGBPattern()
    {
        var pattern = SensorType.Monochrome.GetBayerPatternMatrix(0, 0);

        pattern.ShouldNotBeNull();
        pattern.GetLength(0).ShouldBe(2);
        pattern.GetLength(1).ShouldBe(2);
        pattern[0, 0].ShouldBe(0); // R
        pattern[0, 1].ShouldBe(1); // G
        pattern[1, 0].ShouldBe(1); // G
        pattern[1, 1].ShouldBe(2); // B
    }

    [Fact]
    public void GetBayerPatternMatrix_Color_ReturnsRGGBPattern()
    {
        var pattern = SensorType.Color.GetBayerPatternMatrix(0, 0);

        pattern.ShouldNotBeNull();
        pattern.GetLength(0).ShouldBe(2);
        pattern.GetLength(1).ShouldBe(2);
        pattern[0, 0].ShouldBe(0); // R
        pattern[0, 1].ShouldBe(1); // G
        pattern[1, 0].ShouldBe(1); // G
        pattern[1, 1].ShouldBe(2); // B
    }

    [Fact]
    public void GetBayerPatternMatrix_Unknown_ReturnsRGGBPattern()
    {
        var pattern = SensorType.Unknown.GetBayerPatternMatrix(0, 0);

        pattern.ShouldNotBeNull();
        pattern.GetLength(0).ShouldBe(2);
        pattern.GetLength(1).ShouldBe(2);
        pattern[0, 0].ShouldBe(0); // R
        pattern[0, 1].ShouldBe(1); // G
        pattern[1, 0].ShouldBe(1); // G
        pattern[1, 1].ShouldBe(2); // B
    }
}
