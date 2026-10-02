using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Registrations compared with no truth (docs/plans/planetary-restoration.md, R5 part 3): AutoStakkert's session file read, and
/// the three-cornered hat's split of three tracks into each one's own error, against errors the test knows.
/// </summary>
public class RegistrationComparisonTests
{
    // An AutoStakkert 3.1.4 session file's shape (2022-09-03's Red, cut short), its indentation as the program writes it.
    private const string Session = """
        <AutoStakkert>
        AutoStakkert! Lucky Imaging software - machine- and human-readable session file
        System Information
          2022-09-04 10:56:25.170
          Frame count 3

        Settings
          _frames_count                                                    3
          _as_version                                                      3.1.4
          _quality_type                                                    Gradient
          _stack_output_prefix
          _reference_num_frames                                            2

        Alignment Points
          ap    24   101    64
          ap    32    98    66

        Planet Stabilization
         f 100.71 98.13
         f 101.00 98.48
         f 101.05 98.44
        Footer
           Session file written on 2022-09-04 10:56:40.665
        """;

    [Fact]
    public void AnAutoStakkertSessionReadsItsSettingsPointsAndTrack()
    {
        var session = AutoStakkertSession.Parse(new StringReader(Session)).ShouldNotBeNull();
        session.Version.ShouldBe("3.1.4");
        session.FrameCount.ShouldBe(3);
        session.Setting("_quality_type").ShouldBe("Gradient");
        session.Setting("_reference_num_frames").ShouldBe("2");
        session.Setting("_stack_output_prefix").ShouldBe("", "a setting left blank is a name alone");
        session.AlignmentPoints.ShouldBe([new AutoStakkertAlignmentPoint(24, 101, 64), new AutoStakkertAlignmentPoint(32, 98, 66)]);
        session.Track.ShouldBe([(100.71, 98.13), (101.00, 98.48), (101.05, 98.44)]);
    }

    [Fact]
    public void AFileThatIsNotASessionIsNull()
    {
        AutoStakkertSession.Parse(new StringReader("SIMPLE  =                    T")).ShouldBeNull();
    }

    [Fact]
    public void TheHatSplitsThreeIndependentErrorsWithoutATruth()
    {
        // The disk wanders 1.5 px (a random walk), which every track follows; each track adds an offset of its own origin and an
        // error of its own. The hat never sees the motion and reads each error from the three differences alone.
        const int frames = 20_000;
        var random = new Random(3);
        var errors = new[] { 0.1, 0.3, 0.5 };
        var tracks = new RegistrationTrack[3];
        var motion = new double[frames];
        for (var f = 1; f < frames; f++)
        {
            motion[f] = (0.98 * motion[f - 1]) + (0.3 * PhaseScreen.Gaussian(random));
        }
        for (var t = 0; t < 3; t++)
        {
            var (x, y) = (new double[frames], new double[frames]);
            for (var f = 0; f < frames; f++)
            {
                x[f] = 100 + (7 * t) + motion[f] + (errors[t] * PhaseScreen.Gaussian(random));
                y[f] = 50 - (3 * t) + (0.5 * motion[f]) + (errors[t] * PhaseScreen.Gaussian(random));
            }
            tracks[t] = new RegistrationTrack($"track {t}", x, y);
        }

        var hat = RegistrationComparison.Hat(tracks[0], tracks[1], tracks[2]);
        for (var t = 0; t < 3; t++)
        {
            Math.Sqrt(hat[t].X).ShouldBe(errors[t], 0.03 + (0.05 * errors[t]));
            Math.Sqrt(hat[t].Y).ShouldBe(errors[t], 0.03 + (0.05 * errors[t]));
        }
    }

    [Fact]
    public void TwoTracksThatShareAnErrorAreCreditedWithTooLittleAndTheThirdWithTooMuch()
    {
        // A and B carry one shared error of 0.4 px, so they agree with each other better than their errors warrant. The hat
        // cannot tell and says nothing: it credits A and B with almost no error and charges the shared 0.16 px^2 to C, whose own
        // is 0.01. Why the hat is checked on a synthetic capture against its truth before a real one's reading is believed.
        const int frames = 20_000;
        var random = new Random(5);
        var (a, b, c) = (new double[frames], new double[frames], new double[frames]);
        for (var f = 0; f < frames; f++)
        {
            var shared = 0.4 * PhaseScreen.Gaussian(random);
            a[f] = shared + (0.05 * PhaseScreen.Gaussian(random));
            b[f] = shared + (0.05 * PhaseScreen.Gaussian(random));
            c[f] = 0.1 * PhaseScreen.Gaussian(random);
        }
        var (ab, ac, bc) = (ThreeCorneredHat.DifferenceVariance(a, b), ThreeCorneredHat.DifferenceVariance(a, c), ThreeCorneredHat.DifferenceVariance(b, c));
        var hat = ThreeCorneredHat.Solve(ab, ac, bc);
        hat.A.ShouldBe(0.0025, 0.002, "A's own error is 0.05 px; its share of the 0.4 goes unseen");
        hat.C.ShouldBe(0.17, 0.02, "C is charged the shared 0.16 on top of its own 0.01");
    }

