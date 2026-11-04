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
}
