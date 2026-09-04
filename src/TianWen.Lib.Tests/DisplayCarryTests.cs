using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins P19: stepping between comparable frames of a folder shows every one of them with the SAME
/// display mapping, so the only thing that changes on screen is the frames themselves.
/// </summary>
/// <remarks>
/// The discriminating assertion throughout is the pair: two frames with genuinely different statistics
/// must render the same uniforms WITH an anchor and different ones WITHOUT it. Asserting only the first
/// half passes against an implementation that has no carry at all whenever the two frames happen to
/// solve alike, which synthetic frames easily do.
/// </remarks>
public class DisplayCarryTests
{
    private const int Width = 24;
    private const int Height = 16;

    /// <summary>
    /// A three-channel frame whose background sits at <paramref name="level"/>, with a gradient so the
    /// MAD is non-zero and the solver has a real curve to derive.
    /// </summary>
    private static Image ColourFrame(float level, SensorType sensorType = SensorType.Monochrome, Filter? filter = null)
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = new float[Height, Width];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    plane[y, x] = level + 0.02f * ((x + y + c) % 7);
                }
            }
            planes[c] = plane;
        }

        var meta = new ImageMeta { Instrument = "synth", SensorType = sensorType };
        if (filter is { } f)
        {
            meta = meta with { Filter = f };
        }

        return new Image([planes[0], planes[1], planes[2]], BitDepth.Float32,
            maxValue: 1f, minValue: 0f, pedestal: 0f, imageMeta: meta);
    }

    private static Image MonoFrame(float level)
    {
        var plane = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                plane[y, x] = level + 0.02f * ((x + y) % 7);
            }
        }

        return new Image([plane], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    private static Task<AstroImageDocument> DocumentAsync(Image image, string fileName)
        => AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None, wcs: null, filePath: fileName,
            TestContext.Current.CancellationToken);

    private static StretchUniforms Uniforms(AstroImageDocument document)
        => document.ComputeStretchUniforms(StretchMode.Unlinked, StretchParameters.Default);

    // --- FrameShape ---

    [Fact]
    public void TwoFramesOffTheSameCameraAreComparable()
        => FrameShape.Of(ColourFrame(0.10f)).IsComparableTo(FrameShape.Of(ColourFrame(0.40f))).ShouldBeTrue(
            "a different exposure of the same field is exactly what the carry is for");

    [Fact]
    public void ADifferentSizeIsNotComparable()
    {
        var wide = new Image([new float[Height, Width + 1]], BitDepth.Float32,
            maxValue: 1f, minValue: 0f, pedestal: 0f,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });

        FrameShape.Of(MonoFrame(0.1f)).IsComparableTo(FrameShape.Of(wide)).ShouldBeFalse();
    }

    [Fact]
    public void ADifferentPlaneCountIsNotComparable()
        => FrameShape.Of(ColourFrame(0.1f)).IsComparableTo(FrameShape.Of(MonoFrame(0.1f))).ShouldBeFalse();

    [Fact]
    public void ADifferentCfaIsNotComparable()
        => FrameShape.Of(ColourFrame(0.1f, SensorType.Monochrome))
            .IsComparableTo(FrameShape.Of(ColourFrame(0.1f, SensorType.RGGB))).ShouldBeFalse(
                "the shader debayers one and not the other, so one mapping cannot describe both");

    [Fact]
    public void TwoDifferentNamedFiltersAreNotComparable()
        => FrameShape.Of(ColourFrame(0.1f, filter: Filter.HydrogenAlpha))
            .IsComparableTo(FrameShape.Of(ColourFrame(0.1f, filter: Filter.OxygenIII))).ShouldBeFalse();

    [Fact]
    public void AFilterOnlyOneFrameNamesDoesNotBlockTheCarry()
    {
        // A folder where only some frames carry a FILTER card is the common case, not a corner: refusing
        // there would disable the feature on exactly the archives it was asked for.
        FrameShape.Of(ColourFrame(0.1f)).IsComparableTo(FrameShape.Of(ColourFrame(0.1f, filter: Filter.HydrogenAlpha)))
            .ShouldBeTrue();
        FrameShape.Of(ColourFrame(0.1f, filter: Filter.HydrogenAlpha)).IsComparableTo(FrameShape.Of(ColourFrame(0.1f)))
            .ShouldBeTrue();
    }

    // --- DisplayCarry.Apply ---

    [Fact]
    public async Task TheFirstFrameOfARunBecomesTheAnchorAndKeepsItsOwnStretch()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");

        var anchor = DisplayCarry.Apply(first, anchor: null, carry: true);

        anchor.ShouldBeSameAs(first);
        first.DisplayAnchor.ShouldBeNull("a frame that anchors a run is displayed with its own numbers");
    }

    [Fact]
    public async Task AComparableFrameIsShownWithTheAnchorsStretch()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");

        // The frames really do solve differently -- without which the assertion below proves nothing.
        Uniforms(second).Shadows.ShouldNotBe(Uniforms(first).Shadows);

        var anchor = DisplayCarry.Apply(first, anchor: null, carry: true);
        DisplayCarry.Apply(second, anchor, carry: true).ShouldBeSameAs(first);

        second.DisplayAnchor.ShouldBeSameAs(first);
        Uniforms(second).ShouldBe(Uniforms(first),
            "a step between two subs of one field must not re-solve the auto-stretch");
    }

    [Fact]
    public async Task AnIncomparableFrameStartsARunOfItsOwn()
    {
        var colour = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var mono = await DocumentAsync(MonoFrame(0.10f), "b.fits");

        var anchor = DisplayCarry.Apply(colour, anchor: null, carry: true);
        var next = DisplayCarry.Apply(mono, anchor, carry: true);

        next.ShouldBeSameAs(mono);
        mono.DisplayAnchor.ShouldBeNull();
    }

    [Fact]
    public async Task ReApplyingTheAnchorToTheFrameThatIsTheAnchorLeavesItAlone()
    {
        // The reconcile runs every frame, so it re-visits the anchor itself constantly. A document that
        // became its own anchor would defeat the single hop silently rather than loop.
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var anchor = DisplayCarry.Apply(first, anchor: null, carry: true);

        DisplayCarry.Apply(first, anchor, carry: true).ShouldBeSameAs(first);
        first.DisplayAnchor.ShouldBeNull();
    }

    [Fact]
    public async Task TurningTheCarryOffReleasesAFrameAlreadyAnchored()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        DisplayCarry.Apply(second, first, carry: true);
        second.DisplayAnchor.ShouldNotBeNull();

        DisplayCarry.Apply(second, first, carry: false).ShouldBeNull();

        second.DisplayAnchor.ShouldBeNull();
        Uniforms(second).ShouldNotBe(Uniforms(first), "the pre-P19 behaviour, frame by frame");
    }

    // --- what travels, and what does not ---

    [Fact]
    public async Task TheAnchorsColourCalibrationIsWhatTheFollowerReports()
    {
        // The SPCC fit is the slow half of a file switch; carrying it is what the user asked for as
        // "so that they load faster". A follower that reported none would re-fit on every file.
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        var summary = new ColorCalibrationSummary("SPCC", 1.08f, 1f, 0.93f, StarCount: 412, WhiteReference: "G2V");
        first.InheritColorCalibration((1.08f, 1f, 0.93f), summary);

        DisplayCarry.Apply(second, first, carry: true);

        second.ColorCalibration.ShouldBe((1.08f, 1f, 0.93f));
        second.ColorCalibrationSummary.ShouldBe(summary, "the provenance travels with the triple, or the UI can show a multiplier it cannot source");
    }

    [Fact]
    public async Task TheFollowerStillReportsItsOwnMeasuredBackground()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        var ownBackground = second.MeasuredPerChannelBackground[0];

        DisplayCarry.Apply(second, first, carry: true);

        second.PerChannelBackground[0].ShouldBe(first.PerChannelBackground[0],
            "the DISPLAY is solved from one background for the whole run");
        second.MeasuredPerChannelBackground[0].ShouldBe(ownBackground,
            "the info panel reads a measurement of the frame in front of the user, not the anchor's");
        second.MeasuredPerChannelBackground[0].ShouldNotBe(first.MeasuredPerChannelBackground[0]);
    }

    [Fact]
    public async Task TheStarListIsNeverCarried()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        await first.DetectStarsAsync(TestContext.Current.CancellationToken);

        DisplayCarry.Apply(second, first, carry: true);

        second.Stars.ShouldBeNull("stars are what a blink is looking AT; they are per frame");
    }
}
