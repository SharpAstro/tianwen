using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TwilightPeriod = TianWen.Lib.Sequencing.TwilightPeriod;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The blink's grading: each frame scored as the stacker scores a reference candidate, relative to the best of ITS group
/// seen so far, and shown as a tint on its file-list row and in the info strip's Grade section.
/// </summary>
[Collection("UI")]
public class LightGradingTests
{
    private static Image Frame(Filter filter, double exposureSeconds = 300, string objectName = "")
        => new Image([new float[4, 4]], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("FLI", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(exposureSeconds), FrameType.Light, "",
                9f, 9f, 530, FocuserPositions.None, filter, 1, 1, -25f, SensorType.Monochrome, 0, 0,
                RowOrder.TopDown, float.NaN, float.NaN) { ObjectName = objectName });

    private static LightGrade Graded(Filter filter, float score, double exposureSeconds = 300)
        => new(LightGradeKey.Of(Frame(filter, exposureSeconds)), new FrameMetrics(2f, 2.2f, 0.1f, 1000), score);

    private static ImmutableDictionary<string, LightGrade> Grades(params (string Path, LightGrade Grade)[] entries)
        => entries.ToImmutableDictionary(e => e.Path, e => e.Grade, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AGradeIsTheScoreAsAShareOfItsGroupsBest()
    {
        var best = Graded(Filter.Luminance, 10f);
        var half = Graded(Filter.Luminance, 5f);
        var grades = Grades(("a", best), ("b", half));

        LightGrading.Relative(grades, best).ShouldBe(1f);
        LightGrading.Relative(grades, half).ShouldBe(0.5f);
    }

    private static LightGrade Patchy(float score, float patchiness, TwilightPeriod? twilight = null)
        => Graded(Filter.Luminance, score) with { Patchiness = patchiness, Twilight = twilight };

    /// <summary>
    /// A frame is scored on its stars over <see cref="LightGrading.ScoredSnrFloor"/>, so how deep the detection ladder read it does
    /// not move its score: on 2026-09-10 a frame that retried ended with 4,346 stars and its neighbour that did not with 1,737, and
    /// the few that retried were the night's best by 2.5x. The same bright stars with 3,000 faint ones added score the same.
    /// </summary>
    [Fact]
    public void HowDeepTheDetectionReadDoesNotMoveTheScore()
    {
        static ImagedStar Star(int i, float snr) => new(HFD: 2.5f, StarFWHM: 2.2f, SNR: snr, Flux: snr * 100f, XCentroid: i, YCentroid: i, Ellipticity: 0.4f);
        var bright = Enumerable.Range(0, 1300).Select(i => Star(i, 40f + i % 200)).ToArray();
        var shallow = new StarList(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>([.. bright, .. Enumerable.Range(0, 400).Select(i => Star(i, 15f))]));
        var deep = new StarList(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>([.. bright, .. Enumerable.Range(0, 3000).Select(i => Star(i, 12f))]));

        LightGrading.ScoredStars(shallow).Count.ShouldBe(1300);
        LightGrading.ScoredStars(deep).Count.ShouldBe(1300);

        // Through Grade itself, the one line that changed what a frame is scored on.
        var image = Frame(Filter.Luminance);
        var a = LightGrading.Grade(image, shallow).ShouldNotBeNull();
        var b = LightGrading.Grade(image, deep).ShouldNotBeNull();
        a.Metrics.StarCount.ShouldBe(1300);
        b.Metrics.StarCount.ShouldBe(a.Metrics.StarCount);
        b.Score.ShouldBe(a.Score);

        var sparse = new StarList(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>([.. Enumerable.Range(0, 5).Select(i => Star(i, 50f)), Star(9, 12f)]));
        LightGrading.ScoredStars(sparse).Count.ShouldBe(6, "a field with fewer stars than the minimum is scored on all of them");
    }

    /// <summary>
    /// Near the minimum the score stays continuous: under <see cref="LightGrading.MinScoredStars"/> strong stars a frame is scored on
    /// that many of its brightest, never on every faint one, so 19 strong stars beside 280 faint ones score about as 21 strong ones.
    /// </summary>
    [Fact]
    public void AFrameJustUnderTheMinimumIsScoredOnItsBrightestNotOnEveryStar()
    {
        static ImagedStar Star(int i, float snr) => new(HFD: 2.5f, StarFWHM: 2.2f, SNR: snr, Flux: snr * 100f, XCentroid: i, YCentroid: i, Ellipticity: 0.4f);
        var under = new StarList(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>([.. Enumerable.Range(0, 19).Select(i => Star(i, 60f)), .. Enumerable.Range(0, 280).Select(i => Star(i, 12f))]));
        var over = new StarList(new System.Collections.Concurrent.ConcurrentBag<ImagedStar>([.. Enumerable.Range(0, 21).Select(i => Star(i, 60f))]));

        LightGrading.ScoredStars(under).Count.ShouldBe(LightGrading.MinScoredStars);
        LightGrading.ScoredStars(under).Count(st => st.SNR >= LightGrading.ScoredSnrFloor).ShouldBe(19, "the brightest are taken first");
        LightGrading.ScoredStars(over).Count.ShouldBe(21);
    }

    /// <summary>
    /// The yardstick is the best CLEAR frame: on 2026-09-10 the best score was a cloud frame (thin cloud took the faint, broad
    /// stars and left a tighter median), and every clear frame read as a share of it. With no clear frame the plain best stands.
    /// </summary>
    [Fact]
    public void ACloudFrameWithTheBestScoreIsNotTheYardstick()
    {
        var clear = Enumerable.Range(0, 10).Select(i => ($"c{i}", Patchy(8f, 1f + i * 0.01f))).ToArray();
        var cloud = Patchy(10f, 3f);
        var grades = Grades([.. clear, ("cloud", cloud)]);

        (LightGrading.Verdict(grades, cloud).Causes & GradeCause.Cloud).ShouldNotBe(GradeCause.None, "the outlier must read as cloud");
        LightGrading.Relative(grades, clear[0].Item2).ShouldBe(1f, "the best clear frame is the yardstick");
        LightGrading.Relative(grades, cloud).ShouldBe(1f, "a share is capped at 1");

        var allCloud = Grades(("a", Patchy(10f, 3f)), ("b", Patchy(5f, 3f)));
        LightGrading.Relative(allCloud, allCloud["b"]).ShouldBe(0.5f, "with no clear frame the plain best stands");
    }

    /// <summary>
    /// #1951: a frame through cloud scored as well as a clear one (its stars are as many, as sharp and as round), so a sharp
    /// cloudy frame read green. Its patchy sky now holds the colour at warn, at poor when heavy, and says why.
    /// </summary>
    [Fact]
    public void ASharpFrameThroughCloudCannotReadGood()
    {
        var clear = Enumerable.Range(0, 10).Select(i => ($"c{i}", Patchy(10f, 1f + i * 0.01f))).ToArray();
        var thin = Patchy(10f, 2.5f);
        var heavy = Patchy(10f, 9f);
        var grades = Grades([.. clear, ("thin", thin), ("heavy", heavy)]);

        var thinVerdict = LightGrading.Verdict(grades, thin);
        thinVerdict.Relative.ShouldBe(1f, "its stars are as good as the best's");
        thinVerdict.Causes.ShouldBe(GradeCause.Cloud);
        thinVerdict.Shown.ShouldBeLessThan(LightGrading.GoodFrom);
        thinVerdict.Shown.ShouldBeGreaterThan(LightGrading.PoorBelow);

        var heavyVerdict = LightGrading.Verdict(grades, heavy);
        heavyVerdict.Shown.ShouldBe(LightGrading.PoorBelow);
        heavyVerdict.Patchiness.ShouldBe(9f, 0.1f);
        LightGrading.Verdict(grades, clear[0].Item2).Causes.ShouldBe(GradeCause.None);
    }

    /// <summary>
    /// 2026-09-10: the Helix rose from 47 to 64 degrees and its sky's patchiness fell smoothly from 1.73 to 1.29 times its
    /// clearest over 90 minutes, which the clearest-tenth test alone read as cloud on 72 of 176 clear frames. A frame as patchy
    /// as the frames either side of it is its altitude; one patchier than they are, in the middle of the same climb, is cloud.
    /// </summary>
    [Fact]
    public void ARisingTargetsSkyIsNotCloudButAPassingCloudStillIs()
    {
        var start = new DateTimeOffset(2026, 9, 10, 10, 30, 0, TimeSpan.Zero);
        // Five hours of one frame a minute, as measured: 1.73 falling to 1.29 over the first 90 minutes, on to 1.0 over the
        // next 90, then level.
        static float Climb(int i) => i < 90 ? 1.73f - 0.44f * i / 90f : i < 180 ? 1.29f - 0.29f * (i - 90) / 90f : 1f;
        var frames = Enumerable.Range(0, 300)
            .Select(i => ($"f{i}", Patchy(10f, Climb(i)) with { Taken = start.AddMinutes(i) }))
            .ToArray();
        var passing = Patchy(10f, 2f) with { Taken = start.AddMinutes(200.5) };
        var grades = Grades([.. frames, ("passing", passing)]);

        var first = LightGrading.Verdict(grades, frames[0].Item2);
        first.Patchiness.ShouldBeGreaterThan(LightGrading.CloudFrom, "against the night's clearest it is patchy");
        first.LocalPatchiness.ShouldBeLessThan(LightGrading.LocalCloudFrom, "against its neighbours it is not");
        first.Causes.ShouldBe(GradeCause.None);
        frames.Count(f => LightGrading.Verdict(grades, f.Item2).Causes.HasFlag(GradeCause.Cloud)).ShouldBe(0);

        var verdict = LightGrading.Verdict(grades, passing);
        verdict.Causes.ShouldBe(GradeCause.Cloud);
        verdict.LocalPatchiness.ShouldBe(2f, 0.01f);
    }

    /// <summary>
    /// A frame past astronomical dark reads poor however good its stars, says which twilight, and is left out of the
    /// patchiness baseline: a bright sky's gradient is not the dark sky the group is read against.
    /// </summary>
    [Fact]
    public void ATwilightFrameReadsPoorAndStaysOutOfTheBaseline()
    {
        var dark = Patchy(10f, 1f);
        var dawn = Patchy(10f, 0.2f, TwilightPeriod.Dawn);
        var dusk = Patchy(10f, 1f, TwilightPeriod.Dusk);
        var grades = Grades(("dark", dark), ("dawn", dawn), ("dusk", dusk));

        LightGrading.Verdict(grades, dawn).Causes.ShouldBe(GradeCause.Sunrise);
        LightGrading.Verdict(grades, dusk).Causes.ShouldBe(GradeCause.Sunset);
        LightGrading.Verdict(grades, dawn).Shown.ShouldBe(LightGrading.PoorBelow);
        LightGrading.RelativePatchiness(grades, dark).ShouldBe(1f, "the dawn frame's 0.2 is not the baseline");
    }

    [Fact]
    public void AFrameWithoutAPatchinessReadIsJudgedByItsStarsAlone()
    {
        var unread = Graded(Filter.Luminance, 5f);
        var grades = Grades(("best", Graded(Filter.Luminance, 10f)), ("unread", unread));

        var verdict = LightGrading.Verdict(grades, unread);
        verdict.Patchiness.ShouldBe(float.NaN);
        verdict.Causes.ShouldBe(GradeCause.None);
        verdict.Shown.ShouldBe(0.5f);
    }

    /// <summary>
    /// A blue sub of a field shows a fraction of the luminance sub's stars, so a set graded as one group would read every
    /// blue frame as poor: a group is one filter at one exposure.
    /// </summary>
    [Fact]
    public void FramesThroughAnotherFilterOrExposureAreGradedApart()
    {
        var luminance = Graded(Filter.Luminance, 10f);
        var blue = Graded(Filter.Blue, 2f);
        var shortLuminance = Graded(Filter.Luminance, 3f, exposureSeconds: 60);
        var grades = Grades(("l", luminance), ("b", blue), ("s", shortLuminance));

        LightGrading.Relative(grades, blue).ShouldBe(1f, "the best blue frame is its group's best");
        LightGrading.Relative(grades, shortLuminance).ShouldBe(1f, "a 60 s frame is not held to a 300 s one");
        blue.Key.Describe().ShouldBe("Blue, 300 s");
    }

    /// <summary>
    /// A target cloudy from first frame to last cannot be seen against its own group, whose clearest frames are cloud too, and
    /// is plain against the night: the 2026-07-06 LMC run sat at 1.89 to 3.17 times the night's darkest sky across targets.
    /// </summary>
    [Fact]
    public void AWholeTargetUnderCloudReadsAgainstTheNightsDarkestSky()
    {
        var night = new DateOnly(2026, 7, 6);
        LightGrade On(string target, float sky) => new(
            new LightGradeKey(4, 4, SensorType.Monochrome, "", target, 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: 1f, SkyLevel: sky, Night: night);
        var clear = Enumerable.Range(0, 20).Select(i => ($"lagoon{i}", On("LAGOON", 900f + i * 10))).ToArray();
        var lmc = Enumerable.Range(0, 10).Select(i => ($"lmc{i}", On("LMC", 2200f + i * 20))).ToArray();
        var grades = Grades([.. clear, .. lmc]);

        var cloudy = LightGrading.Verdict(grades, lmc[0].Item2);
        cloudy.NightSky.ShouldBeGreaterThan(LightGrading.NightSkyCloudFrom);
        cloudy.Causes.ShouldBe(GradeCause.Cloud);
        cloudy.Shown.ShouldBe(LightGrading.PoorBelow);
        LightGrading.CloudInWords(cloudy).ShouldStartWith("Cloud: the sky is 2.4x as bright as the night's darkest");
        LightGrading.Relative(grades, lmc[0].Item2).ShouldBe(1f, "against its own group the LMC frame is as good as any");
        LightGrading.Verdict(grades, clear[19].Item2).Causes.ShouldBe(GradeCause.None);

        // Another night is another baseline: the same bright sky alone on its night is its own darkest.
        var otherNight = lmc[0].Item2 with { Night = night.AddDays(1) };
        LightGrading.RelativeNightSky(Grades(("other", otherNight)), otherNight).ShouldBe(1f);
    }

    /// <summary>The Sun is a twilight frame's only cause: a bright sky and a gradient are what twilight looks like, never also cloud.</summary>
    [Fact]
    public void ATwilightFrameWithABrightSkyIsNotAlsoCloud()
    {
        var night = new DateOnly(2026, 7, 6);
        LightGrade On(float sky, TwilightPeriod? twilight = null) => new(
            new LightGradeKey(4, 4, SensorType.Monochrome, "", "T", 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: twilight is null ? 1f : 9f, Twilight: twilight, SkyLevel: sky, Night: night);
        var dark = Enumerable.Range(0, 20).Select(i => ($"d{i}", On(1000f + i * 5))).ToArray();
        var dawn = On(2600f, TwilightPeriod.Dawn);
        var grades = Grades([.. dark, ("dawn", dawn)]);

        var verdict = LightGrading.Verdict(grades, dawn);
        verdict.Causes.ShouldBe(GradeCause.Sunrise);
        verdict.Shown.ShouldBe(LightGrading.PoorBelow);
    }

    /// <summary>A sky level in counts scales with gain, offset and telescope: a rig's frames are read against that rig's own night.</summary>
    [Fact]
    public void TwoRigsOfOneNightAreNotReadAgainstEachOther()
    {
        var night = new DateOnly(2026, 7, 6);
        LightGrade On(float sky, string rig) => new(
            new LightGradeKey(4, 4, SensorType.Monochrome, "", "T", 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: 1f, SkyLevel: sky, Night: night, Rig: rig);
        var low = Enumerable.Range(0, 20).Select(i => ($"a{i}", On(1000f + i, "100|50|ASI533|A"))).ToArray();
        var high = Enumerable.Range(0, 20).Select(i => ($"b{i}", On(2000f + i, "200|50|ASI533|B"))).ToArray();
        var grades = Grades([.. low, .. high]);

        LightGrading.Verdict(grades, high[0].Item2).Causes.ShouldBe(GradeCause.None, "its own rig's darkest is its own");
        LightGrading.RelativeNightSky(grades, high[0].Item2).ShouldBeLessThan(1.1f);
    }

    /// <summary>A Canon raw has no gain or offset, only an ISO: two ISOs of one night are two rigs, in the night's sky and in the patchiness baselines.</summary>
    [Fact]
    public void TwoIsosOfOneNightAreTwoRigs()
    {
        var t = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var meta = new ImageMeta("Canon EOS R", t, TimeSpan.FromSeconds(30), FrameType.Light, "", 3.76f, 3.76f, 135,
            FocuserPositions.None, Filter.None, 1, 1, -5f, SensorType.RGGB, 0, 0, RowOrder.TopDown, -37.876f, 145.178f);
        var rig800 = LightGrading.RigOf(meta with { Iso = 800 });
        var rig3200 = LightGrading.RigOf(meta with { Iso = 3200 });
        rig800.ShouldNotBe(rig3200);

        var night = new DateOnly(2026, 7, 6);
        LightGrade On(float sky, float patchiness, string rig) => new(
            new LightGradeKey(4, 4, SensorType.RGGB, "", "T", 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: patchiness, SkyLevel: sky, Night: night, Rig: rig);
        var low = Enumerable.Range(0, 20).Select(i => ($"a{i}", On(1000f + i, 1f, rig800))).ToArray();
        var high = Enumerable.Range(0, 20).Select(i => ($"b{i}", On(4000f + i, 4f, rig3200))).ToArray();
        var grades = Grades([.. low, .. high]);

        LightGrading.Verdict(grades, high[0].Item2).Causes.ShouldBe(GradeCause.None, "ISO 3200 is read against ISO 3200's own night and patchiness");
        LightGrading.RelativePatchiness(grades, high[0].Item2).ShouldBeLessThan(1.1f);
    }

    [Fact]
    public void AFramesNightIsItsLocalEveningEitherSideOfMidnight()
    {
        static ImageMeta At(DateTimeOffset t) => new("ASI533", t, TimeSpan.FromSeconds(30), FrameType.Light, "", 3.76f, 3.76f, 135,
            FocuserPositions.None, Filter.None, 1, 1, -5f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, -37.876f, 145.178f);

        // Melbourne (UTC+10 by its longitude): 22:00 and 02:00 local are the evening of the 6th, 13:00 the next day is the 7th.
        LightGrading.NightOf(At(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero))).ShouldBe(new DateOnly(2026, 7, 6));
        LightGrading.NightOf(At(new DateTimeOffset(2026, 7, 6, 16, 0, 0, TimeSpan.Zero))).ShouldBe(new DateOnly(2026, 7, 6));
        LightGrading.NightOf(At(new DateTimeOffset(2026, 7, 7, 3, 0, 0, TimeSpan.Zero))).ShouldBe(new DateOnly(2026, 7, 7));
        LightGrading.NightOf(At(default)).ShouldBe(default(DateOnly));
    }

    private static LightGrade Run(string target, float sky, float patchiness, DateOnly night, DateTimeOffset taken = default)
        => new(new LightGradeKey(4, 4, SensorType.Monochrome, "", target, 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: patchiness, SkyLevel: sky, Night: night, Taken: taken);

    private static ImmutableArray<LightGradeReference> ClearReference(string target, DateOnly night, float sky, float clearest, string rig = "")
        => [new LightGradeReference(new LightGradeKey(4, 4, SensorType.Monochrome, "", target, 30_000, 1), rig, double.NaN, double.NaN, night,
            [new LightGradeObjectClearest(target, clearest)], sky)];

    /// <summary>
    /// #1976: the 2026-07-06 LMC run alone in the table (the folder on show) is its own darkest tenth and reads clear. Given a clear
    /// reference of the same rig from another night it is Cloud, and the verdict names the night it was judged against.
    /// </summary>
    [Fact]
    public void ACloudyRunAloneIsJudgedAgainstTheRigsClearReference()
    {
        var night = new DateOnly(2026, 7, 6);
        var referenceNight = new DateOnly(2026, 6, 1);
        var lmc = Enumerable.Range(0, 10).Select(i => ($"lmc{i}", Run("LMC", 2200f + i * 20, 1.9f + i * 0.14f, night))).ToArray();
        var grades = Grades(lmc);
        var references = ClearReference("LMC", referenceNight, 1000f, 1f);

        LightGrading.Verdict(grades, lmc[0].Item2).NightSky.ShouldBeInRange(0.9f, 1.1f, "against itself it is its own darkest");
        foreach (var (_, frame) in lmc)
        {
            var verdict = LightGrading.Verdict(grades, frame, references);
            verdict.Causes.ShouldBe(GradeCause.Cloud);
            verdict.ReferenceNight.ShouldBe(referenceNight);
            verdict.NightSky.ShouldBeGreaterThanOrEqualTo(LightGrading.NightSkyCloudFrom);
            LightGrading.CloudInWords(verdict).ShouldContain("judged against 2026-06-01");
        }
    }

    /// <summary>
    /// A reference read from a hand-edited <c>clear-sky.json</c> may carry no objects at all (a default array): it still grades,
    /// the sky ratio alone deciding.
    /// </summary>
    [Fact]
    public void AReferenceWithNoObjectsStillJudgesTheSky()
    {
        var night = new DateOnly(2026, 7, 6);
        var referenceNight = new DateOnly(2026, 6, 1);
        var lmc = Enumerable.Range(0, 10).Select(i => ($"lmc{i}", Run("LMC", 2200f + i * 20, 1f, night))).ToArray();
        var grades = Grades(lmc);
        ImmutableArray<LightGradeReference> references =
            [new LightGradeReference(new LightGradeKey(4, 4, SensorType.Monochrome, "", "LMC", 30_000, 1), "", double.NaN, double.NaN,
                referenceNight, default, 1000f)];

        var verdict = LightGrading.Verdict(grades, lmc[0].Item2, references);

        verdict.Causes.ShouldBe(GradeCause.Cloud);
        verdict.ReferenceNight.ShouldBe(referenceNight);
    }

    /// <summary>
    /// With no clear night of the rig on file, a run is judged for cloud on its own baselines alone: the verdict says so and its
    /// colour is exactly the run-relative one, so a run cloudy throughout is not called clear without a warning.
    /// </summary>
    [Fact]
    public void ARunWithNoReferenceForItsRigSaysItIsJudgedOnThatNightAlone()
    {
        var night = new DateOnly(2026, 7, 6);
        var lmc = Enumerable.Range(0, 10).Select(i => ($"lmc{i}", Run("LMC", 2200f + i * 20, 1f, night))).ToArray();
        var grades = Grades(lmc);
        var otherRig = ClearReference("LMC", new DateOnly(2026, 6, 1), 1000f, 1f, rig: "200|50|OTHER|X");

        var plain = LightGrading.Verdict(grades, lmc[0].Item2);
        var verdict = LightGrading.Verdict(grades, lmc[0].Item2, otherRig);
        verdict.CloudUnjudged.ShouldBeTrue();
        verdict.ReferenceNight.ShouldBe(default);
        verdict.Shown.ShouldBe(plain.Shown);
        verdict.Causes.ShouldBe(plain.Causes);
        plain.CloudUnjudged.ShouldBeFalse("references not asked");
        LightGrading.CloudUnjudgedInWords.ShouldContain("judged on this night alone");
    }

    /// <summary>A night whose own darkest tenth is plausible keeps today's numbers to the bit when a reference is supplied.</summary>
    [Fact]
    public void AMixedNightKeepsItsNumbersWhenAReferenceIsSupplied()
    {
        var night = new DateOnly(2026, 7, 6);
        var clear = Enumerable.Range(0, 20).Select(i => ($"lagoon{i}", Run("LAGOON", 900f + i * 10, 1f, night))).ToArray();
        var lmc = Enumerable.Range(0, 10).Select(i => ($"lmc{i}", Run("LMC", 2200f + i * 20, 1f, night))).ToArray();
        var grades = Grades([.. clear, .. lmc]);
        var references = ClearReference("LMC", new DateOnly(2026, 6, 1), 1000f, 1f);

        foreach (var (_, frame) in clear.Concat(lmc))
        {
            var with = LightGrading.Verdict(grades, frame, references);
            with.ReferenceNight.ShouldBe(default);
            with.CloudUnjudged.ShouldBeFalse();
            with.ShouldBe(LightGrading.Verdict(grades, frame));
        }
    }

    /// <summary>
    /// A run that turns cloudy for 3 hours (one frame a minute, patchiness 1 to 3, sky 1.5x, under the night-sky threshold so only
    /// the local rule can see it): the middle of the stretch is its own local floor, which the plateau rule refuses, so every cloudy
    /// frame is flagged and none of the clear hours.
    /// </summary>
    [Fact]
    public void ALongCloudyStretchCannotSetItsOwnLocalFloor()
    {
        var night = new DateOnly(2026, 7, 6);
        var start = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero);
        bool Cloudy(int i) => i is >= 300 and < 480;
        var frames = Enumerable.Range(0, 600)
            .Select(i => ($"f{i}", Run("T", Cloudy(i) ? 1500f : 1000f, Cloudy(i) ? 3f : 1f, night, start.AddMinutes(i))))
            .ToArray();
        var grades = Grades(frames);

        var wrong = Enumerable.Range(0, 600)
            .Where(i => LightGrading.Verdict(grades, frames[i].Item2).Causes.HasFlag(GradeCause.Cloud) != Cloudy(i))
            .ToArray();
        wrong.ShouldBeEmpty();
        LightGrading.LocalPatchiness(grades, frames[390].Item2).ShouldBe(3f, 0.01f, "the middle is read against the clear part, not itself");
    }

    /// <summary>
    /// 100,000 frames in one group (one a minute) build their baselines in one pass, plateau test included: the bound is the
    /// test's timeout, so a scan of the group per frame cannot creep back. The plateau test's two side windows scanned over the
    /// whole group are about 20 billion comparisons (n squared, twice): with that scan put back the test timed out at 20 s,
    /// while the binary search passes in under a second (both measured on the desktop), so the bound fails the scan and
    /// passes the search with a wide margin on a slower runner too.
    /// </summary>
    [Fact(Timeout = 20_000)]
    public async Task ALargeGroupBuildsItsBaselinesInTime()
    {
        var night = new DateOnly(2026, 7, 6);
        var start = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero);
        var frames = Enumerable.Range(0, 100_000)
            .Select(i => ($"f{i}", Run("T", 1000f, 1f, night, start.AddMinutes(i))))
            .ToArray();
        var grades = Grades(frames);

        var local = await Task.Run(() => LightGrading.LocalPatchiness(grades, frames[50_000].Item2), TestContext.Current.CancellationToken);

        local.ShouldBe(1f, 0.01f);
    }

    [Fact]
    public void AFrameWithNoStarsIsNotGraded()
        => LightGrading.Grade(Frame(Filter.Luminance), StarList.Empty).ShouldBeNull();

    /// <summary>
    /// A light taken in daylight has no stars because the sky is bright, and is graded at zero with its twilight, so it reads
    /// poor and carries the sun mark: the last dawn frames of 2026-05-14, rejected by hand, were otherwise left as nothing.
    /// A flat has no stars by design and stays ungraded whatever the Sun does.
    /// </summary>
    [Fact]
    public void ADaylightLightWithNoStarsIsGradedPoorWithItsTwilight()
    {
        // Melbourne, 2026-05-15 06:55 local: past astronomical dark, the Sun rising.
        var dawn = new DateTimeOffset(2026, 5, 15, 6, 55, 0, TimeSpan.FromHours(10));
        Image At(FrameType type) => new([new float[4, 4]], BitDepth.Int16, 65535f, 0f, 0f,
            new ImageMeta("ASI533", dawn, TimeSpan.FromSeconds(30), type, "", 3.76f, 3.76f, 135, FocuserPositions.None,
                Filter.None, 1, 1, -5f, SensorType.Monochrome, 0, 0, RowOrder.TopDown, -37.876f, 145.178f));

        var grade = LightGrading.Grade(At(FrameType.Light), StarList.Empty) ?? throw new InvalidOperationException("a daylight light is graded");
        grade.Twilight.ShouldBe(TwilightPeriod.Dawn);
        var verdict = LightGrading.Verdict(Grades(("dawn", grade)), grade);
        verdict.Causes.ShouldBe(GradeCause.Sunrise);
        verdict.Shown.ShouldBeLessThanOrEqualTo(LightGrading.PoorBelow);

        LightGrading.Grade(At(FrameType.Flat), StarList.Empty).ShouldBeNull("a sky flat at dawn is not a bad light");
    }

    [Fact]
    public void TheColourRunsFromErrorThroughWarnToSuccess()
    {
        var palette = GuiTheme.DarkPalette;

        LightGradeColours.Colour(palette, 0.2f).ShouldBe(palette.Error);
        LightGradeColours.Colour(palette, LightGrading.PoorBelow).ShouldBe(palette.Error);
        LightGradeColours.Colour(palette, (LightGrading.PoorBelow + LightGrading.GoodFrom) / 2f).ShouldBe(palette.Warn);
        LightGradeColours.Colour(palette, LightGrading.GoodFrom).ShouldBe(palette.Success);
        LightGradeColours.Colour(palette, 1f).ShouldBe(palette.Success);
    }

    [Fact]
    public void AFileListEntryFindsItsGradeThroughTheFolderOnShow()
    {
        var folder = Path.Combine(Path.GetTempPath(), "graded");
        var state = new ViewerState { CurrentFolder = folder };
        var grade = Graded(Filter.Red, 4f);

        state.RecordFrameGrade(Path.Combine(folder, "light_001.fits"), grade);

        state.GradeOf("LIGHT_001.FITS").ShouldBe(grade, "a path is matched as the file system matches it on Windows");
        state.GradeOf("light_002.fits").ShouldBeNull();
    }

    [Fact]
    public async Task TheGradeSectionSaysTheShareTheMetricsAndTheBest()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var state = new ViewerState();

        InfoPanelData.GetGradeReadout(state, document)?.Relative.ShouldBeNull("not graded until its stars are in");

        var key = LightGradeKey.Of(document.UnstretchedImage);
        state.RecordFrameGrade(document.FilePath, new LightGrade(key, new FrameMetrics(2.5f, 2.8f, 0.12f, 800), 4f));
        state.RecordFrameGrade(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(document.FilePath))!, "best.fits"),
            new LightGrade(key, new FrameMetrics(2f, 2.2f, 0.1f, 1000), 8f));

        var readout = InfoPanelData.GetGradeReadout(state, document) ?? throw new InvalidOperationException("a file has a grade section");
        readout.Relative.ShouldBe(0.5f);
        readout.Lines[0].ShouldBe("Quality: 50% of the best of 2 (0 s)");
        readout.Lines[1].ShouldBe("Scored stars: 800, HFD 2.50, FWHM 2.80, ecc 0.12");
        readout.BestName.ShouldBe("best.fits");
    }

