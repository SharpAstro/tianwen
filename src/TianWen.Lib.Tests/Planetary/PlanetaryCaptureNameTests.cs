using System;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
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

    private static readonly DateTimeOffset Utc = new DateTimeOffset(2022, 9, 3, 12, 11, 8, TimeSpan.Zero);

    [Fact]
    public void ARecordingsNameSaysItsPlanetAndFilterAndReadsBack()
    {
        // A TianWen recording names what it was taken of and through (#1179), the words the reader takes back.
        var name = PlanetaryCaptureName.RecordingFileName(CatalogIndex.Jupiter, "Red", Utc, otaIndex: 0);
        name.ShouldBe("Jupiter_Red_2022-09-03T12_11_08_OTA1.ser");
        var path = "C:/Planetary/2022-09-03/" + name;
        PlanetaryCaptureName.Planet(path).ShouldBe(CatalogIndex.Jupiter);
        PlanetaryCaptureName.WavelengthNm(path).ShouldBe(650);

        // What is not known is left out, and a filter's name keeps its letters and digits.
        PlanetaryCaptureName.RecordingFileName(null, null, Utc, otaIndex: 1).ShouldBe("2022-09-03T12_11_08_OTA2.ser");
        PlanetaryCaptureName.RecordingFileName(CatalogIndex.Saturn, "IR 685/nm", Utc, otaIndex: 0).ShouldBe("Saturn_IR685nm_2022-09-03T12_11_08_OTA1.ser");
    }

    [Fact]
    public void AMountOnAPlanetNamesItAndOneOffItNamesNone()
    {
        // Jupiter where it stood on 2022-09-03 from 48.2 N, 16.3 E, of date (as most mounts report) and in J2000.
        VSOP87a.Reduce(CatalogIndex.Jupiter, Utc, 48.2, 16.3, out var ra, out var dec, out _, out _, out _).ShouldBeTrue();
        VSOP87a.ReduceJ2000(CatalogIndex.Jupiter, Utc, out var raJ2000, out var decJ2000, out _).ShouldBeTrue();
        PlanetaryCaptureName.PointedAt(ra, dec, Utc, 48.2, 16.3).ShouldBe(CatalogIndex.Jupiter);
        PlanetaryCaptureName.PointedAt(raJ2000, decJ2000, Utc, 48.2, 16.3).ShouldBe(CatalogIndex.Jupiter);
        PlanetaryCaptureName.PointedAt(ra, dec + 0.5, Utc, 48.2, 16.3).ShouldBe(CatalogIndex.Jupiter, "half a degree off, the mount's own pointing");
        PlanetaryCaptureName.PointedAt(ra, dec + 5, Utc, 48.2, 16.3).ShouldBeNull("five degrees off is no planet");
        PlanetaryCaptureName.PointedAt(double.NaN, dec, Utc, 48.2, 16.3).ShouldBeNull("a mount that did not answer");
    }

    [Fact]
    public void ATelescopeFieldSaysTheOpticsAndReadsBack()
    {
        var ota = new OTAData("SW 250PDS", 1200, new Uri("camera://fake/1"), null, null, null, null, null, Aperture: 254,
            OpticalDesign: OpticalDesign.Newtonian);
        var field = PlanetaryCaptureName.TelescopeField(ota);
        field.ShouldBe("254 mm f/4.7 Newtonian, SW 250PDS");
        field.Length.ShouldBeLessThanOrEqualTo(PlanetaryCaptureName.SerFieldLength);
        PlanetaryCaptureName.Telescope(field).ShouldBe(((int?)254, OpticalDesign.Newtonian));

        // A long name is cut at the field's length, a typed one is read too, and one naming no aperture reads none.
        PlanetaryCaptureName.TelescopeField(ota with { Name = new string('x', 60) }).Length.ShouldBe(PlanetaryCaptureName.SerFieldLength);
        PlanetaryCaptureName.Telescope("C11 SCT 280mm").ShouldBe(((int?)280, OpticalDesign.SCT));
        PlanetaryCaptureName.Telescope("TianWen").ShouldBe(((int?)null, OpticalDesign.Unknown));
        PlanetaryCaptureName.TelescopeField(ota with { Aperture = null }).ShouldBe("SW 250PDS");
    }
}
