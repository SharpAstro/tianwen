using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using SharpAstro.Ser;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Each frame of a capture carried to one epoch inside the stacker (docs/plans/planetary-restoration.md, R6 part 2): a night's
/// files joined in time order, and a synthetic capture spanning 16 minutes of Jupiter's rotation stacked with and without it,
/// against the planet rendered at the capture's middle. The disk is 2024-12-15's on a split Bayer plane, 24 px in radius, so the
/// middle of the disk moves 2 px either side of the epoch.
/// </summary>
public class PlanetaryFrameDerotationTests
{
    private const int Size = FrameDerotationCaptures.Size;
    private static readonly DateTimeOffset Night = FrameDerotationCaptures.Night;
    private static readonly DiskPlacement Disk = FrameDerotationCaptures.Disk;
    private const double MinnaertK = FrameDerotationCaptures.MinnaertK;

    [Fact]
    public async Task ASequenceJoinsCapturesInTimeOrder()
    {
        var (early, late) = (PlanetarySerFixtures.NewTempPath(), PlanetarySerFixtures.NewTempPath());
        try
        {
            // The later capture named first: the sequence orders by time, never by the order it was given.
            PlanetarySerFixtures.WriteSer(late, 4, 4, SerColorId.Mono, [Filled(30), Filled(40)], [Night.AddMinutes(2), Night.AddMinutes(2.1)]);
            PlanetarySerFixtures.WriteSer(early, 4, 4, SerColorId.Mono, [Filled(10), Filled(20), Filled(25)], [Night, Night.AddSeconds(1), Night.AddSeconds(2)]);
            using var sequence = PlanetaryFrameSequence.OpenSer([late, early]);

            sequence.FrameCount.ShouldBe(5);
            sequence.PartCount.ShouldBe(2);
            sequence.HasTimestamps.ShouldBeTrue();
            sequence.TimestampOf(0).ShouldBe(Night);
            sequence.TimestampOf(3).ShouldBe(Night.AddMinutes(2));
            sequence.TimestampOf(5).ShouldBeNull();
            sequence.PartOf(2).ShouldBe(0);
            sequence.PartOf(3).ShouldBe(1);
            sequence.MidCapture.ShouldBe(Night.AddMinutes(1.05));
            var levels = new List<float>();
            for (var i = 0; i < sequence.FrameCount; i++)
            {
                var frame = await sequence.LoadAsync(i, TestContext.Current.CancellationToken);
                levels.Add(frame.GetChannelSpan(0)[0] * 65535);
                frame.Release();
            }
            levels.ShouldBe([10f, 20f, 25f, 30f, 40f], tolerance: 0.01f);
        }
        finally
        {
            File.Delete(early);
            File.Delete(late);
        }
    }

    [Fact]
    public void ASequenceRefusesCapturesThatOverlapOrDiffer()
    {
        using var a = new InMemoryFrameStream([new float[4, 4], new float[4, 4]], [Night, Night.AddSeconds(10)]);
        using var overlapping = new InMemoryFrameStream([new float[4, 4]], [Night.AddSeconds(5)]);
        using var larger = new InMemoryFrameStream([new float[5, 4]], [Night.AddMinutes(1)]);
        Should.Throw<ArgumentException>(() => new PlanetaryFrameSequence([a, overlapping]));
        Should.Throw<ArgumentException>(() => new PlanetaryFrameSequence([a, larger]));
    }

