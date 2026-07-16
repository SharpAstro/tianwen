using Shouldly;
using System;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class AdcResolutionTests
{
    [Theory]
    [InlineData(8, 255L)]
    [InlineData(10, 1023L)]
    [InlineData(12, 4095L)]
    [InlineData(14, 16383L)] // ASI533MC Pro / IMX533 -- the case DALCameraDriver.MaxADU used to collapse to 65535
    [InlineData(16, 65535L)]
    [InlineData(32, 4294967295L)]
    public void FullScaleAdu_IsTwoToTheBitsMinusOne(int bits, long expected)
    {
        new AdcResolution(bits).FullScaleAdu.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    public void FullScaleAdu_ThrowsOutsideTheSupportedRange(int bits)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AdcResolution(bits).FullScaleAdu);
    }
}