    [Fact]
    public void TwoTracksWhoseErrorsOpposeDriveTheThirdsVarianceBelowZero()
    {
        // B and C err by the same amount in opposite senses, so they disagree by more than their errors add to, and A, independent
        // of both, is solved at a negative variance: the one failure of independence the hat itself shows.
        const int frames = 20_000;
        var random = new Random(11);
        var (a, b, c) = (new double[frames], new double[frames], new double[frames]);
        for (var f = 0; f < frames; f++)
        {
            var opposed = 0.3 * PhaseScreen.Gaussian(random);
            a[f] = 0.1 * PhaseScreen.Gaussian(random);
            (b[f], c[f]) = (opposed, -opposed);
        }
        var (ab, ac, bc) = (ThreeCorneredHat.DifferenceVariance(a, b), ThreeCorneredHat.DifferenceVariance(a, c), ThreeCorneredHat.DifferenceVariance(b, c));
        ThreeCorneredHat.Solve(ab, ac, bc).A.ShouldBeLessThan(0);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(0.0, 0.10, 0.02)]
    [InlineData(1.25, 0.16, 0.05)]
    public async Task APlainCorrelationClimbsItsPeakAlongThePlanetsBelts(double noise, double maxErrorX, double maxErrorY)
    {
        // The banded disk's belts run along x, so x is placed by the limb alone and the correlation's peak is the disk's rounded
        // cone. A parabola through its three samples locks toward the whole pixel: 0.198 px RMS in x with no noise at all, 0.272
        // with the twin's (0.040 and 0.052 in y). Climbed on the correlation itself, 0.070 and 0.123 (0.009 and 0.029). What is
        // left with no noise is not yet attributed; the window, which stays put while the disk moves under it, is the suspect.
        const int frames = 41;
        var random = new Random(13);
        var sharp = PlanetaryMetricsTests.Blur(PlanetaryMetricsTests.Banded(), 1.5);
        var size = PlanetaryMetricsTests.Size;
        var (truthX, truthY) = (new double[frames], new double[frames]);
        var planes = new float[frames][,];
        for (var f = 0; f < frames; f++)
        {
            (truthX[f], truthY[f]) = ((random.NextDouble() * 4) - 2, (random.NextDouble() * 4) - 2);
            var moved = PlanetaryMetrics.Shift(sharp, size, size, truthX[f], truthY[f]);
            planes[f] = new float[size, size];
            for (var i = 0; i < moved.Length; i++)
            {
                planes[f][i / size, i % size] = (float)((moved[i] * 47) + (noise * PhaseScreen.Gaussian(random)));
            }
        }
        using var stream = new InMemoryFrameStream(planes);
        // Against the best frame, which is what the climb was measured against.
        var plain = await LuckyImagingStacker.RegisterAllAsync(stream, new PlanetaryStackOptions { WhitenedCorrelation = false, ReferenceFrames = 0 }, TestContext.Current.CancellationToken);
        var error = RegistrationComparison.DifferenceVariance(new RegistrationTrack("plain", plain.Dx, plain.Dy), new RegistrationTrack("truth", truthX, truthY));
        TestContext.Current.TestOutputHelper?.WriteLine($"noise {noise} ADU: plain correlation off by {Math.Sqrt(error.X):0.000}, {Math.Sqrt(error.Y):0.000} px RMS");
        Math.Sqrt(error.X).ShouldBeLessThan(maxErrorX);
        Math.Sqrt(error.Y).ShouldBeLessThan(maxErrorY);
    }