    [Fact]
    public void AFieldsOffsetIsWhereItsPixelReadsAndBetweenPixelsIsInterpolated()
    {
        var (from, to) = (PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night), PhysicalEphemeris.Compute(CatalogIndex.Jupiter, Night.AddMinutes(8)));
        var field = new DerotationTarget(to, Disk, Size, Size, MinnaertK).FieldFrom(from, Disk);
        var (x, y) = (35, 30);
        var i = (y * Size) + x;
        field.Covered[i].ShouldBeTrue();
        var (ox, oy) = field.OffsetAt(x, y);
        ox.ShouldBe(field.SourceX[i] - x);
        oy.ShouldBe(field.SourceY[i] - y);
        // Eight minutes turn the middle of a 24 px disk about 2 px: a field that carries it, and halfway between two pixels the
        // mean of their offsets.
        Math.Sqrt((ox * ox) + (oy * oy)).ShouldBeInRange(1.0, 3.0);
        var (nx, _) = field.OffsetAt(x + 1, y);
        field.OffsetAt(x + 0.5f, y).OffsetX.ShouldBe((ox + nx) / 2, 1e-5);
        // Off the disk, a pixel reads itself.
        field.OffsetAt(1, 1).ShouldBe((0f, 0f));
        field.RelightAt(1, 1).ShouldBe(1f);
    }

    [Fact(Timeout = 300_000)]
    public async Task ACaptureStackedAcrossSixteenMinutesIsThePlanetAtItsMiddle()
    {
        var capture = FrameDerotationCaptures.Capture(frames: 41, minutes: 16, seed: 5);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var options = new PlanetaryStackOptions { KeepFraction = 1, WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3 };
        var stacker = new LuckyImagingStacker();
        var ct = TestContext.Current.CancellationToken;

        var plain = await stacker.StackGlobalAsync(stream, options, ct);
        var derotated = await stacker.StackGlobalAsync(stream, options with { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) }, ct);

        // The truth: the planet at the capture's middle, on the reference frame's disk, where every stack puts it.
        derotated.Epoch.ShouldBe(stream.MidCapture);
        plain.Epoch.ShouldBeNull();
        var at = capture.Placements[derotated.ReferenceIndex];
        var truth = FrameDerotationCaptures.Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, stream.MidCapture ?? Night), at, Size);
        var (none, done) = (FrameDerotationCaptures.DiskRms(plain.Master, 0, truth, at), FrameDerotationCaptures.DiskRms(derotated.Master, 0, truth, at));
        TestContext.Current.TestOutputHelper?.WriteLine($"RMS against the planet at the middle, inside 0.8 radii: stacked as taken {none:0.00000}, de-rotated {done:0.00000}; {derotated.North}");

        // The capture decided the north: carried to the later quarter's instant turned over, the earlier quarter is far further
        // from it than carried the right way round, and the north it kept is the planet's.
        var north = derotated.North.ShouldNotBeNull();
        north.AgreementAsFitted.ShouldBeLessThan(north.AgreementTurnedOver * 0.7);
        Math.IEEERemainder(north.NorthAngleDeg - Disk.NorthAngleDeg, 360).ShouldBe(0, 5);
        done.ShouldBeLessThan(none * 0.3);
    }

    [Fact]
    public async Task ADerotatedStackRefusesPooledPoints()
    {
        using var stream = new InMemoryFrameStream([new float[8, 8]], [Night]);
        await Should.ThrowAsync<InvalidOperationException>(() => new LuckyImagingStacker().StackAsync(stream,
            new PlanetaryStackOptions { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter), WarpPoolFrames = 2 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUntimedCaptureCannotBeDerotated()
    {
        var capture = FrameDerotationCaptures.Capture(frames: 3, minutes: 1, seed: 1);
        using var stream = new InMemoryFrameStream(capture.Frames);
        await Should.ThrowAsync<InvalidOperationException>(() => new LuckyImagingStacker().StackGlobalAsync(stream,
            new PlanetaryStackOptions { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) }, TestContext.Current.CancellationToken));
    }

    private static ushort[] Filled(ushort value)
    {
        var frame = new ushort[16];
        Array.Fill(frame, value);
        return frame;
    }
}

/// <summary>
/// <see cref="PlanetaryFrameDerotationTests"/>'s alignment-point path, a class of its own: each de-rotated stack pays one limb fit,
/// a cold one on a single frame (tens of seconds in Debug), so the three stacking paths run beside each other.
/// </summary>
public class PlanetaryFrameDerotationPointsTests
{
    private const int Size = FrameDerotationCaptures.Size;
    private static readonly DateTimeOffset Night = FrameDerotationCaptures.Night;
    private static readonly DiskPlacement Disk = FrameDerotationCaptures.Disk;
    private const double MinnaertK = FrameDerotationCaptures.MinnaertK;

    [Fact(Timeout = 300_000)]
    public async Task AlignmentPointsOverADerotationBeatAlignmentPointsAlone()
    {
        // Points follow a rotation locally, as far as their patch reaches, so over minutes they take out part of what a
        // whole-disk shift cannot; carried to one epoch, the points have only the seeing left to follow.
        var capture = FrameDerotationCaptures.Capture(frames: 41, minutes: 16, seed: 3);
        using var stream = new InMemoryFrameStream(capture.Frames, capture.Times);
        var options = new PlanetaryStackOptions
        {
            KeepFraction = 1,
            WhitenedCorrelation = false,
            Interpolation = WarpInterpolation.Lanczos3,
            PerPointQualityWeighting = false,
            AlignmentPointSpacing = 12,
            AlignmentPatchSize = 16,
        };
        var stacker = new LuckyImagingStacker();
        var ct = TestContext.Current.CancellationToken;

        var plain = await stacker.StackAsync(stream, options, ct);
        var derotated = await stacker.StackAsync(stream, options with { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) }, ct);

        var at = capture.Placements[derotated.ReferenceIndex];
        var truth = FrameDerotationCaptures.Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, stream.MidCapture ?? Night), at, Size);
        var (none, done) = (FrameDerotationCaptures.DiskRms(plain.Master, 0, truth, at), FrameDerotationCaptures.DiskRms(derotated.Master, 0, truth, at));
        TestContext.Current.TestOutputHelper?.WriteLine($"RMS against the planet at the middle, inside 0.8 radii: points alone {none:0.00000}, points over the de-rotation {done:0.00000}");

        done.ShouldBeLessThan(none * 0.5);
    }
}

