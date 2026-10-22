using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A focuser's travel runs from <see cref="IFocuserDriver.MinStep"/> to <see cref="IFocuserDriver.MaxStep"/>: 0 by
/// default, below zero for a focuser whose counter goes negative (a QHY QFOC, #1763), and a MaxStep of 0 or less is
/// no upper bound, as every caller read it before.
/// </summary>
public class FocuserTravelTests
{
    private static IFocuserDriver Focuser(int minStep, int maxStep)
    {
        var focuser = Substitute.For<IFocuserDriver>();
        focuser.MinStep.Returns(minStep);
        focuser.MaxStep.Returns(maxStep);
        return focuser;
    }

    [Theory]
    [InlineData(0, 2000, -1, false)]
    [InlineData(0, 2000, 0, true)]
    [InlineData(0, 2000, 2000, true)]
    [InlineData(0, 2000, 2001, false)]
    [InlineData(-1_000_000, 1_000_000, -559, true)]
    [InlineData(-1_000_000, 1_000_000, -1_000_001, false)]
    [InlineData(0, 0, 123_456, true)]
    [InlineData(0, -1, -5, false)]
    public void APositionIsWithinTravelBetweenTheFloorAndTheCeiling(int minStep, int maxStep, int position, bool within)
        => Focuser(minStep, maxStep).IsWithinTravel(position).ShouldBe(within);

    [Theory]
    [InlineData(0, 2000, -15, 0)]
    [InlineData(0, 2000, 2500, 2000)]
    [InlineData(-1_000_000, 1_000_000, -15, -15)]
    [InlineData(-1000, 2000, -1500, -1000)]
    [InlineData(0, 0, 99_999, 99_999)]
    public void AClampStopsAtTheFocusersOwnFloorNotAtZero(int minStep, int maxStep, int position, int clamped)
        => Focuser(minStep, maxStep).ClampToTravel(position).ShouldBe(clamped);

    [Fact]
    public void TheTravelIsSaidAsTheFocuserHasIt()
    {
        Focuser(0, 2000).TravelInWords.ShouldBe("between 0 and 2000");
        Focuser(-1_000_000, 1_000_000).TravelInWords.ShouldBe("between -1000000 and 1000000");
        Focuser(0, 0).TravelInWords.ShouldBe("from 0");
    }
}
