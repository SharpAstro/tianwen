using Shouldly;
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
}
