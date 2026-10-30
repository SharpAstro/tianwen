using System;
using Shouldly;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="GuideDarkCorrection"/> on synthetic guide frames (#1915): the matching dark is subtracted, the nearest exposure
/// is chosen and scaled about its pedestal, a defect the darks show is masked and so never the guide star, and a frame no
/// dark or map serves passes through untouched.
/// </summary>
[Collection("Guider")]
public class GuideDarkCorrectionTests
{
    private const float FullScale = 65535f;
    private const int Width = 160;
    private const int Height = 120;
    private const float Pedestal = 500f;

    private static float[,] Flat(float level)
    {
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // A deterministic wobble, so the mask's noise floor is real and not zero.
                plane[y, x] = level + ((x * 7 + y * 13) % 5 - 2);
            }
        }
        return plane;
    }

    private static Image ImageOf(float[,] plane) => new Image([plane], BitDepth.Int16, FullScale, 0f, 0f, new ImageMeta());

    private static GuideDark DarkOf(double seconds, float level, params (int X, int Y, float Value)[] hot)
    {
        var plane = Flat(level);
        foreach (var (x, y, value) in hot)
        {
            plane[y, x] = value;
        }
        return new GuideDark(TimeSpan.FromSeconds(seconds), -1, double.NaN, ImageOf(plane));
    }

    private static GuideDarkCorrection Library(params GuideDark[] darks) => GuideDarkCorrection.FromDarks([.. darks]);

    [Fact]
    public void TheMatchingDarkIsSubtractedFromTheFrameInANewImage()
    {
        var correction = Library(DarkOf(1, Pedestal + 100));
        var light = Flat(Pedestal + 100);
        light[60, 80] += 3000;
        var frame = ImageOf(light);

        var corrected = correction.Correct(frame, TimeSpan.FromSeconds(1));

        corrected.ShouldNotBeSameAs(frame);
        var plane = corrected.GetChannel(0).Data;
        plane[60, 80].ShouldBe(3000, 1.0);
        plane[10, 10].ShouldBe(0, 1.0, "the dark's own level is gone");
        light[60, 80].ShouldBe(Pedestal + 100 + 3000 + ((80 * 7 + 60 * 13) % 5 - 2), 0.01, "the camera's plane is untouched");
    }

    [Fact]
    public void TheNearestExposureServesAndIsScaledAboutThePedestalTheLibraryImplies()
    {
        // 1 s has 100 of dark current over a pedestal of 500; 8 s has 800. A 2 s frame is nearer 1 s (a factor of 2 against 4).
        var correction = Library(DarkOf(1, Pedestal + 100), DarkOf(8, Pedestal + 800));
        correction.Find(TimeSpan.FromSeconds(2), -1, double.NaN, Width, Height)!.Exposure.ShouldBe(TimeSpan.FromSeconds(1));

        var corrected = correction.Correct(ImageOf(Flat(Pedestal + 200)), TimeSpan.FromSeconds(2));

        corrected.GetChannel(0).Data[30, 30].ShouldBe(0, 2.5, "100 of dark current at 1 s is 200 at 2 s, on a pedestal that does not scale");
    }

    [Fact]
    public void ADarkOfAnotherGainOrBeyondTheExposureRangeDoesNotServe()
    {
        var gain100 = new GuideDark(TimeSpan.FromSeconds(1), 100, double.NaN, ImageOf(Flat(Pedestal)));
        var correction = new GuideDarkCorrection([gain100], null);

        correction.Find(TimeSpan.FromSeconds(1), 100, double.NaN, Width, Height).ShouldNotBeNull();
        correction.Find(TimeSpan.FromSeconds(1), 50, double.NaN, Width, Height).ShouldBeNull("the gain must agree where both are known");
        correction.Find(TimeSpan.FromSeconds(1), -1, double.NaN, Width, Height).ShouldNotBeNull("an unknown gain matches any");
        correction.Find(TimeSpan.FromSeconds(10), 100, double.NaN, Width, Height).ShouldBeNull("ten times the exposure is beyond the scaling range");
    }

    [Fact]
    public void ABadPixelTheDarksShowIsMaskedAndNeverTheGuideStar()
    {
        // The darks are 1 s; the light is 10 s, beyond the scaling range, so only the map serves it.
        var correction = Library(DarkOf(1, Pedestal, (40, 30, 60000f)));
        correction.MaskedPixels.ShouldBeGreaterThan(0);

        var light = Flat(Pedestal);
        light[30, 40] = 60000f;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var dx = x - 120.0;
                var dy = y - 80.0;
                if (dx * dx + dy * dy < 100)
                {
                    light[y, x] += (float)(6000 * Math.Exp(-(dx * dx + dy * dy) / (2 * 2.0 * 2.0)));
                }
            }
        }

        var corrected = correction.Correct(ImageOf(light), TimeSpan.FromSeconds(10));

        corrected.GetChannel(0).Data[30, 40].ShouldBeLessThan(Pedestal + 10, "the defect takes its neighbours' level");
        var read = GuideStarReadout.Measure(corrected);
        read.ShouldNotBeNull();
        read.Value.X.ShouldBe(120, 0.5);
        read.Value.Y.ShouldBe(80, 0.5);
    }

    [Fact]
    public void WithNoLibraryOrAFrameOfAnotherShapeTheFrameIsReturnedUntouched()
    {
        var frame = ImageOf(Flat(Pedestal));

        new GuideDarkCorrection([], null).Correct(frame, TimeSpan.FromSeconds(1)).ShouldBeSameAs(frame);

        var otherShape = new Image([new float[Height / 2, Width / 2]], BitDepth.Int16, FullScale, 0f, 0f, new ImageMeta());
        Library(DarkOf(1, Pedestal)).Correct(otherShape, TimeSpan.FromSeconds(1)).ShouldBeSameAs(otherShape);
    }

    [Fact]
    public void ALibraryOfOneExposureNeverScalesItsPedestalToAnotherExposure()
    {
        // 1 s: pedestal 500 plus 100 of dark current. At 3 s only a second exposure could say what part of it scales, so the
        // frame is masked alone: the sky is left as it is, and a star fainter than the pedestal is not erased.
        var correction = Library(DarkOf(1, Pedestal + 100));
        var light = Flat(Pedestal + 300);
        light[60, 80] += 3000;

        var corrected = correction.Correct(ImageOf(light), TimeSpan.FromSeconds(3));

        corrected.GetChannel(0).Data[60, 80].ShouldBe(Pedestal + 300 + 3000, 3.0, "no dark was taken off, so nothing under-reads the star");

        var own = correction.Correct(ImageOf(light), TimeSpan.FromSeconds(1));
        own.GetChannel(0).Data[60, 80].ShouldBe(Pedestal + 300 - (Pedestal + 100) + 3000, 3.0, "a dark of the frame's own exposure serves");
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(80, 60)]
    [InlineData(200, 120)]
    public void AMapOfOneSizeIsNeverAppliedToAFrameOfAnother(int width, int height)
    {
        var correction = Library(DarkOf(1, Pedestal, (40, 30, 60000f)));
        correction.MaskedPixels.ShouldBeGreaterThan(0);

        var plane = new float[height, width];
        Array.Fill(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length), Pedestal);
        var frame = new Image([plane], BitDepth.Int16, FullScale, 0f, 0f, new ImageMeta());

        correction.Correct(frame, TimeSpan.FromSeconds(1)).ShouldBeSameAs(frame, "neither the dark nor the map has that size");
    }
}