    [Fact(Timeout = 60_000)]
    public async Task AgainstAStackOfTheBestFramesANoisyFrameIsPlacedCloserThanAgainstOneFrame()
    {
        // 41 frames of a banded disk at the twin's level and noise (47 ADU, 1.25 ADU a pixel, rounded), each moved by a known
        // shift. Against one frame, that frame's noise is in every frame's registration; against a stack of 40 it is a sixth.
        const int frames = 41;
        var random = new Random(13);
        var sharp = PlanetaryMetricsTests.Blur(PlanetaryMetricsTests.Banded(), 1.5);
        var size = PlanetaryMetricsTests.Size;
        var (truthX, truthY) = (new double[frames], new double[frames]);
        var planes = new float[frames][,];
        for (var f = 0; f < frames; f++)
        {
            (truthX[f], truthY[f]) = ((random.NextDouble() * 4) - 2, (random.NextDouble() * 4) - 2);
            var moved = PlanetaryMetrics.Shift(sharp, size, size, truthX[f], truthY[f]);
            planes[f] = new float[size, size];
            for (var i = 0; i < moved.Length; i++)
            {
                planes[f][i / size, i % size] = (float)Math.Round((moved[i] * 47) + (1.25 * PhaseScreen.Gaussian(random)));
            }
        }
        var truth = new RegistrationTrack("truth", truthX, truthY);
        using var stream = new InMemoryFrameStream(planes);
        var ct = TestContext.Current.CancellationToken;

        var single = await LuckyImagingStacker.RegisterAllAsync(stream, new PlanetaryStackOptions { WhitenedCorrelation = false, ReferenceFrames = 0 }, ct);
        var stacked = await LuckyImagingStacker.RegisterAllAsync(stream, new PlanetaryStackOptions { WhitenedCorrelation = false, ReferenceFrames = 40 }, ct);
        var singleError = RegistrationComparison.DifferenceVariance(new RegistrationTrack("single", single.Dx, single.Dy), truth);
        var stackedError = RegistrationComparison.DifferenceVariance(new RegistrationTrack("stacked", stacked.Dx, stacked.Dy), truth);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"error px a axis: against the best frame {Math.Sqrt(singleError.X):0.000}, {Math.Sqrt(singleError.Y):0.000}; against a stack of 40 {Math.Sqrt(stackedError.X):0.000}, {Math.Sqrt(stackedError.Y):0.000}");
        (stackedError.X + stackedError.Y).ShouldBeLessThan(singleError.X + singleError.Y);
    }

    [Fact]
    public void TheFastPartOfADifferenceLeavesOutASlowDriftAndKeepsEachFramesError()
    {
        // Two tracks drift apart by a pixel over the capture (the texture rotating while the limb stays) and each errs by 0.2 px
        // a frame: their whole difference reads the drift as well, its fast part only the errors, sqrt(2) x 0.2 = 0.283 px.
        const int frames = 12_000;
        var random = new Random(17);
        var (a, b) = (new double[frames], new double[frames]);
        for (var f = 0; f < frames; f++)
        {
            a[f] = (1.0 * f / frames) + (0.2 * PhaseScreen.Gaussian(random));
            b[f] = 0.2 * PhaseScreen.Gaussian(random);
        }
        var (ta, tb) = (new RegistrationTrack("a", a, a), new RegistrationTrack("b", b, b));
        Math.Sqrt(RegistrationComparison.DifferenceVariance(ta, tb).X).ShouldBeGreaterThan(0.35);
        Math.Sqrt(RegistrationComparison.FastDifferenceVariance(ta, tb, 25).X).ShouldBe(0.283, 0.01);
    }

    [Fact]
    public void AFrameOneTrackDoesNotPlaceIsLeftOutOfEveryPair()
    {
        // The limb track fits every fourth frame; the hat takes all three pairs over the frames all three place, so a wild value
        // on a frame the limb skipped counts nowhere.
        const int frames = 400;
        var random = new Random(7);
        var (a, b, c) = (new double[frames], new double[frames], new double[frames]);
        for (var f = 0; f < frames; f++)
        {
            (a[f], b[f]) = (0.2 * PhaseScreen.Gaussian(random), 0.2 * PhaseScreen.Gaussian(random));
            c[f] = f % 4 == 0 ? 0.2 * PhaseScreen.Gaussian(random) : double.NaN;
            if (f % 4 == 1)
            {
                a[f] = 1e6;
            }
        }
        var hat = RegistrationComparison.Hat(new RegistrationTrack("a", a, a), new RegistrationTrack("b", b, b), new RegistrationTrack("c", c, c));
        Math.Sqrt(hat[0].X).ShouldBe(0.2, 0.06);
    }
}
