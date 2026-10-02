using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The body a capture shows, read off its path (<see cref="PlanetaryCaptureName"/>), on the shapes the corpus's own captures have:
/// SharpCap's target folders, FireCapture's abbreviations inside a file name, PIPP's output folders, a year's folder around them.
/// </summary>
public class PlanetaryCaptureNameTests
{
    [Theory]
    [InlineData("D:/SharpCap Captures/Jupiter 4ms 2x RGB/Light/Crop/2022-09-29-1136_7-2022-09-29-1137_2_pipp.ser", "Jupiter")]
    [InlineData("D:/Astro-Pics/2023/FC/Jup/020723/pipp_20230704_153141/2023-07-02-0728_9-U-G-Jup_pipp.ser", "Jupiter")]
    [InlineData("D:/Astro-Pics/2022/Saturn/Light/2021-12-16-1119_3_.ser", "Saturn")]
    [InlineData("D:/Astro-Pics/2025/Mars/2025-01-02/Light/13_18_43Z_.ser", "Mars")]
    [InlineData("D:/Astro-Pics/2021/Venus LUM/Light/2021-11-08-0855_5_.ser", "Venus")]
    [InlineData("C:/captures/2022-10-09-1124_8_Sat_pipp.ser", "Saturn")]
    public void APathNamesItsPlanet(string path, string planet)
    {
        PlanetaryCaptureName.Planet(path).ShouldBe(System.Enum.Parse<CatalogIndex>(planet));
    }

    [Fact]
    public void TheNameNearestTheFileWins()
    {
        // A Saturn capture filed under a Jupiter folder is Saturn.
        PlanetaryCaptureName.Planet("D:/Astro-Pics/Jupiter/2021-12-16/Saturn_1119_3.ser").ShouldBe(CatalogIndex.Saturn);
    }

    [Theory]
    [InlineData("D:/Astro-Pics/2021/2021-08-14/Capture/23_12_01_pipp/23_12_01_pipp.ser")]
    [InlineData("C:/data/satellite/jupiterlike_test.ser")]
    public void APathNamingNoPlanetNamesNone(string path)
    {
        // A word is matched whole: "satellite" is not Saturn, nor "jupiterlike" Jupiter.
        PlanetaryCaptureName.Planet(path).ShouldBeNull();
    }

    [Theory]
    [InlineData("Jupiter", "Jupiter")]
    [InlineData("Saturn (Cassini Division)", "Saturn")]
    [InlineData("moon", "Moon")]
    public void AnObjectCardNamesItsPlanet(string objectName, string planet)
    {
        // A FITS OBJECT: SharpCap's target, a planetary stack's own.
        PlanetaryCaptureName.Named(objectName).ShouldBe(System.Enum.Parse<CatalogIndex>(planet));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NGC 7000")]
    [InlineData("M 42")]
    public void AnObjectCardNamingNoPlanetNamesNone(string? objectName)
    {
        PlanetaryCaptureName.Named(objectName).ShouldBeNull();
    }

    [Theory]
    [InlineData("D:/Astro-Pics/2022/2022-09-03-1211_1_Red.ser", 650.0)]
    [InlineData("C:/captures/2023-07-02-0728_9-U-G-Jup_pipp.ser", 530.0)]
    [InlineData("C:/captures/Jup_IR_0730.ser", 750.0)]
    [InlineData("C:/captures/Saturn_L_2021.ser", 550.0)]
    public void AFileNameNamesItsFilter(string path, double nm)
    {
        PlanetaryCaptureName.WavelengthNm(path).ShouldBe(nm);
    }

    [Theory]
    [InlineData("C:/temp/synthetic/r8/calibrated.ser")]
    [InlineData("D:/Astro-Pics/Red/2022-09-03-1211_1_.ser")]
    public void AFileNameNamingNoFilterNamesNone(string path)
    {
        // The file name only: a folder's filter word is too often a session's, not this capture's.
        PlanetaryCaptureName.WavelengthNm(path).ShouldBeNull();
    }
}
