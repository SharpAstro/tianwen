using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
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
    /// Patchiness is floored on the lights its baseline is built from: twilight lights are not among them, so seven dusk lights beside
    /// three dark ones (one patchy) leave a baseline of three, which judges nothing.
    /// </summary>
    [Fact]
    public void TwilightLightsDoNotLiftAThinNightOverThePatchinessFloor()
    {
        var entries = new[] { ("a", Frame(0, 1f)), ("b", Frame(1, 1f)), ("c", Frame(2, 2.5f)) }
            .Concat(Enumerable.Range(3, 7).Select(i => ($"d{i}", Frame(i, 1f, TwilightPeriod.Dusk)))).ToArray();

        CloudRejection.Rejected(Grades(entries)).ShouldBeEmpty();
    }

    /// <summary>
    /// The patchiness baseline is a rig's, so one group holding two rigs (a camera on two OTAs) counts each rig's lights apart: six
    /// lights of each rig are twelve in the group but six per baseline, and judge nothing.
    /// </summary>
    [Fact]
    public void AGroupOfTwoRigsCountsEachRigApartForPatchiness()
    {
        var entries = Enumerable.Range(0, 6).SelectMany(i => new[]
        {
            ($"a{i}", Frame(i, i == 5 ? 2.5f : 1f) with { Rig = "otaA" }),
            ($"b{i}", Frame(i, 1f) with { Rig = "otaB" }),
        }).ToArray();

        CloudRejection.Rejected(Grades(entries)).ShouldBeEmpty();
    }

    /// <summary>
    /// Twilight is judged per night: a night with ten dark lights drops its dawn light, while the same group's other night of three
    /// dark lights and two dusk lights keeps them (a single total of thirteen dark lights would drop those too).
    /// </summary>
    [Fact]
    public void TheTwilightGateIsPerNight()
    {
        var full = new DateOnly(2026, 9, 10);
        var thin = new DateOnly(2026, 9, 11);
        LightGrade On(DateOnly night, int minute, TwilightPeriod? twilight = null)
            => Frame(minute, 1f, twilight) with { Night = night, Taken = Start.AddDays(night.DayNumber - full.DayNumber).AddMinutes(minute) };
        var entries = Enumerable.Range(0, CloudRejection.MinGradedForCloud).Select(i => ($"f{i}", On(full, i)))
            .Append(("fullDawn", On(full, 40, TwilightPeriod.Dawn)))
            .Concat(Enumerable.Range(0, 3).Select(i => ($"t{i}", On(thin, i))))
            .Concat(Enumerable.Range(3, 2).Select(i => ($"tDusk{i}", On(thin, i, TwilightPeriod.Dusk)))).ToArray();

        CloudRejection.Rejected(Grades(entries)).Keys.ShouldBe(["fullDawn"]);
    }

    /// <summary>
    /// A run cloudy from its first light to its last is its own darkest tenth and reads clear against itself; read through the
    /// rig's clear-night references (the viewer's store, #1976) it is cloud, and the stacker leaves it out as the viewer marks it.
    /// </summary>
    [Fact]
    public void ACloudyThroughoutRunIsRejectedAgainstTheRigsReference()
    {
        var night = new DateOnly(2026, 7, 6);
        var entries = Enumerable.Range(0, 10).Select(i => ($"f{i}", Frame(i, 1f) with { Night = night, SkyLevel = 2200f + i * 20, Rig = "" })).ToArray();
        var grades = Grades(entries);
        ImmutableArray<LightGradeReference> references =
            [new LightGradeReference(Key, "", double.NaN, double.NaN, new DateOnly(2026, 6, 1), default, 1000f)];

        CloudRejection.Rejected(grades).ShouldBeEmpty("against itself it reads clear");
        var rejected = CloudRejection.Rejected(grades, references);

        rejected.Count.ShouldBe(10);
        rejected.Values.ShouldAllBe(v => v.Causes == GradeCause.Cloud && v.ReferenceNight == new DateOnly(2026, 6, 1));
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

    /// <summary>
    /// #1974: the stacker never reads a night against its own fitted sky (the viewer may; the stacker and the session's judge take
    /// the stored model only), so a thin veil over the last 34 of 300 frames rejects nothing, and a frame rejected inside it for its
    /// patchiness carries no expected-sky excess. With the night's self-fit read, that frame's verdict would carry the veil's sigma.
    /// </summary>
    /// <remarks>
    /// The contrast is what keeps this from passing on its own: "the stacker rejects nothing for a veil" is true whenever nobody can see
    /// the veil, so the viewer's verdict of the last veiled frame is asserted to be Cloud by the veil alone (its patchiness is as clear as
    /// any), worded against the night's own frames, while the stacker leaves that very frame in. The words of the patchy frame cannot
    /// carry the veil in either case (the veil's wording needs the frame NOT patchy), so what pins that frame is its NaN sigmas.
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task TheStackerDoesNotReadAVeilAgainstTheNightsOwnSky()
    {
        var frames = LightGradingTests.SyntheticSkyNight(300, veilFrom: 266, objectName: "stacker-veil")
            .Select(static g => g with { Patchiness = 1f }).ToArray();
        frames[290] = frames[290] with { Patchiness = 2.5f };
        var grades = LightGradingTests.Table(frames);

        await LightGradingTests.AwaitSelfFitAsync(grades, "stacker-veil", TestContext.Current.CancellationToken);
        LightGrading.Verdict(grades, grades["f290"]).LevelSigma.ShouldBeGreaterThan(0f, "the viewer's self-fit sees the veil");
        var viewer = LightGrading.Verdict(grades, grades["f299"]);
        (viewer.Causes & GradeCause.Cloud).ShouldNotBe(GradeCause.None, "the viewer calls the last veiled frame cloud by the veil alone");
        viewer.ExpectedSkySigma.ShouldBeGreaterThanOrEqualTo(LightGrading.ExpectedSkyCloudFrom);
        LightGrading.CloudInWords(viewer).ShouldContain("this night's own frames");

        var rejected = CloudRejection.Rejected(grades);

        rejected.Keys.ShouldBe(["f290"], "the veil alone rejects nothing");
        rejected.ContainsKey("f299").ShouldBeFalse("the frame the viewer calls veiled is left in the stack");
        rejected["f290"].LevelSigma.ShouldBe(float.NaN);
        rejected["f290"].GradientSigma.ShouldBe(float.NaN);
        CloudRejection.InWords(rejected["f290"]).ShouldStartWith("Cloud: the sky is 2.5x as patchy");
    }
}
