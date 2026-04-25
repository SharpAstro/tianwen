using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pure-function tests for the FOV scout classifier and FOV computation. The full
/// <c>ScoutAndProbeAsync</c> integration flow lives in
/// <c>SessionScoutAndProbeTests</c> in the Functional test project.
/// </summary>
public class SessionScoutClassifierTests(ITestOutputHelper output)
{
    private static FrameMetrics M(int starCount, double exposureSec = 10.0)
        => new(starCount, MedianHfd: 2.5f, MedianFwhm: 3.0f, Exposure: TimeSpan.FromSeconds(exposureSec), Gain: 0);

    [Fact]
    public async Task GivenScoutMatchingBaselineWhenClassifyThenHealthy()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var scout = new[] { M(starCount: 80, exposureSec: 10) };
            var baseline = new[] { M(starCount: 100, exposureSec: 10) };

            var (classification, ratio) = ctx.Session.ClassifyAgainstBaseline(scout, baseline);

            classification.ShouldBe(ScoutClassification.Healthy);
            ratio.ShouldBe(0.8f, tolerance: 0.001f);
        }
        finally { ctx.Dispose(); }
    }

    [Fact]
    public async Task GivenScoutFarBelowBaselineWhenClassifyThenObstructionTentative()
    {
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var scout = new[] { M(starCount: 5, exposureSec: 10) };
            var baseline = new[] { M(starCount: 100, exposureSec: 10) };

            var (classification, ratio) = ctx.Session.ClassifyAgainstBaseline(scout, baseline);

            classification.ShouldBe(ScoutClassification.Obstruction);
            ratio.ShouldBeLessThan(0.1f);
        }
        finally { ctx.Dispose(); }
    }

    [Fact]
    public async Task GivenShorterScoutThanBaselineWhenClassifyThenExposureScaledByRoot()
    {
        // Baseline: 100 stars at 120s. Scout: 30 stars at 10s.
        // Scaled expected = 100 * sqrt(10/120) = 100 * 0.289 = 28.9 stars.
        // Ratio = 30 / 28.9 = 1.04 → above 0.7 threshold → Healthy.
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var scout = new[] { M(starCount: 30, exposureSec: 10) };
            var baseline = new[] { M(starCount: 100, exposureSec: 120) };

            var (classification, ratio) = ctx.Session.ClassifyAgainstBaseline(scout, baseline);

            classification.ShouldBe(ScoutClassification.Healthy);
            ratio.ShouldBeGreaterThan(1.0f);
        }
        finally { ctx.Dispose(); }
    }

    [Fact]
    public async Task GivenInvalidBaselineWhenClassifyThenHealthyDefault()
    {
        // No usable baseline → no judgement; caller should treat as Healthy.
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var scout = new[] { M(starCount: 0) };
            var baseline = new[] { default(FrameMetrics) }; // IsValid = false

            var (classification, _) = ctx.Session.ClassifyAgainstBaseline(scout, baseline);

            classification.ShouldBe(ScoutClassification.Healthy);
        }
        finally { ctx.Dispose(); }
    }

    [Fact]
    public async Task GivenMultiOtaWhenWorstOtaSevereThenObstruction()
    {
        // Two OTAs: one healthy (0.9 ratio), one severe (0.1 ratio).
        // Worst-OTA dominates because the rig points one place.
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var scout = new[] { M(starCount: 90), M(starCount: 10) };
            var baseline = new[] { M(starCount: 100), M(starCount: 100) };

            var (classification, ratio) = ctx.Session.ClassifyAgainstBaseline(scout, baseline);

            classification.ShouldBe(ScoutClassification.Obstruction);
            ratio.ShouldBe(0.1f, tolerance: 0.001f);
        }
        finally { ctx.Dispose(); }
    }

    [Fact]
    public async Task GivenSingleOtaSetupWhenComputeWidestHalfFovThenMatchesPixelScale()
    {
        // Default FakeCameraDriver: pixel size 5.4 µm, 512×512 NumX. focalLength=1000mm via helper.
        // Pixel scale = 5.4 / 1000 * 206.265 = 1.1138 arcsec/pixel
        // FOV = 512 * 1 * 1.1138 / 3600 = 0.158° → half = 0.079°
        var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var halfFov = ctx.Session.ComputeWidestHalfFovDeg();

            halfFov.ShouldBeGreaterThan(0.05);
            halfFov.ShouldBeLessThan(0.2);
        }
        finally { ctx.Dispose(); }
    }
}
