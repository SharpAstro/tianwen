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
    /// The session judges each light against the frames BEFORE it, and the sky's trend is read from those alone, so a sky that
    /// turns against its own line is cloud live as in the viewer (2026-09-12's end-of-night rise), from its first risen frame.
    /// A sustained rise reads cloud for a while (<see cref="LightGrading.SkyOverTrend"/>): at 1 minute subs a +6 % step held reads
    /// cloud for its first 51 minutes (the 60 minute window less the ten frames the fit needs), then the frame is not read. The
    /// reach depends on the cadence, so it is pinned at 1 minute here and at 5 minutes below. The bracket fill needs the cloud
    /// frame AFTER a frame, so a live verdict never carries it.
    /// </summary>
    [Fact]
    public void ASkyTurningAgainstItsTrendReadsCloudLiveFromItsFirstRisenFrame()
    {
        var judge = new LiveCloudJudge();
        var start = new DateTimeOffset(2026, 9, 12, 17, 0, 0, TimeSpan.Zero);
        var random = new Random(5);
        var risen = 0;
        for (var m = 0; m < 200; m++)
        {
            var jitter = 1f + 0.002f * (random.NextSingle() + random.NextSingle() - 1f);
            var sky = (1400f - 1.4f * m) * (m >= 80 ? 1.06f : 1f) * jitter;
            var verdict = judge.Add($"f{m}", Graded(1f) with { Taken = start.AddMinutes(m), SkyLevel = sky });
            if (m < 80)
            {
                LiveCloudJudge.IsCloud(verdict).ShouldBeFalse($"minute {m} is on its trend");
            }
            else if (m < 80 + 51)
            {
                LiveCloudJudge.IsCloud(verdict).ShouldBeTrue($"minute {m} is within the rise's reach");
                verdict.ShouldNotBeNull().SkyRising.ShouldBeTrue();
                risen++;
            }
            else
            {
                LiveCloudJudge.IsCloud(verdict).ShouldBeFalse($"minute {m}: too few clear frames left in the window to read");
            }
        }
        risen.ShouldBe(51, "the first 51 minutes of the rise");
    }

    /// <summary>
    /// The same rise at 5 minute subs: ten frames would span 45 minutes of a 60 minute window, so the fit also accepts
    /// <see cref="LightGrading.SkyTrendMinFramesOverSpan"/> frames spanning <see cref="LightGrading.SkyTrendMinSpan"/>, and the
    /// rule still reads at long subs (a count alone read 3 frames at 5 minutes and none from 6).
    /// </summary>
    [Fact]
    public void ASkyTurningAgainstItsTrendIsStillReadAtFiveMinuteSubs()
    {
        var judge = new LiveCloudJudge();
        var start = new DateTimeOffset(2026, 9, 12, 17, 0, 0, TimeSpan.Zero);
        var random = new Random(5);
        var risen = 0;
        for (var i = 0; i < 80; i++)
        {
            var jitter = 1f + 0.002f * (random.NextSingle() + random.NextSingle() - 1f);
            var sky = (1400f - 7f * i) * (i >= 40 ? 1.06f : 1f) * jitter;
            var verdict = judge.Add($"f{i}", Graded(1f) with { Taken = start.AddMinutes(5 * i), SkyLevel = sky });
            if (i < 40)
            {
                LiveCloudJudge.IsCloud(verdict).ShouldBeFalse($"frame {i} is on its trend");
            }
            else if (LiveCloudJudge.IsCloud(verdict))
            {
                risen++;
            }
        }
        risen.ShouldBeGreaterThan(3, "more than the three a count of ten allowed at 5 minute subs");
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
