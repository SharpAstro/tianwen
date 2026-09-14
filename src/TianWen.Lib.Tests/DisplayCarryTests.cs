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
    private static Image ColourFrame(float level, SensorType sensorType = SensorType.Monochrome, Filter? filter = null,
        string objectName = "")
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

        var meta = new ImageMeta { Instrument = "synth", SensorType = sensorType, ObjectName = objectName };
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

    /// <summary>
    /// <b>An enhance result solves its own display mapping.</b> It is the same frame after a spatial
    /// and photometric transform, so it matches its original on every dimension
    /// <see cref="DisplayCarry.AreComparable"/> asks about -- size, channels, depth, sensor, filter,
    /// object -- and was anchored to it like any other frame of the run. That put the PRE-ENHANCE
    /// statistics behind the document's display basis while the enhancer had just flattened the
    /// background, so <c>ChannelsAlreadyAgree</c> read three channels percents apart, answered "not
    /// levelled" about a frame that was, and the background was neutralised a second time: a colour
    /// shift arriving one frame after the enhance landed, on top of an SPCC triple that was itself
    /// correct.
    /// </summary>
    [Fact]
    public async Task AnEnhanceResultIsNotAnchoredToTheFrameItCameFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await DocumentAsync(ColourFrame(0.10f, objectName: "M31"), "a.fits");
        var anchor = DisplayCarry.Apply(original, anchor: null, holdDisplay: true);
        anchor.ShouldBeSameAs(original);

        // Same shape, same target, same sensor -- which is the whole trap: comparable by every rule
        // DisplayCarry has, and yet not a frame of the run at all.
        var enhanced = await DocumentAsync(ColourFrame(0.30f, objectName: "M31"), "a.fits");
        DisplayCarry.AreComparable(original, enhanced).ShouldBeTrue(
            "the shape rule cannot tell an enhance result from another frame -- which is why the flag exists");

        enhanced.MarkAsEnhanceResult();
        var standing = DisplayCarry.Apply(enhanced, anchor, holdDisplay: true);

        enhanced.DisplayAnchor.ShouldBeNull("an enhance result displays from its OWN statistics");
        standing.ShouldBeSameAs(original,
            "and it does not displace the run's anchor either, so stepping on or reverting finds it unchanged");
    }

    /// <summary>
    /// The per-frame reconcile re-runs every frame, so the rule has to hold on the second pass too --
    /// that repetition is exactly how the anchor got re-attached after the enhance path cleared it.
    /// </summary>
    [Fact]
    public async Task TheEnhanceResultStaysUnanchoredAcrossRepeatedReconciles()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = await DocumentAsync(ColourFrame(0.10f, objectName: "M31"), "a.fits");
        var anchor = DisplayCarry.Apply(original, anchor: null, holdDisplay: true);

        var enhanced = await DocumentAsync(ColourFrame(0.30f, objectName: "M31"), "a.fits");
        enhanced.MarkAsEnhanceResult();

        for (var i = 0; i < 5; i++)
        {
            anchor = DisplayCarry.Apply(enhanced, anchor, holdDisplay: true);
            enhanced.DisplayAnchor.ShouldBeNull($"reconcile {i} re-anchored the enhance result");
        }

        anchor.ShouldBeSameAs(original);
    }

    private static Task<AstroImageDocument> DocumentAsync(Image image, string fileName)
        => AstroImageDocument.AdoptImageAsync(image, DebayerAlgorithm.None, wcs: null, filePath: fileName,
            cancellationToken: TestContext.Current.CancellationToken);

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

    /// <summary>
    /// <b>Two different targets are not one scene, however alike the sensor.</b> Geometry alone called
    /// them comparable, so stepping from one object to another in a folder of masters off the same rig
    /// showed the second with the first one's stretch -- every dimension, plane count, depth and CFA
    /// being identical by construction.
    /// </summary>
    [Fact]
    public void FramesOfDifferentObjectsAreNotComparable()
        => FrameShape.Of(ColourFrame(0.1f, objectName: "M8"))
            .IsComparableTo(FrameShape.Of(ColourFrame(0.1f, objectName: "SMC"))).ShouldBeFalse();

    [Fact]
    public void FramesOfTheSameObjectStayComparable()
        => FrameShape.Of(ColourFrame(0.1f, objectName: "M8"))
            .IsComparableTo(FrameShape.Of(ColourFrame(0.4f, objectName: "m8"))).ShouldBeTrue(
                "case is not a different target");

    /// <summary>
    /// An OBJECT card only one frame carries does not block the carry, the same permissive rule the
    /// filter takes: a folder where only some frames name their target is the common case, and
    /// refusing there would disable the blink on exactly the archives it was asked for.
    /// </summary>
    [Fact]
    public void AnObjectOnlyOneFrameNamesDoesNotBlockTheCarry()
    {
        FrameShape.Of(ColourFrame(0.1f)).IsComparableTo(FrameShape.Of(ColourFrame(0.1f, objectName: "M8")))
            .ShouldBeTrue();
        FrameShape.Of(ColourFrame(0.1f, objectName: "M8")).IsComparableTo(FrameShape.Of(ColourFrame(0.1f)))
            .ShouldBeTrue();
    }

    // --- DisplayCarry.PublishCalibration ---

    /// <summary>
    /// <b>Solve one frame, and the whole run is held to that colour balance.</b> The read direction
    /// always worked -- a follower inherits the anchor's triple through Basis -- but a fit solved while
    /// looking at a FOLLOWER landed on that follower alone, where no other frame could see it. So
    /// calibrating a sub and blinking to the next showed the next one uncalibrated.
    /// </summary>
    [Fact]
    public async Task AFitSolvedOnAFollowerReachesEveryFrameOfTheRun()
    {
        var anchorDoc = await DocumentAsync(ColourFrame(0.10f, objectName: "M8"), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.20f, objectName: "M8"), "b.fits");
        var third = await DocumentAsync(ColourFrame(0.30f, objectName: "M8"), "c.fits");

        var anchor = DisplayCarry.Apply(anchorDoc, anchor: null, holdDisplay: true);
        DisplayCarry.Apply(second, anchor, holdDisplay: true).ShouldBeSameAs(anchorDoc);
        DisplayCarry.Apply(third, anchor, holdDisplay: true).ShouldBeSameAs(anchorDoc);

        // A fit measured while the SECOND frame was on screen, which is where it lands today.
        second.InheritColorCalibration((1.4429f, 1f, 1.2284f), summary: null);
        third.ColorCalibration.ShouldBeNull("nothing has been published to the run yet");

        DisplayCarry.PublishCalibration(second);

        anchorDoc.ColorCalibration.ShouldBe((1.4429f, 1f, 1.2284f), "the run now carries it");
        third.ColorCalibration.ShouldBe((1.4429f, 1f, 1.2284f), "so a frame never calibrated reads it back");
        second.ColorCalibration.ShouldBe((1.4429f, 1f, 1.2284f), "and the frame it was solved on is unchanged");
    }

    /// <summary>
    /// It cannot leak across targets, which is the half that matters for a mixed folder: a different
    /// object shares no anchor, so publishing reaches nothing and that frame starts uncalibrated.
    /// </summary>
    [Fact]
    public async Task AFitDoesNotReachAFrameOfAnotherTarget()
    {
        var lagoon = await DocumentAsync(ColourFrame(0.10f, objectName: "M8"), "a.fits");
        var smc = await DocumentAsync(ColourFrame(0.10f, objectName: "SMC"), "b.fits");

        var anchor = DisplayCarry.Apply(lagoon, anchor: null, holdDisplay: true);
        // Not comparable, so it anchors its own run rather than following the Lagoon's.
        DisplayCarry.Apply(smc, anchor, holdDisplay: true).ShouldBeSameAs(smc);
        smc.DisplayAnchor.ShouldBeNull();

        lagoon.InheritColorCalibration((1.4429f, 1f, 1.2284f), summary: null);
        DisplayCarry.PublishCalibration(lagoon);

        smc.ColorCalibration.ShouldBeNull("a different target is calibrated on its own or not at all");
    }

    /// <summary>A frame that anchors its own run has nowhere to publish, and is left as it is.</summary>
    [Fact]
    public async Task PublishingFromAnAnchorIsANoOp()
    {
        var only = await DocumentAsync(ColourFrame(0.10f, objectName: "M8"), "a.fits");
        DisplayCarry.Apply(only, anchor: null, holdDisplay: true).ShouldBeSameAs(only);

        only.InheritColorCalibration((1.4429f, 1f, 1.2284f), summary: null);
        DisplayCarry.PublishCalibration(only);

        only.ColorCalibration.ShouldBe((1.4429f, 1f, 1.2284f));
    }

    // --- DisplayCarry.Apply ---

    [Fact]
    public async Task TheFirstFrameOfARunBecomesTheAnchorAndKeepsItsOwnStretch()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");

        var anchor = DisplayCarry.Apply(first, anchor: null, holdDisplay: true);

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

        var anchor = DisplayCarry.Apply(first, anchor: null, holdDisplay: true);
        DisplayCarry.Apply(second, anchor, holdDisplay: true).ShouldBeSameAs(first);

        second.DisplayAnchor.ShouldBeSameAs(first);
        Uniforms(second).ShouldBe(Uniforms(first),
            "a step between two subs of one field must not re-solve the auto-stretch");
    }

    [Fact]
    public async Task AnIncomparableFrameStartsARunOfItsOwn()
    {
        var colour = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var mono = await DocumentAsync(MonoFrame(0.10f), "b.fits");

        var anchor = DisplayCarry.Apply(colour, anchor: null, holdDisplay: true);
        var next = DisplayCarry.Apply(mono, anchor, holdDisplay: true);

        next.ShouldBeSameAs(mono);
        mono.DisplayAnchor.ShouldBeNull();
    }

    [Fact]
    public async Task ReApplyingTheAnchorToTheFrameThatIsTheAnchorLeavesItAlone()
    {
        // The reconcile runs every frame, so it re-visits the anchor itself constantly. A document that
        // became its own anchor would defeat the single hop silently rather than loop.
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var anchor = DisplayCarry.Apply(first, anchor: null, holdDisplay: true);

        DisplayCarry.Apply(first, anchor, holdDisplay: true).ShouldBeSameAs(first);
        first.DisplayAnchor.ShouldBeNull();
    }

    /// <summary>Releasing the hold gives a frame already held its own stretch back; the run's anchor
    /// stays, because the calibration still rides on it.</summary>
    [Fact]
    public async Task ReleasingTheHoldGivesAnAnchoredFrameItsOwnStretchBack()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        DisplayCarry.Apply(second, first, holdDisplay: true);
        second.HasDisplayAnchor.ShouldBeTrue();
        Uniforms(second).ShouldBe(Uniforms(first), "held, one mapping for both");

        DisplayCarry.Apply(second, first, holdDisplay: false).ShouldBeSameAs(first);

        second.DisplayAnchor.ShouldBeSameAs(first, "the run is still the run");
        second.HasDisplayAnchor.ShouldBeFalse("but the display is this frame's own again");
        Uniforms(second).ShouldNotBe(Uniforms(first), "frame by frame, as a click shows it");
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

        DisplayCarry.Apply(second, first, holdDisplay: true);

        second.ColorCalibration.ShouldBe((1.08f, 1f, 0.93f));
        second.ColorCalibrationSummary.ShouldBe(summary, "the provenance travels with the triple, or the UI can show a multiplier it cannot source");
    }

    [Fact]
    public async Task TheFollowerStillReportsItsOwnMeasuredBackground()
    {
        var first = await DocumentAsync(ColourFrame(0.10f), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.40f), "b.fits");
        var ownBackground = second.MeasuredPerChannelBackground[0];

        DisplayCarry.Apply(second, first, holdDisplay: true);

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

        DisplayCarry.Apply(second, first, holdDisplay: true);

        second.Stars.ShouldBeNull("stars are what a blink is looking AT; they are per frame");
    }

    /// <summary>
    /// <b>A click carries the calibration; only the hold carries the stretch.</b> The run's anchor
    /// stands either way (it is what the SPCC triple rides on, one fit per run), but the display
    /// statistics read through it only under Ctrl+H -- a click means "show me this frame", and a held
    /// curve over a darker sky dimmed the target as the night improved.
    /// </summary>
    [Fact]
    public async Task WithoutTheHold_TheCalibrationIsCarriedAndTheStretchIsNot()
    {
        var first = await DocumentAsync(ColourFrame(0.10f, objectName: "M31"), "a.fits");
        var second = await DocumentAsync(ColourFrame(0.30f, objectName: "M31"), "b.fits");

        var anchor = DisplayCarry.Apply(first, anchor: null, holdDisplay: false);
        DisplayCarry.Apply(second, anchor, holdDisplay: false).ShouldBeSameAs(first,
            "the run has an anchor whatever the hold says: the calibration rides on it");

        first.InheritColorCalibration((1.4429f, 1f, 1.2284f), summary: null);
        second.ColorCalibration.ShouldBe((1.4429f, 1f, 1.2284f), "a click still gets the run's calibration");
        second.HasDisplayAnchor.ShouldBeFalse("but its stretch is its own");
        second.BasisPerChannelStats.ShouldBe(second.PerChannelStats);

        DisplayCarry.Apply(second, anchor, holdDisplay: true);
        second.HasDisplayAnchor.ShouldBeTrue("held, the stretch is the anchor's");
        second.BasisPerChannelStats.ShouldBe(first.PerChannelStats);
    }
}
