using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The rigs' clear nights are kept across folders (#1976): a clear night is recorded to AppData's <c>Viewer/clear-sky.json</c>, a later
/// folder that is cloud from first frame to last is then judged against it, and a cloudy run is never recorded.
/// </summary>
[Collection("UI")]
public class LightGradeReferenceStoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private FakeExternal MakeExternal() => new(output, _folders.Create("tianwen-clear-sky-"));

    private static LightGrade Frame(string target, float sky, float patchiness, DateOnly night, int minute)
        => new(new LightGradeKey(4, 4, SensorType.Monochrome, "", target, 30_000, 1), new FrameMetrics(2f, 2.2f, 0.1f, 1000), 10f,
            Patchiness: patchiness, SkyLevel: sky, Night: night, Taken: new DateTimeOffset(night.Year, night.Month, night.Day, 12, 0, 0, TimeSpan.Zero).AddMinutes(minute));

    private static ImmutableDictionary<string, LightGrade> Table(string folder, params LightGrade[] frames)
        => ImmutableDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
            frames.Select((f, i) => new System.Collections.Generic.KeyValuePair<string, LightGrade>($"{folder}/f{i}.fits", f)));

    private static LightGrade[] ClearNight(DateOnly night) => [.. Enumerable.Range(0, 12).Select(i => Frame("LMC", 1000f + i * 10, 1f + i * 0.01f, night, i))];

    // Cloud from first frame to last, but not still: the sky wanders from 2x to 5x the clear night's.
    private static LightGrade[] CloudyNight(DateOnly night) => [.. Enumerable.Range(0, 12).Select(i => Frame("LMC", 2000f + i * 300, 3f + i * 0.4f, night, i))];

    [Fact(Timeout = 60_000)]
    public async Task TheStoreRoundTripsAndAReloadSeesWhatWasWritten()
    {
        var external = MakeExternal();
        var ct = TestContext.Current.CancellationToken;
        (await LightGradeReferenceStore.LoadAsync(external, ct)).ShouldBeEmpty();
        (await LightGradeReferenceStore.LoadAsync(external, ct)).IsDefault.ShouldBeFalse("none saved is empty, not not-asked");

        var night = new DateOnly(2026, 6, 1);
        var written = await LightGradeReferenceStore.RecordAsync(external, Table("a", ClearNight(night)), [], ct);
        written.Length.ShouldBe(1);

        var read = await LightGradeReferenceStore.LoadAsync(external, ct);
        read.Length.ShouldBe(1);
        read[0].Night.ShouldBe(night);
        read[0].Key.ObjectKey.ShouldBeEmpty("the sky level is the rig's, not the target's");
        read[0].NightDarkestSky.ShouldBeInRange(1000f, 1020f);
        read[0].Objects.Select(o => o.ObjectKey).ShouldBe(["LMC"]);
        double.IsNaN(read[0].SiteLatitude).ShouldBeTrue("an unknown site survives JSON as NaN");
    }

    [Fact(Timeout = 60_000)]
    public async Task AClearNightIsRecordedAndACloudyOnlyFolderIsThenGradedCloudNamingItsDate()
    {
        var external = MakeExternal();
        var ct = TestContext.Current.CancellationToken;
        var clearNight = new DateOnly(2026, 6, 1);
        await LightGradeReferenceStore.RecordAsync(external, Table("clear", ClearNight(clearNight)), [], ct);

        var references = await LightGradeReferenceStore.LoadAsync(external, ct);
        var cloudy = CloudyNight(new DateOnly(2026, 7, 6));
        var table = Table("cloudy", cloudy);
        foreach (var frame in cloudy)
        {
            var verdict = LightGrading.Verdict(table, frame, references);
            verdict.Causes.ShouldBe(GradeCause.Cloud);
            verdict.ReferenceNight.ShouldBe(clearNight);
            LightGrading.CloudInWords(verdict).ShouldContain("2026-06-01");
        }

        // And the cloudy run is not recorded: not read through the reference, and not on its own terms.
        var after = await LightGradeReferenceStore.RecordAsync(external, table, references, ct);
        after.Length.ShouldBe(1);
        (await LightGradeReferenceStore.LoadAsync(external, ct)).Select(r => r.Night).ShouldBe([clearNight]);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACloudyOnlyFolderWithNoStoreSaysCloudCannotBeJudgedAndRecordsNothing()
    {
        var external = MakeExternal();
        var ct = TestContext.Current.CancellationToken;
        var references = await LightGradeReferenceStore.LoadAsync(external, ct);
        var cloudy = CloudyNight(new DateOnly(2026, 7, 6));
        var table = Table("cloudy", cloudy);

        LightGrading.Verdict(table, cloudy[0], references).CloudUnjudged.ShouldBeTrue();
        (await LightGradeReferenceStore.RecordAsync(external, table, references, ct)).ShouldBeEmpty();
        System.IO.File.Exists(LightGradeReferenceStore.PathFor(external)).ShouldBeFalse("nothing qualified, so nothing was written");
    }

    /// <summary>
    /// A rig's first reference comes from a night that is mostly clear and clouds over late (2026-09-10: two clear hours, cloud
    /// after midnight), which the rule that demanded no cloud at all never recorded; a night mostly in cloud still makes none.
    /// </summary>
    [Fact]
    public void AMostlyClearNightThatCloudsOverLateIsARigsFirstReference()
    {
        var night = new DateOnly(2026, 9, 10);
        var clear = ClearNight(night);
        var late = Enumerable.Range(12, 4).Select(i => Frame("LMC", 1100f + i * 10, 4f, night, i)).ToArray();
        var table = Table("mixed", [.. clear, .. late]);
        late.ShouldAllBe(f => (LightGrading.Verdict(table, f, default).Causes & GradeCause.Cloud) != 0, "the late frames must read as cloud");

        var made = LightGrading.ClearNights(table);
        made.Length.ShouldBe(1);
        made[0].Night.ShouldBe(night);

        var mostlyCloud = Table("cloudy", [.. clear.Take(LightGrading.MinClearFramesPerReference),
            .. Enumerable.Range(20, 12).Select(i => Frame("LMC", 1100f + i * 10, 4f, night, i))]);
        LightGrading.ClearNights(mostlyCloud).ShouldBeEmpty("most of the night in cloud is no night to judge a rig by");
    }

    [Fact]
    public void TooFewClearFramesAndTwilightFramesDoNotMakeAReference()
    {
        var night = new DateOnly(2026, 6, 1);
        LightGrading.ClearNights(Table("few", ClearNight(night).Take(LightGrading.MinClearFramesPerReference - 1).ToArray())).ShouldBeEmpty();
        var twilight = ClearNight(night).Select(g => g with { Twilight = TianWen.Lib.Sequencing.TwilightPeriod.Dawn }).ToArray();
        LightGrading.ClearNights(Table("dawn", twilight)).ShouldBeEmpty();
    }

    [Fact]
    public void ANightIsReplacedInPlaceAndEachRigKeepsOnlyItsNewestNights()
    {
        ImmutableArray<LightGradeReference> kept = [];
        for (var day = 1; day <= LightGrading.MaxReferenceNightsPerRig + 2; day++)
        {
            kept = LightGrading.MergeReferences(kept, LightGrading.ClearNights(Table("n", ClearNight(new DateOnly(2026, 6, day)))));
        }
        kept.Select(r => r.Night.Day).ShouldBe(Enumerable.Range(3, LightGrading.MaxReferenceNightsPerRig).Reverse());

        var again = LightGrading.MergeReferences(kept, LightGrading.ClearNights(Table("n", ClearNight(new DateOnly(2026, 6, 7)))));
        again.Length.ShouldBe(kept.Length);
    }
}
