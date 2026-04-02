using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public class OpticalDesignTests
{
    [Theory]
    [InlineData(OpticalDesign.Newtonian, false)]
    [InlineData(OpticalDesign.Cassegrain, false)]
    [InlineData(OpticalDesign.RASA, false)]
    [InlineData(OpticalDesign.Astrograph, false)]
    [InlineData(OpticalDesign.Refractor, true)]
    [InlineData(OpticalDesign.SCT, true)]
    [InlineData(OpticalDesign.NewtonianCassegrain, true)]
    [InlineData(OpticalDesign.Unknown, true)]
    public void NeedsFocusAdjustmentPerFilter_ReturnsExpected(OpticalDesign design, bool expected)
    {
        design.NeedsFocusAdjustmentPerFilter.ShouldBe(expected);
    }
}