    /// <summary>
    /// Past the 1.20 local bar by no more than its neighbours' jitter, a frame needs a second sign: on 2026-09-10 nine clear frames
    /// sat 1.20 to 1.24 against their floor with their stars at 0.995 of their neighbours'. The same frame losing a third of its stars
    /// is cloud, and a marginal frame inside a cloud run joins it (the owner's backtracking): cloud creeps in and fades out.
    /// </summary>
    [Fact]
    public void AMarginalFrameIsCloudOnlyWithASecondSignOrBesideConfirmedCloud()
    {
        var start = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero);
        // An evening of steady frames at 1.10 (a rising target's sky, 1.4x the night's clearest) with the 1.5 % random jitter
        // 2026-09-10 measured (seeded: a periodic pattern's successive differences are nearly all equal, a jitter of zero), then a
        // clear stretch at 0.80 that sets the clearest.
        LightGrade At(int minute, float patchiness, int stars = 1000)
            => Patchy(10f, patchiness) with { Taken = start.AddMinutes(minute), Metrics = new FrameMetrics(2f, 2.2f, 0.1f, stars) };
        var random = new Random(7);
        var frames = new System.Collections.Generic.List<(string, LightGrade)>();
        for (var m = 0; m < 240; m++)
        {
            var jitter = 0.015f * (random.NextSingle() + random.NextSingle() + random.NextSingle() - 1.5f) * 2f;
            frames.Add(($"f{m}", At(m, m < 160 ? 1.10f * (1f + jitter) : 0.80f)));
        }
        frames[30] = ("flicker", At(30, 1.10f * 1.22f));
        frames[60] = ("dimmed", At(60, 1.10f * 1.22f, stars: 650));
        frames[100] = ("bank0", At(100, 1.10f * 2.2f));
        frames[101] = ("inside", At(101, 1.10f * 1.22f));
        frames[102] = ("bank1", At(102, 1.10f * 2.2f));
        var grades = Grades([.. frames]);
        bool Cloud(string name) => (LightGrading.Verdict(grades, grades[name]).Causes & GradeCause.Cloud) != 0;

