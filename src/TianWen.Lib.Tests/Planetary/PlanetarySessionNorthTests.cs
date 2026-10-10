using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A north given by the caller (#1347): a session's, read once from its first and last captures, which every capture of it then takes
/// instead of reading its own quarters, minutes apart, which can tie on a bland globe (one file of 30 of the owner's 2026-10-07 Saturn
/// read it turned over, 0.02008 against 0.02009). On <see cref="FrameDerotationCaptures"/>' 16 minutes of Jupiter, whose north the
/// capture's quarters tell well apart.
/// </summary>
public class PlanetarySessionNorthTests
{
    private static readonly DiskPlacement Disk = FrameDerotationCaptures.Disk;

    // Against the best frame alone, as the de-rotation tests stack: a stacked reference doubles the Debug time.
    private static readonly PlanetaryStackOptions Options = new()
    {
        CropToCoverage = false, KeepFraction = 1, WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3, ReferenceFrames = 0,
        Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter),
    };

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task ANorthReadOnceAndGivenBackStacksAsTheCapturesOwnQuartersDo()
    {
        var capture = FrameDerotationCaptures.Capture(frames: 41, minutes: 16, seed: 5);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var ct = TestContext.Current.CancellationToken;
        var stacker = new LuckyImagingStacker();

        var read = (await LuckyImagingStacker.ReadNorthAsync(stream, Options, ct)).ShouldNotBeNull();
        read.Given.ShouldBeNull();
        Math.IEEERemainder(read.NorthAngleDeg - Disk.NorthAngleDeg, 360).ShouldBe(0, 5);

        var own = await stacker.StackGlobalAsync(stream, Options, ct);
        var given = await stacker.StackGlobalAsync(stream, Options with { Derotation = Options.Derotation! with { North = read.NorthAngleDeg } }, ct);

        // The quarters were not read for the given north, and it was taken: the same way round as the capture's own reading, so the
        // same stack, bit for bit (the same fit, the same derotator).
        var taken = given.North.ShouldNotBeNull();
        taken.Given.ShouldBe(read.NorthAngleDeg);
        double.IsNaN(taken.AgreementAsFitted).ShouldBeTrue();
        double.IsNaN(taken.AgreementTurnedOver).ShouldBeTrue();
        taken.NorthAngleDeg.ShouldBe(own.North.ShouldNotBeNull().NorthAngleDeg);
        given.Master.GetChannelSpan(0).SequenceEqual(own.Master.GetChannelSpan(0)).ShouldBeTrue();
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task ASessionNorthTurnedOverReachesEachCaptureTurnedOverOnce()
    {
        // #1409: --each-file reads the session's north with the run's options, --turn-north-over among them, and gives it to every capture
        // with the flag still set. The read turned it over and the capture's stack turned the north given over again, so the two cancelled
        // and every capture was de-rotated with the north the user asked to reverse. The read is the capture's own north; the turn is the
        // stack's, once.
        var capture = FrameDerotationCaptures.Capture(frames: 21, minutes: 16, seed: 7);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var ct = TestContext.Current.CancellationToken;
        var turnedOver = Options with { Derotation = Options.Derotation! with { TurnNorthOver = true } };

        var read = (await LuckyImagingStacker.ReadNorthAsync(stream, turnedOver, ct)).ShouldNotBeNull();
        var each = turnedOver with { Derotation = turnedOver.Derotation! with { North = read.NorthAngleDeg } };
        var result = await new LuckyImagingStacker().StackGlobalAsync(stream, each, ct);

        Math.IEEERemainder(result.North.ShouldNotBeNull().NorthAngleDeg - (Disk.NorthAngleDeg + 180), 360).ShouldBe(0, 5, "turned over once");
        Math.IEEERemainder(read.NorthAngleDeg - Disk.NorthAngleDeg, 360).ShouldBe(0, 5, "the session's own north");
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "Heavy")]
    public async Task AGivenNorthIsTakenEvenWhereTheCapturesQuartersDisagree()
    {
        // Given the wrong way round, the stack takes the fit's axis the way round nearer the north given: the quarters, which tell this
        // capture's north well apart, are not asked.
        var capture = FrameDerotationCaptures.Capture(frames: 21, minutes: 16, seed: 7);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var result = await new LuckyImagingStacker().StackGlobalAsync(stream,
            Options with { Derotation = Options.Derotation! with { North = Disk.NorthAngleDeg + 180 } }, TestContext.Current.CancellationToken);

        var north = result.North.ShouldNotBeNull();
        north.Given.ShouldBe(Disk.NorthAngleDeg + 180);
        Math.IEEERemainder(north.NorthAngleDeg - (Disk.NorthAngleDeg + 180), 360).ShouldBe(0, 5);
    }
}
