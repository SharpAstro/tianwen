using Shouldly;
using System;
using System.IO;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Where a frame's PIXEL SCALE comes from, and the single metadata parse that decides it.
///
/// <para><c>FOCALLEN</c> is only ever a hint: it carries whatever was typed into a capture profile and
/// nothing validates it. On the 10P/Tempel 2 set it read 205 mm for a 202.5 mm rig -- a 1.2% error the
/// plate solver then had to work against, and one the solver itself detected, recovering 202.4 mm from
/// the stars alone. So a frame that STATES its own scale is the better source, and
/// <see cref="Image.GetImageDim"/> prefers it.</para>
///
/// <para>It could not, before: <c>PIXSCALE</c> was parsed into a local by the pixel-read path and
/// dropped on the floor, and the header-only path never parsed it at all, so a declared scale was
/// unreachable however you opened the file. That is one symptom of a larger defect these tests also
/// pin -- the two read paths were SEPARATE COPIES of the same ~35-card parse, and had drifted.</para>
/// </summary>
[Collection("Imaging")]
public class FitsPixelScaleTests
{
    private static ImageMeta Meta(int focalLength, float declaredPixelScale, TimeSpan? exposure = null,
        float latitude = float.NaN, float longitude = float.NaN, float siteElevation = float.NaN)
        => new(
            Instrument: "Test Camera",
            ExposureStartTime: new DateTimeOffset(2026, 8, 16, 10, 53, 18, TimeSpan.Zero),
            ExposureDuration: exposure ?? TimeSpan.FromSeconds(60),
            FrameType: FrameType.Light,
            Telescope: "SV 545 f4.5",
            PixelSizeX: 4.63f,
            PixelSizeY: 4.63f,
            FocalLength: focalLength,
            FocusPos: -1,
            Filter: Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: float.NaN,
            SensorType: SensorType.Monochrome,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: latitude,
            Longitude: longitude,
            DeclaredPixelScale: declaredPixelScale,
            SiteElevation: siteElevation);

    private static Image ImageWith(ImageMeta meta)
        => new([new float[8, 8]], BitDepth.Int16, maxValue: 100f, minValue: 0f, pedestal: 0f, meta);

    [Fact]
    public void ADeclaredScaleWinsOverTheOneDerivedFromFocalLength()
    {
        // The real numbers from the 10P set: the profile said 205 mm, the solve said 4.7172"/px.
        // Deriving from 205 mm and 4.63 um gives 4.6586, so the two are distinguishable by more than
        // rounding -- which is the point, since a test where they agree would pass either way.
        var declared = ImageWith(Meta(focalLength: 205, declaredPixelScale: 4.7172f)).GetImageDim();

        declared.ShouldNotBeNull();
        declared.Value.PixelScale.ShouldBe(4.7172, tolerance: 1e-4);
    }

    [Fact]
    public void WithNoDeclaredScaleTheFocalLengthIsStillUsedAsTheHint()
    {
        // The fallback is not a leftover: pixel size plus focal length is all most frames ever carry,
        // and an approximate hint is what lets the solver bound its search at all. Only a frame that
        // knows better gets to override it.
        var derived = ImageWith(Meta(focalLength: 205, declaredPixelScale: float.NaN)).GetImageDim();

        derived.ShouldNotBeNull();
        derived.Value.PixelScale.ShouldBe(4.6586, tolerance: 1e-3);
    }

    [Fact]
    public void WithNeitherScaleNorFocalLengthThereIsNoAnswerRatherThanAGuess()
    {
        ImageWith(Meta(focalLength: -1, declaredPixelScale: float.NaN)).GetImageDim().ShouldBeNull();
    }

    [Fact]
    public void TheTwoReadPathsAgreeOnEveryMetadataField()
    {
        // The regression that matters most here, and the one that made the PIXSCALE bug possible.
        // TryReadFitsFile and TryReadFitsHeader were separate copies of the same parse; whenever one
        // learned a card the other did not, a file meant two different things depending on which
        // function opened it -- and the header-only path is what the calibration scan uses, so the
        // divergence lands on which dark calibrates what. Comparing the whole record rather than the
        // fields I happen to suspect is deliberate: a future card added to one path alone fails here
        // without anyone remembering to extend the assertion.
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "two-read-paths.fits");
        ImageWith(Meta(focalLength: 203, declaredPixelScale: float.NaN)).WriteToFitsFile(fitsPath);

        Image.TryReadFitsFile(fitsPath, out var viaPixels).ShouldBeTrue();
        Image.TryReadFitsHeader(fitsPath, out var viaHeader).ShouldBeTrue();