/// <summary><see cref="PlanetaryFrameDerotationTests"/>'s Bayer drizzle path, a class of its own for the same reason.</summary>
public class PlanetaryFrameDerotationDrizzleTests
{
    private const int Size = FrameDerotationCaptures.Size;
    private static readonly DateTimeOffset Night = FrameDerotationCaptures.Night;
    private static readonly DiskPlacement Disk = FrameDerotationCaptures.Disk;
    private const double MinnaertK = FrameDerotationCaptures.MinnaertK;

    [Fact(Timeout = 300_000)]
    public async Task ABayerDrizzleOfADerotatedRunIsThePlanetAtItsMiddle()
    {
        // A colour run as a Bayer mosaic, the planet grey in every photosite: drizzled to the sensor's grid as taken, and with
        // every raw sample relit and scattered through its frame's de-rotation.
        var path = PlanetarySerFixtures.NewTempPath();
        try
        {
            const int sensor = 2 * Size;
            const double level = 40000.0 / 65535;
            var onSensor = Disk with { CenterX = (2 * Disk.CenterX) + 0.5, CenterY = (2 * Disk.CenterY) + 0.5, EquatorialRadius = 2 * Disk.EquatorialRadius };
            var map = PlanetaryDerotationTests.SpottedMap();
            var random = new Random(11);
            var (frames, times, placements) = (new ushort[41][], new DateTimeOffset[41], new DiskPlacement[41]);
            for (var f = 0; f < frames.Length; f++)
            {
                times[f] = Night.AddMinutes(16.0 * f / (frames.Length - 1));
                placements[f] = onSensor with { CenterX = onSensor.CenterX + ((random.NextDouble() - 0.5) * 6), CenterY = onSensor.CenterY + ((random.NextDouble() - 0.5) * 6) };
                var render = PlanetaryRender.Render(map, PhysicalEphemeris.Compute(CatalogIndex.Jupiter, times[f]), placements[f], sensor, sensor, MinnaertK, supersample: 2);
                var samples = new ushort[sensor * sensor];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (ushort)Math.Clamp(Math.Round(((render[i] * level) + (0.004 * FrameDerotationCaptures.Gaussian(random))) * 65535), 0, 65535);
                }
                frames[f] = samples;
            }
            PlanetarySerFixtures.WriteSer(path, sensor, sensor, SerColorId.BayerRGGB, frames, times);
            using var stream = SerFrameStream.Open(path);
            var options = new PlanetaryStackOptions
            {
                KeepFraction = 1,
                WhitenedCorrelation = false,
                Drizzle = new PlanetaryDrizzleOptions(Scale: 1f, Pixfrac: 1f, AlignmentPointMesh: false),
            };
            var stacker = new LuckyImagingStacker();
            var ct = TestContext.Current.CancellationToken;

            var plain = await stacker.StackDrizzleAsync(stream, options, ct);
            var derotated = await stacker.StackDrizzleAsync(stream, options with { Derotation = new PlanetaryDerotationOptions(CatalogIndex.Jupiter) }, ct);

            var at = placements[derotated.ReferenceIndex];
            var truth = FrameDerotationCaptures.Render(PhysicalEphemeris.Compute(CatalogIndex.Jupiter, stream.MidCapture ?? Night), at, sensor);
            for (var i = 0; i < truth.Length; i++)
            {
                truth[i] *= (float)level;
            }
            var (none, done) = (FrameDerotationCaptures.DiskRms(plain.Master, 1, truth, at), FrameDerotationCaptures.DiskRms(derotated.Master, 1, truth, at));
            TestContext.Current.TestOutputHelper?.WriteLine($"green's RMS against the planet at the middle, inside 0.8 radii: drizzled as taken {none:0.00000}, de-rotated {done:0.00000}");

            done.ShouldBeLessThan(none * 0.4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The synthetic captures the frame de-rotation tests stack: the spotted map on 2024-12-15's split Bayer plane, a 24 px disk,
/// a little past east quadrature (10 degrees of phase), and how a stack is scored against the planet rendered at an instant.
/// </summary>
internal static class FrameDerotationCaptures
{
    internal const int Size = 64;
    // A little past east quadrature, 10 degrees of phase.
    internal static readonly DateTimeOffset Night = new(2025, 3, 3, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DiskPlacement Disk = new(31.7, 32.2, 24, 30);
    internal const double MinnaertK = 0.95;

    // A mono capture of the spotted map, frames evenly spread over the span, each rendered at its own instant, its disk moved by
    // up to 3 px and read with a little noise.
    internal static (float[][,] Frames, DateTimeOffset[] Times, DiskPlacement[] Placements) Capture(int frames, double minutes, int seed)
    {
        var map = PlanetaryDerotationTests.SpottedMap();
        var random = new Random(seed);
        var (planes, times, placements) = (new float[frames][,], new DateTimeOffset[frames], new DiskPlacement[frames]);
        for (var f = 0; f < frames; f++)
        {
            times[f] = Night.AddMinutes(minutes * f / Math.Max(1, frames - 1));
            placements[f] = Disk with { CenterX = Disk.CenterX + ((random.NextDouble() - 0.5) * 6), CenterY = Disk.CenterY + ((random.NextDouble() - 0.5) * 6) };
            var render = PlanetaryRender.Render(map, PhysicalEphemeris.Compute(CatalogIndex.Jupiter, times[f]), placements[f], Size, Size, MinnaertK, supersample: 2);
            var plane = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    plane[y, x] = render[(y * Size) + x] + (float)(0.004 * Gaussian(random));
                }
            }
            planes[f] = plane;
        }
        return (planes, times, placements);
    }

    internal static float[] Render(in PlanetAspect aspect, in DiskPlacement placement, int size)
        => PlanetaryRender.Render(PlanetaryDerotationTests.SpottedMap(), aspect, placement, size, size, MinnaertK, supersample: 2);

    // The RMS difference of a stack's channel from the truth inside 0.8 equatorial radii of `at`, where neither the limb's blur
    // nor the strip a de-rotation leaves as taken counts.
    internal static double DiskRms(Image stack, int channel, float[] truth, in DiskPlacement at)
    {
        var (width, height) = (stack.Width, stack.Height);
        var plane = stack.GetChannelSpan(channel);
        double sum = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (dx, dy) = (x - at.CenterX, y - at.CenterY);
                if ((dx * dx) + (dy * dy) < 0.64 * at.EquatorialRadius * at.EquatorialRadius)
                {
                    var d = plane[(y * width) + x] - truth[(y * width) + x];
                    sum += d * d;
                    count++;
                }
            }
        }
        return Math.Sqrt(sum / count);
    }

    internal static double Gaussian(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
