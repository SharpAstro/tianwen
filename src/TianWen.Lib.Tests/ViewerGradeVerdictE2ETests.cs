using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
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