        viaHeader.Meta.ShouldBe(viaPixels!.ImageMeta);
    }

    [Fact]
    public void TheObservingSiteSurvivesTheRoundTripIncludingItsElevation()
    {
        // A TOPOCENTRIC ephemeris is stated for a PLACE, so comet-aligned registration reads all
        // three site cards back off the frames. SITEELEV was the one with no home in ImageMeta: the
        // header carried it and the Horizons query asked for it, but the read path dropped it, so the
        // query would have been answered at sea level from a frame that knew better.
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "observing-site.fits");
        ImageWith(Meta(focalLength: 203, declaredPixelScale: float.NaN,
                latitude: -37.876389f, longitude: 145.178056f, siteElevation: 120f))
            .WriteToFitsFile(fitsPath);

        Image.TryReadFitsFile(fitsPath, out var viaPixels).ShouldBeTrue();
        Image.TryReadFitsHeader(fitsPath, out var viaHeader).ShouldBeTrue();

        viaPixels!.ImageMeta.Latitude.ShouldBe(-37.876389f, tolerance: 1e-4f);
        viaPixels.ImageMeta.Longitude.ShouldBe(145.178056f, tolerance: 1e-4f);
        viaPixels.ImageMeta.SiteElevation.ShouldBe(120f, tolerance: 1e-3f);
        viaHeader.Meta.SiteElevation.ShouldBe(120f, tolerance: 1e-3f);
    }

    [Fact]
    public void AnUnstatedElevationStaysUnknownRatherThanBecomingSeaLevel()
    {
        // Zero is a perfectly plausible elevation, so defaulting to zero is indistinguishable from a
        // measurement of zero. What to do with the absence is the CONSUMER's policy -- the Horizons
        // query treats it as sea level, deliberately, because it costs under a thousandth of a pixel
        // -- and that policy must not be baked into the file or the read.
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "no-elevation.fits");
        ImageWith(Meta(focalLength: 203, declaredPixelScale: float.NaN)).WriteToFitsFile(fitsPath);

        Image.TryReadFitsFile(fitsPath, out var reread).ShouldBeTrue();

        float.IsNaN(reread!.ImageMeta.SiteElevation).ShouldBeTrue();
    }

    [Fact]
    public void AWrittenScaleSurvivesTheRoundTripAndReachesTheSolverHint()
    {
        // WriteToFitsFile emits SCALE and PIXSCALE from the focal length, so reading our own output
        // back must recover a declared scale rather than silently re-deriving one. 203 mm with 4.63 um
        // is 4.7044"/px.
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "declared-scale.fits");
        ImageWith(Meta(focalLength: 203, declaredPixelScale: float.NaN)).WriteToFitsFile(fitsPath);

        Image.TryReadFitsFile(fitsPath, out var reread).ShouldBeTrue();

        reread!.ImageMeta.DeclaredPixelScale.ShouldBe(4.7044f, tolerance: 1e-3f);
        reread.GetImageDim()!.Value.PixelScale.ShouldBe(4.7044, tolerance: 1e-3);
    }

    [Fact]
    public void AnExposureCardIsReadWhenExptimeIsAbsent()
    {
        // EXPTIME and EXPOSURE are both in the wild for the same quantity. The fallback list read
        // { EXPTIME, EXPTIME, 0 } in BOTH copies of the parse, so EXPOSURE was dead everywhere and a
        // frame carrying only that card read as a ZERO-second exposure -- which is not inert, because
        // exposure is part of MasterGroupKey and so decides which dark calibrates what.
        var testDir = SharedTestData.CreateTempTestOutputDir();
        var fitsPath = Path.Combine(testDir, "exposure-only.fits");
        ImageWith(Meta(focalLength: 203, declaredPixelScale: float.NaN, exposure: TimeSpan.FromSeconds(42)))
            .WriteToFitsFile(fitsPath);
        // Our own writer only ever emits EXPTIME, so the case has to be built by RENAMING the card:
        // stripping it would leave a frame with no exposure at all, which tests something else and
        // would pass whether or not EXPOSURE is read.
        RenameCard(fitsPath, "EXPTIME", "EXPOSURE");

        Image.TryReadFitsFile(fitsPath, out var viaPixels).ShouldBeTrue();
        Image.TryReadFitsHeader(fitsPath, out var viaHeader).ShouldBeTrue();

        viaPixels!.ImageMeta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(42));
        viaHeader.Meta.ExposureDuration.ShouldBe(TimeSpan.FromSeconds(42));
    }

    /// <summary>
    /// Rewrites one card's 8-byte keyword field in place, so a frame written with EXPTIME can be made
    /// to carry EXPOSURE instead. Both names are exactly 8 bytes, so the card keeps its length and the
    /// data stays where it is.
    /// </summary>
    private static void RenameCard(string path, string from, string to)
    {
        const int Card = 80, KeywordBytes = 8;
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i + Card <= bytes.Length; i += Card)
        {
            var name = System.Text.Encoding.ASCII.GetString(bytes, i, KeywordBytes).TrimEnd();
            if (name == "END")
            {
                break;
            }
            if (name == from)
            {
                var replacement = to.PadRight(KeywordBytes);
                System.Text.Encoding.ASCII.GetBytes(replacement, 0, KeywordBytes, bytes, i);
                File.WriteAllBytes(path, bytes);
                return;
            }
        }
        throw new InvalidOperationException($"no {from} card to rename -- the fixture no longer sets up the case");
    }
}
