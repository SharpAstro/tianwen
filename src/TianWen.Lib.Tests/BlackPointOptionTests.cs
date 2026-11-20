using Shouldly;
using TianWen.Cli;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>--black-point-sigma</c> (#2309) is read the same way by the command that writes an export at a depth and by the
/// one that runs a model at it: a depth is a number of sigma over 0, and it has to have something to act on.
/// </summary>
public class BlackPointOptionTests
{
    private const string Needs = "it needs the other option";

    [Fact]
    public void NoDepthIsNoRefusalWhateverElseIsGiven()
    {
        BlackPointOption.Refusal(null, applies: true, Needs).ShouldBeNull();
        BlackPointOption.Refusal(null, applies: false, Needs).ShouldBeNull();
    }

    [Theory]
    [InlineData(40.0)]
    [InlineData(0.5)]
    public void ADepthOverZeroIsTakenWhereItApplies(double depth)
    {
        BlackPointOption.Refusal(depth, applies: true, Needs).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ADepthThatIsNotANumberOverZeroIsRefusedFirst(double depth)
    {
        // Said before anything else: the value is wrong on its own, wherever it was given.
        var here = BlackPointOption.Refusal(depth, applies: true, Needs).ShouldNotBeNull();
        var there = BlackPointOption.Refusal(depth, applies: false, Needs).ShouldNotBeNull();

        here.ShouldContain("give a number of sigma over 0");
        there.ShouldBe(here);
    }

    [Fact]
    public void ADepthWithNothingToActOnSaysWhatItNeeds()
    {
        BlackPointOption.Refusal(40, applies: false, Needs).ShouldBe(Needs);
    }
}
