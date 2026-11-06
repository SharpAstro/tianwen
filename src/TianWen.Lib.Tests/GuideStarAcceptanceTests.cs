using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// #2050: the guider's candidate scan and the readout must only ever answer a STAR. A guide frame of a million pixels holds
/// about 200k noise maxima, and a hot pixel is a bright single sample; neither has a stellar profile, and a calibration that
/// starts on one measures zero movement and fails "insufficient star displacement". Frames here carry Gaussian stars of a guide
/// camera's FWHM over Gaussian noise plus isolated single-pixel hot pixels, and every answer is checked against what was drawn.
/// </summary>
[Collection("Guider")]
public class GuideStarAcceptanceTests(ITestOutputHelper output)
{
    private const float FullScale = 65535f;
    private const float Sky = 500f;

    /// <summary>The tolerance of the Done-when: an answer lies within this many pixels of a real star.</summary>
    private const double Tolerance = 1.5;

    /// <summary>Star peaks over the noise's sigma, a range that runs from faint to bright.</summary>
    private static readonly double[] PeakSnrs = [10, 15, 25, 60, 200, 1500];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    private sealed record Scene(float[,] Frame, List<(double X, double Y, double PeakSnr)> Stars, List<(int X, int Y)> HotPixels, double Noise);

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float[,] NoiseFrame(Random rng, int width, int height, double noise)
    {
        var frame = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                frame[y, x] = (float)(Sky + noise * Gaussian(rng));
            }
        }
        return frame;
    }

    private static void AddStar(float[,] frame, double cx, double cy, double fwhm, double peak)
    {
        var sigma = fwhm / 2.3548;
        var reach = (int)Math.Ceiling(5 * sigma);
        for (var y = (int)cy - reach; y <= (int)cy + reach; y++)
        {
            for (var x = (int)cx - reach; x <= (int)cx + reach; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                frame[y, x] += (float)(peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma)));
            }
        }
    }

    /// <summary>
    /// A frame of <paramref name="starCount"/> stars well apart, a hot pixel run clear of every star's aperture, and noise.
    /// </summary>
    private static Scene MakeScene(int seed, int width = 640, int height = 480, int starCount = 6, int hotCount = 40, double noise = 6.0, double fwhm = 3.5)
    {
        var rng = new Random(seed);
        var frame = NoiseFrame(rng, width, height, noise);
        var stars = new List<(double X, double Y, double PeakSnr)>();
        while (stars.Count < starCount)
        {
            var x = 40 + rng.NextDouble() * (width - 80);
            var y = 40 + rng.NextDouble() * (height - 80);
            if (stars.Any(s => Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y)) < 70))
            {
                continue;
            }
            var snr = PeakSnrs[stars.Count % PeakSnrs.Length];
            stars.Add((x, y, snr));
            AddStar(frame, x, y, fwhm, snr * noise);
        }

        var hots = new List<(int X, int Y)>();
        while (hots.Count < hotCount)
        {
            var x = 25 + rng.Next(width - 50);
            var y = 25 + rng.Next(height - 50);
            // A hot pixel inside a star's aperture or its background annulus is a different problem (it skews that star).
            if (stars.Any(s => Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y)) < 30))
            {
                continue;
            }
            // 20 sigma to 500 sigma, single pixel.
            var amplitude = noise * (20 + rng.NextDouble() * 480);
            frame[y, x] += (float)amplitude;
            hots.Add((x, y));
        }

        return new Scene(frame, stars, hots, noise);
    }

    private static double NearestStar(Scene scene, double x, double y)
        => scene.Stars.Min(s => Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y)));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void FindStarsReturnsOnlyStarsAndKeepsTheFaintOnes(int seed)
    {
        var scene = MakeScene(seed);
        var found = new GuiderCentroidTracker().FindStars(scene.Frame);

        foreach (var c in found)
        {
            NearestStar(scene, c.X, c.Y).ShouldBeLessThan(Tolerance,
                $"seed {seed}: candidate ({c.X:F1}, {c.Y:F1}) SNR {c.SNR:F1} is not within {Tolerance} px of a real star");
        }

        foreach (var star in scene.Stars)
        {
            found.Any(c => Math.Abs(c.X - star.X) < Tolerance && Math.Abs(c.Y - star.Y) < Tolerance)
                .ShouldBeTrue($"seed {seed}: the star at ({star.X:F1}, {star.Y:F1}) of peak SNR {star.PeakSnr} was missed");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MeasureWithNoNameAnswersARealStar(int seed)
    {
        var scene = MakeScene(seed);
        var read = GuideStarReadout.Measure(scene.Frame, FullScale);

        read.ShouldNotBeNull($"seed {seed}: there are {scene.Stars.Count} stars in the frame");
        NearestStar(scene, read.Value.X, read.Value.Y).ShouldBeLessThan(Tolerance,
            $"seed {seed}: ({read.Value.X:F1}, {read.Value.Y:F1}) is not within {Tolerance} px of a real star");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MeasureWithANameAnswersTheStarThereOrNothing(int seed)
    {
        var scene = MakeScene(seed);
        var rng = new Random(seed + 1000);

        // A click a few pixels off a star lands on it.
        foreach (var star in scene.Stars)
        {
            var read = GuideStarReadout.Measure(scene.Frame, FullScale, (star.X + 3, star.Y - 2));
            read.ShouldNotBeNull($"seed {seed}: a click near the star at ({star.X:F1}, {star.Y:F1}) of peak SNR {star.PeakSnr}");
            Math.Sqrt(Math.Pow(read.Value.X - star.X, 2) + Math.Pow(read.Value.Y - star.Y, 2)).ShouldBeLessThan(Tolerance);
        }

        // A click anywhere else answers a star within the guider's own pick radius, or nothing; never a noise maximum or a hot pixel.
        for (var i = 0; i < 40; i++)
        {
            var x = 30 + rng.NextDouble() * (scene.Frame.GetLength(1) - 60);
            var y = 30 + rng.NextDouble() * (scene.Frame.GetLength(0) - 60);
            var read = GuideStarReadout.Measure(scene.Frame, FullScale, (x, y));
            if (read is { } r)
            {
                NearestStar(scene, r.X, r.Y).ShouldBeLessThan(Tolerance,
                    $"seed {seed}: a click at ({x:F1}, {y:F1}) answered ({r.X:F1}, {r.Y:F1}), which is not a star");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AFrameOfNothingButNoiseAndHotPixelsHoldsNoStar(int seed)
    {
        // No star at all, the case a calibration at an empty position meets: a hundred hot pixels, noise everywhere.
        var rng = new Random(seed);
        var frame = NoiseFrame(rng, 640, 480, 6.0);
        for (var i = 0; i < 100; i++)
        {
            frame[25 + rng.Next(430), 25 + rng.Next(590)] += (float)(6.0 * (20 + rng.NextDouble() * 480));
        }

        new GuiderCentroidTracker().FindStars(frame).ShouldBeEmpty($"seed {seed}");
        GuideStarReadout.Measure(frame, FullScale).ShouldBeNull($"seed {seed}");
        for (var i = 0; i < 40; i++)
        {
            var at = (30 + rng.NextDouble() * 580, 30 + rng.NextDouble() * 420);
            GuideStarReadout.Measure(frame, FullScale, at).ShouldBeNull($"seed {seed}: a name at ({at.Item1:F1}, {at.Item2:F1})");
        }

        var tracker = new GuiderCentroidTracker(maxStars: 1) { PreferredStar = (320, 240) };
        tracker.ProcessFrame(frame).ShouldBeNull($"seed {seed}: acquiring at an empty position");
        tracker.IsAcquired.ShouldBeFalse();
    }

    [Fact]
    public void PureNoiseOverManyLargeFramesHoldsNoCandidate()
    {
        // A million pixels each, the size on which a peak over 3 sigma was no evidence of a star (hundreds of thousands of maxima).
        var tracker = new GuiderCentroidTracker();
        var falseCandidates = 0;
        const int frames = 40;
        for (var seed = 100; seed < 100 + frames; seed++)
        {
            falseCandidates += tracker.FindStars(NoiseFrame(new Random(seed), 1024, 1024, 6.0)).Count;
        }
        output.WriteLine($"{falseCandidates} candidates over {frames} noise frames of 1024 x 1024");
        falseCandidates.ShouldBe(0);
    }

    /// <summary>
    /// What the stricter bar costs the faint (#2050 review): a star is a peak whose 3x3 mean stands 6 sigma-of-a-3x3-mean over
    /// the background, about a peak of 3.7 sigma for a star of FWHM 3.5, hardly above the tracker's own SNR floor
    /// (<see cref="GuiderCentroidTracker.MinSNR"/>, 3). This prints the rate of finding a lone star of each peak SNR over many
    /// noise draws, so a change to the bar shows what it moved, and pins that every star from 5 sigma up is found every time
    /// (measured: 54 of 60 draws at 4 sigma, 60 of 60 from 5 sigma up).
    /// </summary>
    [Fact]
    public void TheFaintestStarTheBarTakesIsNearTheTrackersSnrFloor()
    {
        const int draws = 60;
        const double noise = 6.0;
        foreach (var peakSnr in new[] { 4.0, 5.0, 6.0, 8.0, 10.0 })
        {
            var found = 0;
            for (var seed = 1; seed <= draws; seed++)
            {
                var frame = NoiseFrame(new Random(seed * 31), 128, 128, noise);
                AddStar(frame, 64.3, 63.7, 3.5, peakSnr * noise);
                if (new GuiderCentroidTracker().FindStars(frame).Any(c => Math.Abs(c.X - 64.3) < Tolerance && Math.Abs(c.Y - 63.7) < Tolerance))
                {
                    found++;
                }
            }
            output.WriteLine($"a lone star of peak SNR {peakSnr}: found in {found} of {draws} draws");
            if (peakSnr >= 5.0)
            {
                found.ShouldBe(draws, $"a star of peak SNR {peakSnr} must always be found");
            }
        }
    }

    [Theory]
    [InlineData(1.8)]
    [InlineData(3.5)]
    [InlineData(7.0)]
    public void StarsOfAnyGuideCameraFocusAreKept(double fwhm)
    {
        // A tight, an ordinary and a defocused star: the profile check does not assume one width.
        for (var seed = 1; seed <= 6; seed++)
        {
            var scene = MakeScene(seed, fwhm: fwhm);
            var found = new GuiderCentroidTracker().FindStars(scene.Frame);
            foreach (var star in scene.Stars.Where(s => s.PeakSnr >= 15))
            {
                found.Any(c => Math.Abs(c.X - star.X) < Tolerance && Math.Abs(c.Y - star.Y) < Tolerance)
                    .ShouldBeTrue($"FWHM {fwhm}, seed {seed}: the star at ({star.X:F1}, {star.Y:F1}) of peak SNR {star.PeakSnr} was missed");
            }
            foreach (var c in found)
            {
                NearestStar(scene, c.X, c.Y).ShouldBeLessThan(Tolerance, $"FWHM {fwhm}, seed {seed}: ({c.X:F1}, {c.Y:F1}) is not a star");
            }
        }
    }
}
