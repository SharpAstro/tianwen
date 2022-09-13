using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests
{
    public class ConstellationTests
    {
        [Theory]
        [InlineData(Constellation.Andromeda, "And")]
        [InlineData(Constellation.Antlia, "Ant")]
        [InlineData(Constellation.Apus, "Aps")]
        [InlineData(Constellation.Telescopium, "Tel")]
        [InlineData(Constellation.Vela, "Vel")]
        public void GivenAConstellationWhenGettingIAUAbbreviationThenItIsReturned(Constellation constellation, string expectedAbbreviation)
        {
            constellation.ToIAUAbbreviation().ShouldBe(expectedAbbreviation);
        }
    }
}
