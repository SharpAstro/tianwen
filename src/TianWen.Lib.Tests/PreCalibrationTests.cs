using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using nom.tam.fits;
using nom.tam.util;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using Xunit;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Tests;

/// <summary>
/// The CALSTAT read guard (#466): a light whose own header says a correction was already applied is never
/// given that correction again. The case in hand is iTelescope's pre-calibrated lights (MaxIm DL,
/// <c>CALSTAT='BDF'</c>), which a matching dark or flat in the archive would otherwise calibrate twice.
/// </summary>
[Collection("Imaging")]
public sealed class PreCalibrationTests : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    [Theory]
    [InlineData("BDF", CalibrationApplied.Bias | CalibrationApplied.Dark | CalibrationApplied.Flat)]
    [InlineData("bd", CalibrationApplied.Bias | CalibrationApplied.Dark)]
    [InlineData(" F ", CalibrationApplied.Flat)]
    [InlineData("Q", CalibrationApplied.None)]
    [InlineData("", CalibrationApplied.None)]
    [InlineData(null, CalibrationApplied.None)]
    public void CalStatLettersNameTheAppliedCorrections(string? calStat, CalibrationApplied expected)
        => PreCalibration.Parse(calStat).ShouldBe(expected);

    [Fact]
    public void TheLettersComeBackInTheirOwnOrder()
        => PreCalibration.Letters(PreCalibration.Parse("fdb")).ShouldBe("BDF");

    /// <summary>Both read paths take the card, since both go through the one header parse.</summary>
    [Fact]
    public void BothReadPathsReadCalStat()
    {
        var path = Path.Combine(_folders.Create("calstat").FullName, "precal.fits");
        var fits = new Fits();
        var hdu = FitsFactory.HDUFactory(new float[4, 4]);
        hdu.Header.AddValue("DATE-OBS", FitsDate.GetFitsDateString(new DateTime(2022, 1, 4, 1, 8, 24, DateTimeKind.Utc)), "UT");
        hdu.Header.AddValue("IMAGETYP", "Light Frame", "");
        hdu.Header.AddValue("CALSTAT", "BDF", "MaxIm: bias, dark and flat applied");
        fits.AddHDU(hdu);
        using (var bf = new BufferedFile(path, FileAccess.ReadWrite, FileShare.Read, 16 * 2880))
        {
            fits.Write(bf);
            bf.Flush();
        }

        Image.TryReadFitsHeader(path, out var info).ShouldBeTrue();
        info.ShouldNotBeNull().Meta.PreCalibrated
            .ShouldBe(CalibrationApplied.Bias | CalibrationApplied.Dark | CalibrationApplied.Flat);
        Image.TryReadFitsFile(path, out var image).ShouldBeTrue();
        image.ShouldNotBeNull().ImageMeta.PreCalibrated.ShouldBe(info.Meta.PreCalibrated);
    }

    private static FrameInfo Frame(FrameType type, double exposureSec, string calStat = "")
    {
        var meta = new ImageMeta(
            Instrument: "SBIG STL-6303",
            ExposureStartTime: new DateTimeOffset(2021, 12, 29, 20, 46, 11, TimeSpan.Zero),
            ExposureDuration: TimeSpan.FromSeconds(exposureSec),
            FrameType: type,
            Telescope: "T18",
            PixelSizeX: 9f,
            PixelSizeY: 9f,
            FocalLength: 2541,
            FocusPos: FocuserPositions.None,
            Filter: Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: -20,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN)
        { PreCalibrated = PreCalibration.Parse(calStat) };
        return new FrameInfo("x.fits", 100, 100, 1, BitDepth.Float32, meta);
    }

    private static ImagingSession Session(params string[] calStats)
        => new("root", "night", "SBIG STL-6303", "M 33", "",
            [.. calStats.Select(c => Frame(FrameType.Light, 180, c))]);

    /// <summary>A library holding a dark and a flat that WOULD match these lights.</summary>
    private static IReadOnlyDictionary<FrameType, List<CalibrationResolver.CalGroup>> Library()
        => CalibrationResolver.GroupCalibration(
        [
            Frame(FrameType.Dark, 180), Frame(FrameType.Dark, 180),
            Frame(FrameType.Flat, 2), Frame(FrameType.Flat, 2),
        ]);

    [Fact]
    public void AnUncalibratedSessionTakesTheMatchingDarkAndFlat()
    {
        var choice = CalibrationResolver.Choose(Session("", ""), Library());

        choice.Dark.ShouldNotBeNull("the control: without CALSTAT the library's dark matches");
        choice.Flat.ShouldNotBeNull();
    }

    [Fact]
    public void ACalibratedSessionTakesNeitherAgain()
    {
        var choice = CalibrationResolver.Choose(Session("BDF", "BDF"), Library());

        choice.Dark.ShouldBeNull();
        choice.Flat.ShouldBeNull();
        CalibrationResolver.ProvenanceWithoutMasters(Session("BDF", "BDF"))
            .ShouldBe(new CalibrationProvenance(PreCalibrated: "BDF"));
    }

    /// <summary>Per correction: lights that arrive dark-subtracted but not flat-fielded still get the flat.</summary>
    [Fact]
    public void OnlyTheCorrectionsAlreadyAppliedAreSkipped()
    {
        var choice = CalibrationResolver.Choose(Session("BD", "BD"), Library());

        choice.Dark.ShouldBeNull();
        choice.Flat.ShouldNotBeNull();
    }

    /// <summary>
    /// Lights that disagree take the union: applying a correction to the light that already carries it is
    /// the worse error, so it is skipped for all and the disagreement is reported.
    /// </summary>
    [Fact]
    public void LightsThatDisagreeTakeTheUnionAndSaySo()
    {
        var applied = PreCalibration.Across(Session("BDF", "").Lights, out var mixed);

        applied.ShouldBe(CalibrationApplied.Bias | CalibrationApplied.Dark | CalibrationApplied.Flat);
        mixed.ShouldBeTrue();
        CalibrationResolver.Choose(Session("BDF", ""), Library()).Dark.ShouldBeNull();
    }

    [Fact]
    public void AnUncalibratedSessionRecordsNoProvenance()
        => CalibrationResolver.ProvenanceWithoutMasters(Session("", "")).IsEmpty.ShouldBeTrue();
}
