using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The session's per-frame cloud question (#1977), answered by the viewer grade's own measures.</summary>
public class LiveCloudJudgeTests
{
    private static Image Frame()
        => new([new float[4, 4]], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("FLI", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(300), FrameType.Light, "",
                9f, 9f, 530, FocuserPositions.None, Filter.Luminance, 1, 1, -25f, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN));

    private static LightGrade Graded(float patchiness)
        => new(LightGradeKey.Of(Frame()), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f, patchiness);

    [Fact]
    public void AClearRunReadsClearThenAPatchyFrameReadsCloudAndTheNextClearOneClearAgain()
    {
        var judge = new LiveCloudJudge();
        for (var i = 0; i < 10; i++)
        {
            LiveCloudJudge.IsCloud(judge.Add($"c{i}", Graded(1f + i * 0.01f))).ShouldBeFalse();
        }

        var cloudy = judge.Add("cloudy", Graded(9f));
        cloudy.ShouldNotBeNull();
        LiveCloudJudge.IsCloud(cloudy).ShouldBeTrue();

        LiveCloudJudge.IsCloud(judge.Add("after", Graded(1.02f))).ShouldBeFalse();
        judge.Grades.Count.ShouldBe(12);
    }

    /// <summary>
    /// The judge holds no night to fit: without a stored model it reads no veil term (the viewer's self-fit is the viewer's),
    /// so a thin veil over a night's last frames is not called cloud here.
    /// </summary>
    [Fact]
    public void AThinVeilIsNotReadWithoutAStoredModel()
    {
        var judge = new LiveCloudJudge();
        var night = LightGradingTests.SyntheticSkyNight(300, veilFrom: 266);
        for (var i = 0; i < night.Length; i++)
        {
            LiveCloudJudge.IsCloud(judge.Add($"f{i}", night[i])).ShouldBeFalse($"frame {i}");
        }
    }

    /// <summary>
    /// Given the site's stored expected sky for the rig (as the session loads it from <c>ExpectedSkyStore</c>), the judge reads the
    /// same veil as the viewer does: the veiled frames are cloud, the clear ones are not.
    /// </summary>
    [Fact]
    public void AThinVeilIsCloudAgainstTheStoredModel()
    {
        var clearSamples = new System.Collections.Generic.List<SkySample>();
        foreach (var g in LightGradingTests.SyntheticSkyNight(300, seed: 3))
        {
            clearSamples.Add(new SkySample(g.Covariates, g.Surface.Level, g.Surface.GradientStrength));
        }
        foreach (var g in LightGradingTests.SyntheticSkyNight(300, seed: 5))
        {
            clearSamples.Add(new SkySample(g.Covariates, g.Surface.Level, g.Surface.GradientStrength));
        }
        var model = ExpectedSkyModel.Fit(clearSamples) ?? throw new InvalidOperationException("the clear nights fit");
        var night = LightGradingTests.SyntheticSkyNight(300, veilFrom: 266);
        var sample = night[0];
        var key = new ExpectedSkyKey(LightGradingTests.SiteLat, LightGradingTests.SiteLon, sample.Rig, sample.Key.SensorType,
            sample.Key.FilterKey, sample.Key.ExposureMs, sample.Key.Bin);
        var judge = new LiveCloudJudge(expectedSky: [new ExpectedSkyEntry(key, model, 2)]);

        var clearCloud = 0;
        var veiledCloud = 0;
        for (var i = 0; i < night.Length; i++)
        {
            if (LiveCloudJudge.IsCloud(judge.Add($"f{i}", night[i])))
            {
                if (i < 266) { clearCloud++; } else { veiledCloud++; }
            }
        }

        veiledCloud.ShouldBeGreaterThanOrEqualTo(27, "the veiled frames are caught");
        clearCloud.ShouldBeLessThanOrEqualTo(3, "the clear ones are not");
    }

    /// <summary>
    /// A target cloudy from its first light is its own darkest sky and reads clear against itself; given a clear night of the
    /// same rig (#1976, the viewer's <c>clear-sky.json</c>) the judge calls it cloud from that first light on.
    /// </summary>
    [Fact]
    public void ARunCloudyFromItsFirstLightIsCaughtAgainstTheRigsClearNight()
    {
        var night = new DateOnly(2026, 7, 6);
        var key = new LightGradeKey(4, 4, SensorType.Monochrome, "", "LMC", 30_000, 1);
        // An even overcast: patchiness as steady as the clear night's best and twice its sky, the whole run through.
        LightGrade Cloudy(int i) => new(key, new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: 1f, SkyLevel: 2200f + i * 20, Night: night);
        ImmutableArray<LightGradeReference> clearNight =
            [new LightGradeReference(key, "", double.NaN, double.NaN, new DateOnly(2026, 6, 1), [new LightGradeObjectClearest("LMC", 0.5f)], 1000f)];

        var alone = new LiveCloudJudge();
        var withReference = new LiveCloudJudge(clearNight);
        for (var i = 0; i < 10; i++)
        {
            LiveCloudJudge.IsCloud(alone.Add($"lmc{i}", Cloudy(i))).ShouldBeFalse("against itself the run is its own clear sky");
            LiveCloudJudge.IsCloud(withReference.Add($"lmc{i}", Cloudy(i))).ShouldBeTrue("against the rig's clear night it is cloud");
        }
    }

    [Fact]
    public void AStarlessLightIsNotJudgedAndNotClear()
    {
        var judge = new LiveCloudJudge();
        var verdict = judge.Add("dark", LightGrading.Grade(Frame(), StarList.Empty));

        verdict.ShouldBeNull();
        LiveCloudJudge.IsCloud(verdict).ShouldBeFalse();
        judge.Grades.ShouldBeEmpty();
    }
}
