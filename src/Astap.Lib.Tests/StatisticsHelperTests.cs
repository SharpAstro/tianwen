using Astap.Lib.Stat;
using Shouldly;
using System;
using Xunit;

namespace Astap.Lib.Tests;

public class StatisticsHelperTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(42f, 42f)]
    [InlineData(5f, 1f, 4f, 5f, 7f, 9f)]
    [InlineData(5.5f, 1f, 5f, 6f, 9f)]
    [InlineData(8f, 8f, 9f, 7f, 10f, 6.5f)]
    public void GivenValuesWhenCalcMedianThenItIsReturned(float expectedMedian, params float[] values)
    {
        StatisticsHelper.Median(values).ShouldBe(expectedMedian);
    }

    [Theory]
    [InlineData(20u, -120, -40, -60)]
    [InlineData(1u, 1)]
    [InlineData(0u, 0)]
    [InlineData(0u, 0, 0)]
    [InlineData(4u, 8, 12, 24)]
    [InlineData(20u, 120, 40, 60)]
    public void GivenValuesWhenCalcGCDThenItIsReturned(uint expectedGCD, int first, params int[] rest)
    {
        StatisticsHelper.GCD(first, rest).ShouldBe(expectedGCD);
    }

    [Theory]
    [InlineData(14400uL, -120, -40, -60)]
    [InlineData(1u, 1)]
    [InlineData(0u, 0)]
    [InlineData(0u, 0, 0)]
    [InlineData(576uL, 8, 12, 24)]
    [InlineData(120uL, 120, 60)]
    [InlineData(14400uL, 120, 40, 60)]
    public void GivenValuesWhenCalcLCMThenItIsReturned(uint expectedLCM, int first, params int[] rest)
    {
        StatisticsHelper.LCM(first, rest).ShouldBe(expectedLCM);
    }
}
