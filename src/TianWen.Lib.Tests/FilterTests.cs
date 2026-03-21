using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public sealed class FilterTests
{
    [Theory]
    [InlineData("Luminance", "Luminance")]
    [InlineData("L", "Luminance")]
    [InlineData("LUM", "Luminance")]
    [InlineData("Red", "Red")]
    [InlineData("R", "Red")]
    [InlineData("Green", "Green")]
    [InlineData("G", "Green")]
    [InlineData("Blue", "Blue")]
    [InlineData("B", "Blue")]
    [InlineData("H-Alpha", "HydrogenAlpha")]
    [InlineData("Ha", "HydrogenAlpha")]
    [InlineData("HAlpha", "HydrogenAlpha")]
    [InlineData("H-Beta", "HydrogenBeta")]
    [InlineData("Hb", "HydrogenBeta")]
    [InlineData("OIII", "OxygenIII")]
    [InlineData("O3", "OxygenIII")]
    [InlineData("SII", "SulphurII")]
    [InlineData("S2", "SulphurII")]
    public void FromName_SingleBand_ResolvesCorrectly(string input, string expectedName)
    {
        Filter.FromName(input).Name.ShouldBe(expectedName);
    }

    [Theory]
    [InlineData("H-Alpha + OIII")]
    [InlineData("HAlpha+OIII")]
    [InlineData("Ha+OIII")]
    [InlineData("HydrogenAlphaOxygenIII")]
    public void FromName_DualBand_HAlphaOIII_ResolvesCorrectly(string input)
    {
        var filter = Filter.FromName(input);
        filter.ShouldBe(Filter.HydrogenAlphaOxygenIII);
        filter.DisplayName.ShouldBe("H-Alpha + OIII");
    }

    [Theory]
    [InlineData("SII + OIII")]
    [InlineData("SII+OIII")]
    [InlineData("S2+OIII")]
    [InlineData("SulphurIIOxygenIII")]
    public void FromName_DualBand_SIIOIII_ResolvesCorrectly(string input)
    {
        var filter = Filter.FromName(input);
        filter.ShouldBe(Filter.SulphurIIOxygenIII);
        filter.DisplayName.ShouldBe("SII + OIII");
    }

    [Theory]
    [InlineData("None")]
    [InlineData("NONE")]
    public void FromName_None_ResolvesCorrectly(string input)
    {
        Filter.FromName(input).ShouldBe(Filter.None);
    }

    [Fact]
    public void FromName_Null_ReturnsNone()
    {
        Filter.FromName(null!).ShouldBe(Filter.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("FooBar")]
    [InlineData("XYZ")]
    [InlineData("L-Quad Enhance")]
    [InlineData("Optolong L-Ultimate")]
    [InlineData("L-eXtreme")]
    [InlineData("IDAS NBZ")]
    public void FromName_Unknown_ReturnsUnknown(string input)
    {
        Filter.FromName(input).ShouldBe(Filter.Unknown);
    }

    [Fact]
    public void DisplayName_MatchesCommonNames()
    {
        Filter.Luminance.DisplayName.ShouldBe("Luminance");
        Filter.Red.DisplayName.ShouldBe("Red");
        Filter.Green.DisplayName.ShouldBe("Green");
        Filter.Blue.DisplayName.ShouldBe("Blue");
        Filter.HydrogenAlpha.DisplayName.ShouldBe("H-Alpha");
        Filter.HydrogenBeta.DisplayName.ShouldBe("H-Beta");
        Filter.OxygenIII.DisplayName.ShouldBe("OIII");
        Filter.SulphurII.DisplayName.ShouldBe("SII");
        Filter.HydrogenAlphaOxygenIII.DisplayName.ShouldBe("H-Alpha + OIII");
        Filter.SulphurIIOxygenIII.DisplayName.ShouldBe("SII + OIII");
    }

    [Fact]
    public void ShortName_UsesAbbreviations()
    {
        Filter.Luminance.ShortName.ShouldBe("L");
        Filter.Red.ShortName.ShouldBe("R");
        Filter.HydrogenAlpha.ShortName.ShouldBe("H\u03B1");
        Filter.HydrogenBeta.ShortName.ShouldBe("H\u03B2");
        Filter.HydrogenAlphaOxygenIII.ShortName.ShouldBe("H\u03B1+OIII");
    }

    [Theory]
    [InlineData("H\u03B1", "HydrogenAlpha")]
    [InlineData("H\u03B2", "HydrogenBeta")]
    [InlineData("H\u03B1+OIII", "HydrogenAlphaOxygenIII")]
    public void FromName_UnicodeGreekLetters_ResolvesCorrectly(string input, string expectedName)
    {
        Filter.FromName(input).Name.ShouldBe(expectedName);
    }

    [Theory]
    [MemberData(nameof(AllKnownFilters))]
    public void FromName_Roundtrip_Name(Filter filter)
    {
        if (filter == Filter.None || filter == Filter.Unknown) return;
        Filter.FromName(filter.Name).ShouldBe(filter);
    }

    [Theory]
    [MemberData(nameof(AllKnownFilters))]
    public void FromName_Roundtrip_DisplayName(Filter filter)
    {
        if (filter == Filter.None || filter == Filter.Unknown) return;
        Filter.FromName(filter.DisplayName).ShouldBe(filter);
    }

    [Theory]
    [MemberData(nameof(AllKnownFilters))]
    public void FromName_Roundtrip_ShortName(Filter filter)
    {
        if (filter == Filter.None || filter == Filter.Unknown) return;
        Filter.FromName(filter.ShortName).ShouldBe(filter);
    }

    public static TheoryData<Filter> AllKnownFilters => new()
    {
        Filter.Luminance,
        Filter.Red,
        Filter.Green,
        Filter.Blue,
        Filter.HydrogenAlpha,
        Filter.HydrogenBeta,
        Filter.OxygenIII,
        Filter.SulphurII,
        Filter.HydrogenAlphaOxygenIII,
        Filter.SulphurIIOxygenIII,
    };
}