        LightGrading.Verdict(grades, grades["flicker"]).LocalPatchiness.ShouldBeGreaterThan(LightGrading.LocalCloudFrom, "past the plain bar");
        var fv = LightGrading.Verdict(grades, grades["flicker"]);
        Cloud("flicker").ShouldBeFalse($"within its neighbours' jitter and no star lost: patch {fv.Patchiness} local {fv.LocalPatchiness} noise {fv.LocalNoise} stars {fv.StarRatio} run {fv.InCloudRun} sky {fv.NightSky}");
        Cloud("dimmed").ShouldBeTrue("the same patchiness with a third of its stars gone");
        Cloud("bank0").ShouldBeTrue();
        Cloud("inside").ShouldBeTrue("marginal, but between two confirmed cloud frames");
        LightGrading.InCloudRun(grades, grades["inside"]).ShouldBeTrue();
        LightGrading.InCloudRun(grades, grades["flicker"]).ShouldBeFalse();
    }

    /// <summary>
    /// The yardstick's cloud test reads both of Verdict's terms: a top-scoring frame that is cloud by its sky LEVEL alone (the
    /// 2026-07-06 LMC case, a flat patchiness) is not the yardstick, and a rising target's early frame, patchy against the night's
    /// clearest but not against its neighbours (the local floor), stays eligible. Every frame Verdict calls cloud is left out.
    /// </summary>
    [Fact]
    public void TheYardstickLeavesOutEveryFrameVerdictCallsCloudAndNoOther()
    {
        var night = new DateOnly(2026, 7, 6);
        var start = new DateTimeOffset(2026, 7, 6, 10, 0, 0, TimeSpan.Zero);
        var clear = Enumerable.Range(0, 12).Select(i => ($"c{i}", Run("LMC", 1000f + i, 1f, night, start.AddMinutes(i)) with { Score = 8f })).ToArray();
        var bright = Run("LMC", 2500f, 1f, night, start.AddMinutes(13)) with { Score = 10f };
        var sky = Grades([.. clear, ("bright", bright)]);
        (LightGrading.Verdict(sky, bright).Causes & GradeCause.Cloud).ShouldNotBe(GradeCause.None, "cloud by the sky term alone");
        LightGrading.BestScore(sky, bright.Key).ShouldBe(8f);

        // A rising target: patchiness falling 1.73 -> 1.0 over three hours, as on 2026-09-10; its first frame scores best.
        static float Climb(int i) => i < 90 ? 1.73f - 0.44f * i / 90f : i < 180 ? 1.29f - 0.29f * (i - 90) / 90f : 1f;
        var rising = Enumerable.Range(0, 300)
            .Select(i => ($"f{i}", Patchy(i == 0 ? 12f : 10f, Climb(i)) with { Taken = start.AddMinutes(i) })).ToArray();
        var grades = Grades(rising);
        LightGrading.Verdict(grades, rising[0].Item2).Patchiness.ShouldBeGreaterThan(LightGrading.CloudFrom, "patchy against the clearest");
        LightGrading.BestScore(grades, rising[0].Item2.Key).ShouldBe(12f, "not cloud against its neighbours, so it stays the yardstick");

        foreach (var table in new[] { sky, grades })
        {
            var best = LightGrading.BestScore(table, table.Values.First().Key);
            var clearMax = table.Values.Where(g => (LightGrading.Verdict(table, g).Causes & GradeCause.Cloud) == 0).Max(g => g.Score);
            best.ShouldBe(clearMax, "the yardstick is exactly the best frame Verdict calls clear");
        }
    }

    /// <summary>The Best the Grade line names is the frame the share is of: the best CLEAR frame, not a cloud frame scoring higher.</summary>
    [Fact]
    public async Task TheBestNamedIsTheYardstickNotACloudFrameScoringHigher()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var state = new ViewerState();
        var key = LightGradeKey.Of(document.UnstretchedImage);
        var folder = Path.GetDirectoryName(Path.GetFullPath(document.FilePath))!;
        state.RecordFrameGrade(document.FilePath, new LightGrade(key, new FrameMetrics(2.5f, 2.8f, 0.12f, 800), 4f, Patchiness: 1f));
        for (var i = 0; i < 10; i++)
        {
            state.RecordFrameGrade(Path.Combine(folder, i == 0 ? "clear-best.fits" : $"clear{i}.fits"),
                new LightGrade(key, new FrameMetrics(2f, 2.2f, 0.1f, 1000), i == 0 ? 8f : 6f, Patchiness: 1f + i * 0.01f));
        }
        state.RecordFrameGrade(Path.Combine(folder, "BAD_cloud.fits"), new LightGrade(key, new FrameMetrics(1.8f, 2f, 0.1f, 1000), 10f, Patchiness: 3f));

        var readout = InfoPanelData.GetGradeReadout(state, document) ?? throw new InvalidOperationException("a file has a grade section");
        readout.Relative.ShouldBe(0.5f, "a share of the best clear frame, 8");
        readout.BestName.ShouldBe("clear-best.fits");
    }

    [Fact]
    public async Task AGradedFramesStarsAreSaidUnderGradeAndNotAgainInTheStatusLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await ViewerInfoPanelCollapseTests.NewColourDocumentAsync(ct);
        var state = new ViewerState();
        var findings = new FrameFindings(null, new StarList(
        [
            new ImagedStar(HFD: 2.5f, StarFWHM: 2.8f, SNR: 100f, Flux: 1000f, XCentroid: 8f, YCentroid: 8f, Ellipticity: 0.1f),
        ]), 2.5f, 2.8f);

        // Before a grade, the status line is the only place the stars are said.
        InfoPanelData.StarsStatus(document, state, findings).ShouldBe("Stars: 1  HFD: 2.50  FWHM: 2.80");

        // A single frame's grade, "the best of 1", says them under Grade, FWHM included, and the status line no longer does.
        state.RecordFrameGrade(document.FilePath,
            new LightGrade(LightGradeKey.Of(document.UnstretchedImage), new FrameMetrics(2.5f, 2.8f, 0.1f, 1), 4f));
        InfoPanelData.StarsStatus(document, state, findings).ShouldBeNull();
        var readout = InfoPanelData.GetGradeReadout(state, document) ?? throw new InvalidOperationException("a file has a grade section");
        readout.Lines[1].ShouldBe("Scored stars: 1, HFD 2.50, FWHM 2.80, ecc 0.10");
    }

    /// <summary>
    /// A graded row is tinted by its grade, an ungraded one keeps the list's ground, and the selected row keeps the
    /// selection colour, which says which frame is on show.
    /// </summary>
    [Fact]
    public void TheFileListTintsAGradedRowByItsGrade()
    {
        using var renderer = new RgbaImageRenderer(900, 700);
        var viewer = new ListViewer(renderer);
        viewer.UploadChannelTexture(ReadOnlySpan<float>.Empty, 0, 400, 300);
        var folder = Path.Combine(Path.GetTempPath(), "graded-list");
        var state = new ViewerState
        {
            ShowFileList = true,
            ShowInfoPanel = false,
            ShowHistogram = false,
            StretchMode = StretchMode.None,
            CurrentFolder = folder,
            ImageFileNames = ["a.fits", "b.fits", "c.fits", "d.fits"],
            SelectedFileIndex = 0,
        };
        state.RecordFrameGrade(Path.Combine(folder, "a.fits"), Graded(Filter.Luminance, 10f));
        state.RecordFrameGrade(Path.Combine(folder, "b.fits"), Graded(Filter.Luminance, 10f));
        state.RecordFrameGrade(Path.Combine(folder, "c.fits"), Graded(Filter.Luminance, 3f));

        viewer.Render(null, state);

        var palette = ViewerTheme.Palette;
        RowColour(viewer, renderer, 0).ShouldBe(palette.Selection);
        RowColour(viewer, renderer, 1).ShouldBe(LightGradeColours.RowTint(palette, 1f));
        RowColour(viewer, renderer, 2).ShouldBe(LightGradeColours.RowTint(palette, 0.3f));
        RowColour(viewer, renderer, 3).ShouldNotBe(LightGradeColours.RowTint(palette, 1f), "an ungraded row is not tinted");
    }

    // A row's highlight colour, read just inside its left edge where the name is not drawn.
    private static RGBAColor32 RowColour(ListViewer viewer, RgbaImageRenderer renderer, int row)
    {
        var region = viewer.GetRegisteredRegions().First(r => r.Result is HitResult.ListItemHit { Index: var i } && i == row);
        var x = (int)(region.X + 3);
        var y = (int)(region.Y + region.Height / 2);
        var offset = ((y * renderer.Surface.Width) + x) * 4;
        var p = renderer.Surface.Pixels;
        return new RGBAColor32(p[offset], p[offset + 1], p[offset + 2], p[offset + 3]);
    }

    private sealed class ListViewer : ImageRendererBase<RgbaImage>
    {
        public ListViewer(RgbaImageRenderer renderer) : base(renderer)
        {
            Width = renderer.Width;
            Height = renderer.Height;
            DpiScale = 1f;
            FontPath = FontResolver.ResolveSystemFont();
        }

        protected override void RenderImageQuad(IPreviewSource? source, ViewerState state,
            in DisplayRendition rendition, WCS? wcs,
            float left, float top, float right, float bottom, uint projW, uint projH,
            RenditionSlot slot, bool sampleBeforeChannels) { }

        protected override void RenderHistogramQuad(StretchUniforms stretch, HistogramDisplay histogram,
            ViewerState state, float left, float top, float right, float bottom, uint projW, uint projH) { }

        protected override void DrawEllipseOverlay(float cx, float cy, float semiMajor, float semiMinor,
            float rotationRad, RGBAColor32 color, float thickness) { }

        protected override void DrawCrossOverlay(float cx, float cy, float armLength, RGBAColor32 color) { }

        protected override void DrawLineOverlay(float x0, float y0, float x1, float y1,
            RGBAColor32 color, float thickness) { }

        protected override void OnResize(uint width, uint height) { }

        public override void UploadImageTexture(ReadOnlySpan<float> data, int channel,
            int width, int height) { }

        public override void UploadHistogramData(IPreviewSource source) { }

        protected override HistogramDisplay? GetHistogramDisplay() => null;
    }
}
