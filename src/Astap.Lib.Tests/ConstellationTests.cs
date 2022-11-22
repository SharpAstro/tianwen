using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Catalogs;
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
        [InlineData(Constellation.Aquarius, "Aqr")]
        [InlineData(Constellation.Aquila, "Aql")]
        [InlineData(Constellation.Ara, "Ara")]
        [InlineData(Constellation.Aries, "Ari")]
        [InlineData(Constellation.Auriga, "Aur")]
        [InlineData(Constellation.Bootes, "Boo")]
        [InlineData(Constellation.Caelum, "Cae")]
        [InlineData(Constellation.ComaBerenices, "Com")]
        [InlineData(Constellation.Lyra, "Lyr")]
        [InlineData(Constellation.Lynx, "Lyn")]
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
        public void GivenAConstellationWhenToGenitiveThenItIsReturned(Constellation constellation, string expectedGenitive)
        {
            constellation.ToGenitive().ShouldBe(expectedGenitive);
        }

        [Theory]
        [InlineData(Constellation.Andromeda, "Alpheratz")]
        [InlineData(Constellation.Antlia, "α Antliae")]
        [InlineData(Constellation.Apus, "α Apodis")]
        [InlineData(Constellation.CanesVenatici, "Cor Caroli")]
        [InlineData(Constellation.CanisMajor, "Sirius")]
        [InlineData(Constellation.Centaurus, "Rigil Kentaurus")]
        [InlineData(Constellation.Serpens, "Unukalhai")]
        [InlineData(Constellation.SerpensCaput, "Unukalhai")]
        [InlineData(Constellation.SerpensCauda, "η Serpentis")]
        [InlineData(Constellation.Telescopium, "α Telescopii")]
        [InlineData(Constellation.Vela, "γ2 Velorum")]
        public void GivenAConstellationWhenToBrightestStarThenItIsReturned(Constellation constellation, string expectedGenitive)
        {
            constellation.ToBrighestStarName().ShouldBe(expectedGenitive);
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
