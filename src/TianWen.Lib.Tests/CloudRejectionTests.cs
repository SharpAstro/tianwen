using System;
using System.Collections.Immutable;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;
using Xunit;
using TwilightPeriod = TianWen.Lib.Sequencing.TwilightPeriod;

namespace TianWen.Lib.Tests;

/// <summary>
/// The stacker leaves out exactly the lights the viewer's grade calls cloud or twilight (#1981): one verdict, read by both. A clear
/// group keeps every frame, a passing cloud and a dawn frame go, and the reason given is the viewer's own wording.
/// </summary>
public class CloudRejectionTests
{
    private static readonly LightGradeKey Key = new(64, 64, SensorType.Monochrome, "L", "M42", 300_000, 1);
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 10, 30, 0, TimeSpan.Zero);

    private static LightGrade Frame(int minute, float patchiness, TwilightPeriod? twilight = null)
        => new(Key, new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f, patchiness, twilight, Taken: Start.AddMinutes(minute));

    private static ImmutableDictionary<string, LightGrade> Grades(params (string Path, LightGrade Grade)[] entries)
        => entries.ToImmutableDictionary(e => e.Path, e => e.Grade, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AClearGroupKeepsEveryFrame()
    {
        var grades = Grades([.. Enumerable.Range(0, 60).Select(i => ($"f{i}", Frame(i, 1f + (i % 5) * 0.01f)))]);

        CloudRejection.Rejected(grades).ShouldBeEmpty();
    }

    [Fact]
    public void APassingCloudAndADawnFrameAreLeftOutAndSayWhy()
    {
        var clear = Enumerable.Range(0, 60).Select(i => ($"f{i}", Frame(i, 1f))).ToArray();
        var cloud = Frame(30, 2.5f) with { Taken = Start.AddMinutes(30.5) };
        var dawn = Frame(61, 1f, TwilightPeriod.Dawn);
        var grades = Grades([.. clear, ("cloud", cloud), ("dawn", dawn)]);

        var rejected = CloudRejection.Rejected(grades);

        rejected.Keys.ShouldBe(["cloud", "dawn"], ignoreOrder: true);
        CloudRejection.InWords(rejected["cloud"]).ShouldStartWith("Cloud: the sky is 2.5x as patchy");
        CloudRejection.InWords(rejected["dawn"]).ShouldBe("taken past astronomical dark as the Sun rose");
        rejected["cloud"].ShouldBe(LightGrading.Verdict(grades, cloud), "the stacker's verdict is the viewer's");
    }

    /// <summary>
    /// A handful of frames gives the cloud verdict no baseline (a 4-frame group's clearest tenth is its one clearest frame), so cloud is
    /// judged only from <see cref="CloudRejection.MinGradedForCloud"/> graded frames; a night with fewer lights outside twilight is
    /// not judged for twilight either.
    /// </summary>
    [Fact]
    public void ASmallGroupIsNotJudgedForCloudNorTwilight()
    {
        var few = Enumerable.Range(0, CloudRejection.MinGradedForCloud - 3).Select(i => ($"f{i}", Frame(i, 1f))).ToArray();
        var grades = Grades([.. few, ("patchy", Frame(4, 2.5f)), ("dawn", Frame(5, 1f, TwilightPeriod.Dawn))]);

        CloudRejection.Rejected(grades).ShouldBeEmpty();
    }

    [Fact]
    public void ANightWithEnoughDarkLightsDropsItsTwilightLights()
    {
        var dark = Enumerable.Range(0, CloudRejection.MinGradedForCloud).Select(i => ($"f{i}", Frame(i, 1f))).ToArray();
        var grades = Grades([.. dark, ("dawn", Frame(40, 1f, TwilightPeriod.Dawn))]);

        var rejected = CloudRejection.Rejected(grades);

        rejected.Keys.ShouldBe(["dawn"]);
        rejected["dawn"].Causes.ShouldBe(GradeCause.Sunrise);
    }

    /// <summary>A sliver of dark (50.9N near the solstice) must not take the rest of the night with it.</summary>
    [Fact]
    public void ASliverOfDarkDoesNotDropTheRestOfTheNight()
    {
        var grades = Grades([.. Enumerable.Range(0, 20).Select(i =>
            ($"f{i}", Frame(i, 1f, i == 0 ? null : i < 10 ? TwilightPeriod.Dusk : TwilightPeriod.Dawn)))]);

        CloudRejection.Rejected(grades).ShouldBeEmpty();
    }

    /// <summary>The floor counts the lights the baselines are built from: twilight lights are not among them.</summary>
    [Fact]
    public void TwilightLightsDoNotLiftAThinNightOverTheSkyFloor()
    {
        LightGrade Night(int i, float sky, TwilightPeriod? twilight = null) => Frame(i, 1f, twilight) with
        {
            Night = new DateOnly(2026, 9, 10), SkyLevel = sky, Rig = "rig",
        };
        var entries = new[] { ("a", Night(0, 10f)), ("b", Night(1, 10f)), ("c", Night(2, 100f)) }
            .Concat(Enumerable.Range(3, 7).Select(i => ($"d{i}", Night(i, 10f, TwilightPeriod.Dusk)))).ToArray();

        CloudRejection.Rejected(Grades(entries)).ShouldBeEmpty();
    }

    /// <summary>
    /// A night that never reaches astronomical dark (50.9N at the June solstice: the Sun bottoms at -15.7 degrees) grades every light as
    /// twilight; none is judged for it, or the whole group would be dropped.
    /// </summary>
    [Fact]
    public void ANightWithNoAstronomicalDarkKeepsItsTwilightLights()
    {
        var grades = Grades([.. Enumerable.Range(0, 20).Select(i => ($"f{i}", Frame(i, 1f, i < 10 ? TwilightPeriod.Dusk : TwilightPeriod.Dawn)))]);

        CloudRejection.Rejected(grades).ShouldBeEmpty();
    }

    /// <summary>
    /// The night-sky baseline is one rig's night, so a group of 12 lights from 4 nights is not judged on the sky term (3 lights a night),
    /// (patchiness, read over the group, is judged separately).
    /// </summary>
    [Fact]
    public void AMultiNightGroupOfFewLightsPerNightIsNotJudgedOnTheSkyTerm()
    {
        LightGrade Night(int n, int i, float sky) => Frame(n * 600 + i, 1f) with
        {
            Night = new DateOnly(2026, 9, 1 + n), SkyLevel = sky, Rig = "rig", Taken = Start.AddDays(n).AddMinutes(i),
        };
        var entries = Enumerable.Range(0, 4)
            .SelectMany(n => Enumerable.Range(0, 3).Select(i => ($"n{n}_{i}", Night(n, i, n == 3 && i == 2 ? 100f : 10f)))).ToArray();

        CloudRejection.Rejected(Grades(entries)).ShouldBeEmpty();
    }
}
