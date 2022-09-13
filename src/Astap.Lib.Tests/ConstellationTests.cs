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
        [InlineData(Constellation.Serpens, "Ser")]
        [InlineData(Constellation.SerpensCaput, "Se1")]
        [InlineData(Constellation.SerpensCauda, "Se2")]
        [InlineData(Constellation.Telescopium, "Tel")]
        [InlineData(Constellation.Vela, "Vel")]
        public void GivenAConstellationWhenGettingIAUAbbreviationThenItIsReturned(Constellation constellation, string expectedAbbreviation)
        {
            constellation.ToIAUAbbreviation().ShouldBe(expectedAbbreviation);
        }

        [Theory]
        [InlineData(Constellation.Andromeda, "Andromeda")]
        [InlineData(Constellation.Antlia, "Antlia")]
        [InlineData(Constellation.Apus, "Apus")]
        [InlineData(Constellation.CanesVenatici, "Canes Venatici")]
        [InlineData(Constellation.CanisMajor, "Canis Major")]
        [InlineData(Constellation.Telescopium, "Telescopium")]
        [InlineData(Constellation.Vela, "Vela")]
        public void GivenAConstellationWhenGettingTheNameThenItIsReturned(Constellation constellation, string expectedName)
        {
            constellation.ToName().ShouldBe(expectedName);
        }

        [Theory]
        [InlineData(Constellation.Andromeda, "Andromedae")]
        [InlineData(Constellation.Antlia, "Antliae")]
        [InlineData(Constellation.Apus, "Apodis")]
        [InlineData(Constellation.CanesVenatici, "Canum Venaticorum")]
        [InlineData(Constellation.CanisMajor, "Canis Majoris")]
        [InlineData(Constellation.Serpens, "Serpentis")]
        [InlineData(Constellation.SerpensCaput, "Serpentis")]
        [InlineData(Constellation.SerpensCauda, "Serpentis")]
        [InlineData(Constellation.Telescopium, "Telescopii")]
        [InlineData(Constellation.Vela, "Velorum")]
        public void GivenAConstellationWhenGettingTheGenitiveThenItIsReturned(Constellation constellation, string expectedGenitive)
        {
            constellation.ToGenitive().ShouldBe(expectedGenitive);
        }

        [Theory]
        [InlineData(Constellation.Andromeda, Constellation.Andromeda, true)]
        [InlineData(Constellation.Carina, Constellation.Telescopium, false)]
        [InlineData(Constellation.Serpens, Constellation.Serpens, true)]
        [InlineData(Constellation.SerpensCaput, Constellation.Serpens, true)]
        [InlineData(Constellation.SerpensCauda, Constellation.Serpens, true)]
        [InlineData(Constellation.SerpensCaput, Constellation.Hercules, false)]
        [InlineData(Constellation.SerpensCauda, Constellation.Hercules, false)]
        public void GivenAConstellationWhenTestingIfContainedWithinThenAThruthvalueIsReturned(Constellation constellation, Constellation parent, bool expectedIsContained)
        {
            constellation.IsContainedWithin(parent).ShouldBe(expectedIsContained);
        }
    }
}
