using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Grading;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The Grade section's thumbs (#1951) through the viewer's own host, at DPI 1 and 1.5: drawn only once the frame is graded,
/// pressed where they are painted, the file renamed with the <c>BAD_</c> prefix and back, and the file list's row and the open
/// document following the name.
/// </summary>
[Collection("Viewer")]
public class ViewerGradeVerdictE2ETests
{
    public static TheoryData<float> Scales => new TheoryData<float> { 1f, 1.5f };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Scales))]
    public async Task TheThumbsAppearOnceGradedAndRejectAndKeepTheFrameWhereTheyAreDrawn(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpiScale);
        var path = e2e.WriteColourFits("light_0089.fits", level: 3f);
        await e2e.OpenAsync(path, ct);

        static bool IsThumb(HitResult hit, string action) => hit is HitResult.ButtonHit { Action: var a } && a == action;
        e2e.Viewer.PaintedRegions().Any(r => IsThumb(r.Result, "RejectCloud")).ShouldBeFalse("no verdict before a grade");

        // The synthetic frame has no stars to grade by, so the grade star detection would record is recorded here.
        var document = e2e.Controller.Document ?? throw new System.InvalidOperationException("the file is open");
        e2e.State.RecordFrameGrade(path, new LightGrade(LightGradeKey.Of(document.UnstretchedImage), new FrameMetrics(2f, 2.2f, 0.1f, 500), 5f));
        e2e.Frame();

        e2e.Click(e2e.Region(hit => IsThumb(hit, "RejectCloud"), "the Grade section's Cloud"));
        e2e.Frame();

        var rejected = Path.Combine(Path.GetDirectoryName(path)!, "BAD_CLOUD_light_0089.fits");
        await e2e.PumpUntilAsync(() => File.Exists(rejected) && e2e.IsShowing(rejected), "the rename to be applied", ct);
        File.Exists(rejected).ShouldBeTrue();
        File.Exists(path).ShouldBeFalse();
        e2e.IsShowing(rejected).ShouldBeTrue("the open document follows its file");
        e2e.State.ImageFileNames.ShouldContain("BAD_CLOUD_light_0089.fits");
        e2e.State.StatusMessage.ShouldNotBeNull().ShouldStartWith("Rejected as cloud");

        e2e.Click(e2e.Region(hit => IsThumb(hit, "KeepFrame"), "the Grade section's Good"));
        e2e.Frame();

        await e2e.PumpUntilAsync(() => File.Exists(path) && e2e.IsShowing(path), "the keep to be applied", ct);
        File.Exists(path).ShouldBeTrue();
        CaptureRejection.IsRejectedAtCapture(e2e.Controller.Document?.FilePath ?? "").ShouldBeFalse();
        e2e.State.ImageFileNames.ShouldContain("light_0089.fits");
    }

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Scales))]
    public async Task ASuspectedCloudFrameIsConfirmedByCloudAndClearedByGood(float dpiScale)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpiScale);
        // The folder holds one frame of each kind, so its count in the Grade section has all three to tell apart.
        e2e.WriteColourFits("BAD_STREAKY_light_0090.fits", level: 2f);
        e2e.WriteColourFits("light_0092.fits", level: 4f);
        var path = e2e.WriteColourFits("MAYBE_CLOUD_light_0091.fits", level: 3f);
        await e2e.OpenAsync(path, ct);
        var document = e2e.Controller.Document ?? throw new System.InvalidOperationException("the file is open");
        e2e.State.RecordFrameGrade(path, new LightGrade(LightGradeKey.Of(document.UnstretchedImage), new FrameMetrics(2f, 2.2f, 0.1f, 500), 5f));
        e2e.Frame();
        PaintedStripText(e2e).ShouldContain("Folder: 1 suspected cloud, 1 rejected, 1 unmarked");

        static bool IsThumb(HitResult hit, string action) => hit is HitResult.ButtonHit { Action: var a } && a == action;
        var dir = Path.GetDirectoryName(path)!;

        e2e.Click(e2e.Region(hit => IsThumb(hit, "RejectCloud"), "the Grade section's Cloud"));
        e2e.Frame();
        var confirmed = Path.Combine(dir, "BAD_CLOUD_light_0091.fits");
        await e2e.PumpUntilAsync(() => File.Exists(confirmed) && e2e.IsShowing(confirmed), "the confirm to be applied", ct);
        File.Exists(path).ShouldBeFalse();
        e2e.State.ImageFileNames.ShouldContain("BAD_CLOUD_light_0091.fits");
        e2e.State.GradeOf("BAD_CLOUD_light_0091.fits").ShouldNotBeNull("the grade follows the name");
        PaintedStripText(e2e).ShouldContain("Folder: 2 rejected, 1 unmarked");

        // Put the suspicion back as the session writes it (the file, the list's row, the document and its grade), then clear it with Good.
        File.Move(confirmed, path);
        e2e.State.RecordFrameRename(confirmed, path);
        e2e.Controller.Document?.FollowRename(path);
        e2e.State.RenameFrameGrade(confirmed, path);
        e2e.State.ImageFileNames[e2e.State.ImageFileNames.IndexOf("BAD_CLOUD_light_0091.fits")] = "MAYBE_CLOUD_light_0091.fits";
        e2e.Frame();
        PaintedStripText(e2e).ShouldContain("Folder: 1 suspected cloud, 1 rejected, 1 unmarked");

        e2e.Click(e2e.Region(hit => IsThumb(hit, "KeepFrame"), "the Grade section's Good"));
        e2e.Frame();
        var cleared = Path.Combine(dir, "light_0091.fits");
        await e2e.PumpUntilAsync(() => File.Exists(cleared) && e2e.IsShowing(cleared), "the clear to be applied", ct);
        File.Exists(path).ShouldBeFalse();
        CaptureRejection.IsSuspectedCloud(e2e.Controller.Document?.FilePath ?? "").ShouldBeFalse();
        e2e.State.ImageFileNames.ShouldContain("light_0091.fits");
        e2e.State.GradeOf("light_0091.fits").ShouldNotBeNull("the grade follows the name");
        PaintedStripText(e2e).ShouldContain("Folder: 1 rejected, 2 unmarked");
    }

    /// <summary>
    /// #1974: the open frame carries a thin veil against its folder's own night (the stored models none), and the Grade section says
    /// Cloud with the expected-sky words, naming where the clear sky came from.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AThinVeilIsSaidInTheGradeSectionAgainstTheFoldersOwnNight()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(1f);
        var path = e2e.WriteColourFits("light_0200.fits", level: 3f);
        await e2e.OpenAsync(path, ct);

        // The folder's night, 300 synthetic frames, the open one (285) in the middle of the veil.
        var night = LightGradingTests.SyntheticSkyNight(300, veilFrom: 266, objectName: "e2e-veil");
        for (var i = 0; i < 300; i++)
        {
            if (i == 285)
            {
                continue;
            }
            e2e.State.RecordFrameGrade(Path.Combine(Path.GetDirectoryName(path)!, $"light_{i:0000}.fits"), night[i]);
        }
        e2e.State.RecordFrameGrade(path, night[285]);
        e2e.State.ExpectedSky = [];
        // The night's own sky is fitted off the thread that draws (#2069): start it and let it land, as the viewer's poll repaints on.
        await LightGradingTests.AwaitSelfFitAsync(e2e.State.FrameGrades, "e2e-veil", ct);
        e2e.Frame();

        var verdict = LightGrading.Verdict(e2e.State.FrameGrades, night[285], e2e.State.GradeReferences, e2e.State.ExpectedSky);
        verdict.Causes.HasFlag(GradeCause.Cloud).ShouldBeTrue("the veiled last frame reads as cloud");
        PaintedStripText(e2e).ShouldContain("sigma brighter");
    }

    /// <summary>
    /// #1974 section 2, through the viewer's own path and not a hand-built grade: a real light with a header WCS is opened, the viewer's
    /// star detection grades it through <c>LightGrading.Grade</c> with the document's WCS and the catalogue (the frame adopted on [0, 1]
    /// and its zero point put back into counts by the document's full scale), and the verdict reads that zero point against a model the
    /// real fitter fitted from clear lights in FILE counts. The two sides measure the zero point in different spaces, so a slip in the
    /// scale (a sign, a missing term) is a transparency that is wrong by a fixed factor, which only a frame that went the whole way
    /// shows: a clear light reads about 1, the same field 40 % dimmer about 0.6, and is Cloud, said in the Grade section.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALightGradedThroughTheViewerReadsItsTransparencyAgainstTheFittedModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var field = PhotometricTransparencyTests.BuildField(54, seed: 2);
        using var folders = new TempFolders();
        var clearFolder = folders.Create("clear").FullName;
        var random = new Random(77);
        foreach (var day in new[] { 10, 11 })
        {
            for (var i = 0; i < 24; i++)
            {
                var when = new DateTimeOffset(2026, 6, day, 11, 0, 0, TimeSpan.Zero).AddMinutes(i * 9);
                PhotometricTransparencyTests.WriteStarLight(Path.Combine(clearFolder, $"Light_{day}_{i:000}.fits"),
                    PhotometricTransparencyTests.MetaFor(when, -5.0 + 10.0 * i / 23), field, 1.0, withWcs: true, random);
            }
        }
        var fitted = await ExpectedSkyFitter.FitAsync([clearFolder], logger: null, cancellationToken: ct, catalogue: field.Db);
        fitted.Entries.Length.ShouldBe(1);
        fitted.Entries[0].Model.HasZeroPoint.ShouldBeTrue("the fitter read a clear zero point from the lights' header WCS");

        await using var e2e = ViewerE2E.Start(1f);
        e2e.Controller.CelestialObjectDB = new DotNext.Threading.AsyncLazy<ICelestialObjectDB>(field.Db);
        e2e.State.ExpectedSky = fitted.Entries;
        var taken = new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero).AddMinutes(100);
        var clearPath = Path.Combine(e2e.Folder, "light_clear.fits");
        var dimPath = Path.Combine(e2e.Folder, "light_dim.fits");
        PhotometricTransparencyTests.WriteStarLight(clearPath, PhotometricTransparencyTests.MetaFor(taken, 1.0), field, 1.0, withWcs: true, random);
        PhotometricTransparencyTests.WriteStarLight(dimPath, PhotometricTransparencyTests.MetaFor(taken.AddMinutes(1), 1.0), field, 0.6, withWcs: true, random);

        async Task<LightGradeVerdict> VerdictOfAsync(string path)
        {
            await e2e.OpenAsync(path, ct);
            var full = Path.GetFullPath(path);
            await e2e.PumpUntilAsync(() => e2e.State.FrameGrades.ContainsKey(full), $"{Path.GetFileName(path)} to be graded", ct);
            var grade = e2e.State.FrameGrades[full];
            float.IsFinite(grade.ZeroPoint).ShouldBeTrue("the viewer's grade measured a zero point: the header WCS and the catalogue reached LightGrading.Grade");
            return LightGrading.Verdict(e2e.State.FrameGrades, grade, e2e.State.GradeReferences, e2e.State.ExpectedSky);
        }

        var clear = await VerdictOfAsync(clearPath);
        var dim = await VerdictOfAsync(dimPath);

        clear.Transparency.ShouldBeInRange(0.85f, 1.15f, "a clear light reads the rig's own clear light");
        (clear.Transparency < LightGrading.TransparencyCloudBelow).ShouldBeFalse();
        dim.Transparency.ShouldBeInRange(0.5f, 0.7f, "the same field 40 % dimmer");
        (dim.Causes & GradeCause.Cloud).ShouldNotBe(GradeCause.None, "a dim light is cloud against the rig's clear light");
        LightGrading.CloudInWords(dim).ShouldContain("transmits");

        e2e.Frame();
        PaintedStripText(e2e).ShouldContain("transmits");
    }

    /// <summary>The info strip's painted words in reading order, joined by spaces: what the strip says, read off the captured layout.</summary>
    private static string PaintedStripText(ViewerE2E e2e)
    {
        var panel = e2e.Viewer.CurrentInfoPanelRect;
        panel.Width.ShouldBeGreaterThan(0f, "the strip has to be painted for this to observe anything");
        return string.Join(' ', e2e.Viewer.GetCapturedLayout()
            .Where(a => a.Bounds.X >= panel.X && a.Bounds.X < panel.X + panel.Width && a.Bounds.Y >= panel.Y && a.Bounds.Y < panel.Y + panel.Height)
            .Select(InfoPanelStripWidthTests.Text)
            .Where(static t => t.Length > 0));
    }

    [Fact(Timeout = 120_000)]
    public async Task AFinishedRenameAsksTheLoopForAFrameWithNoInput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(1f);
        var path = e2e.WriteColourFits("light_0090.fits", level: 3f);
        await e2e.OpenAsync(path, ct);
        var document = e2e.Controller.Document ?? throw new System.InvalidOperationException("the file is open");

        ViewerActions.StartMarkFrame(e2e.State, document, CaptureRejection.Cause.Cloud);
        e2e.Frame();
        var pending = e2e.State.PendingMark ?? throw new System.InvalidOperationException("the rename is pending");
        await pending.Rename.WaitAsync(ct);

        e2e.Host.WantsFrame().ShouldBeTrue("a finished rename is applied by a frame, which must be asked for");
        await e2e.PumpUntilAsync(() => e2e.State.PendingMark is null, "the rename to be applied", ct);
    }
}
