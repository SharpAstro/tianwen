using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A file's last display comes back when it is opened again (#1814), through the host <c>tianwen-fits</c> runs (<see cref="ViewerE2E"/>
/// over a data root of its own): tune a file, open another, reopen the first. A rewritten file opens with the defaults, a held run's
/// followers keep the anchor's display, and Reset display puts the defaults back and forgets the record.
/// </summary>
[Collection("Viewer")]
public class ViewerDisplayMemoryE2ETests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFolders _folders = new TempFolders();

    public void Dispose() => _folders.Dispose();

    private FakeExternal MakeExternal() => new(output, _folders.Create("tianwen-viewer-memory-e2e-"));

    private static StretchMode NotTheDefaultMode
        => ViewerActions.DefaultStretchMode is StretchMode.Unlinked ? StretchMode.Luma : StretchMode.Unlinked;

    /// <summary>One tuning, every setting off its default; <paramref name="k"/> makes two tunings differ.</summary>
    private static void Tune(ViewerState state, float k)
    {
        var defaults = new ViewerState();
        state.StretchMode = NotTheDefaultMode;
        state.StretchParameters = new StretchParameters(0.1 + k, -1.5 - k);
        state.ChannelView = ChannelView.Green;
        state.BackgroundNeutralizationEnabled = !defaults.BackgroundNeutralizationEnabled;
        state.BackgroundSkyLevel = 0.05f + (k / 100f);
        state.ManualWhiteBalance = (1.2f + k, 1f, 0.8f);
        state.DisplayCrop = new PixelRect(4, 4, 32, 24);
        state.CurvesBoost = 0.3f;
        state.CurvesMode = defaults.CurvesMode + 1;
        state.HdrAmount = 0.4f + (k / 100f);
        state.HdrKnee = 0.6f;
        state.FinishContrast = 0.25f;
        state.FinishSaturation = 1.4f;
        state.StarMethod = StarStretchMethod.StarStretch;
        state.StarAmount = defaults.StarAmount + 0.5f;
    }

    private static void ShouldBeTuned(ViewerState state, float k)
    {
        var defaults = new ViewerState();
        state.StretchMode.ShouldBe(NotTheDefaultMode);
        state.StretchParameters.ShouldBe(new StretchParameters(0.1 + k, -1.5 - k));
        state.ChannelView.ShouldBe(ChannelView.Green);
        state.BackgroundNeutralizationEnabled.ShouldBe(!defaults.BackgroundNeutralizationEnabled);
        state.BackgroundSkyLevel.ShouldBe(0.05f + (k / 100f));
        state.ManualWhiteBalance.ShouldBe((1.2f + k, 1f, 0.8f));
        state.DisplayCrop.ShouldBe(new PixelRect(4, 4, 32, 24));
        state.CurvesBoost.ShouldBe(0.3f);
        state.CurvesMode.ShouldBe(defaults.CurvesMode + 1);
        state.HdrAmount.ShouldBe(0.4f + (k / 100f));
        state.HdrKnee.ShouldBe(0.6f);
        state.FinishContrast.ShouldBe(0.25f);
        state.FinishSaturation.ShouldBe(1.4f);
        state.StarMethod.ShouldBe(StarStretchMethod.StarStretch);
        state.StarAmount.ShouldBe(defaults.StarAmount + 0.5f);
    }

    private static void ShouldBeDefault(ViewerState state)
    {
        var defaults = new ViewerState();
        state.StretchMode.ShouldBe(ViewerActions.DefaultStretchMode);
        state.StretchParameters.ShouldBe(defaults.StretchParameters);
        state.ChannelView.ShouldBe(defaults.ChannelView);
        state.BackgroundNeutralizationEnabled.ShouldBe(defaults.BackgroundNeutralizationEnabled);
        state.BackgroundSkyLevel.ShouldBe(defaults.BackgroundSkyLevel);
        state.ManualWhiteBalance.ShouldBe(defaults.ManualWhiteBalance);
        state.DisplayCrop.ShouldBeNull();
        state.CurvesBoost.ShouldBe(defaults.CurvesBoost);
        state.CurvesMode.ShouldBe(defaults.CurvesMode);
        state.HdrAmount.ShouldBe(defaults.HdrAmount);
        state.HdrKnee.ShouldBe(defaults.HdrKnee);
        state.FinishContrast.ShouldBe(defaults.FinishContrast);
        state.FinishSaturation.ShouldBe(defaults.FinishSaturation);
        state.StarMethod.ShouldBe(defaults.StarMethod);
        state.StarAmount.ShouldBe(defaults.StarAmount);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ATunedFileComesBackWhenItIsOpenedAgain(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi, external: MakeExternal());
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        ShouldBeDefault(e2e.State);
        Tune(e2e.State, 0f);
        e2e.Frame();

        // A file with no record keeps what is on show; b is tuned differently, so a's return can only come from its record.
        await e2e.OpenAsync(b, ct);
        Tune(e2e.State, 3f);

        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARewrittenFileOpensWithTheDefaults(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var e2e = ViewerE2E.Start(dpi, external: MakeExternal());
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        Tune(e2e.State, 3f);

        e2e.WriteColourFits("a.fits", level: 20f);
        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddMinutes(3));

        // The stale record is not applied: what was on show (b's) carries over instead (the crop is the document's own).
        await e2e.OpenAsync(a, ct);
        e2e.State.StretchParameters.ShouldBe(new StretchParameters(0.1 + 3f, -1.5 - 3f));
        e2e.State.ManualWhiteBalance.ShouldBe((1.2f + 3f, 1f, 0.8f));
        e2e.State.ChannelView.ShouldBe(ChannelView.Green);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task AHeldRunsFollowersKeepTheAnchorsDisplayAndLeaveNoRecord(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        // a anchors the run; b, a follower of it, remembers a tuning of its own (B), made unheld.
        await e2e.OpenAsync(a, ct);
        e2e.Frame();
        await e2e.OpenAsync(b, ct);
        Tune(e2e.State, 7f);
        await e2e.OpenAsync(a, ct);
        e2e.Frame();

        // Hold the display on a, tuned as A: a is the run's anchor, b a follower.
        e2e.Key(InputKey.H, InputModifier.Ctrl);
        e2e.State.CarryDisplayAcrossFrames.ShouldBeTrue();
        Tune(e2e.State, 0f);
        e2e.Frame();

        await e2e.OpenAsync(b, ct);
        e2e.Frame();
        e2e.Controller.Document.ShouldNotBeNull().DisplayAnchor.ShouldNotBeNull("b follows a's run");
        e2e.State.StretchParameters.ShouldBe(new StretchParameters(0.1 + 0f, -1.5 - 0f));
        e2e.State.ManualWhiteBalance.ShouldBe((1.2f, 1f, 0.8f));

        // Leaving the follower saves nothing over what b remembered.
        await e2e.OpenAsync(a, ct);
        e2e.State.StretchParameters.ShouldBe(new StretchParameters(0.1 + 0f, -1.5 - 0f));
        var record = await ViewerDisplayMemory.LoadAsync(external, b, ct);
        record.ShouldNotBeNull();
        record.StretchParameters.ShouldBe(new StretchParameters(0.1 + 7f, -1.5 - 7f));
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task LeavingAnEnhanceResultLeavesItsSourcesRecordAlone(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);

        // An enhance result carries a's path but not a's display: its crop is dropped and its tuning is its own.
        e2e.Controller.Document.ShouldNotBeNull().MarkAsEnhanceResult();
        Tune(e2e.State, 5f);
        e2e.State.DisplayCrop = null;
        await e2e.OpenAsync(b, ct);

        var record = await ViewerDisplayMemory.LoadAsync(external, a, ct);
        record.ShouldNotBeNull();
        record.DisplayCrop.ShouldBe(new PixelRect(4, 4, 32, 24));
        record.StretchParameters.ShouldBe(new StretchParameters(0.1, -1.5));
    }

    /// <summary>
    /// A thumbs mark renames the file (#1951), which forgets the record kept under the old name. A display restored on open and
    /// never touched since is written again under the new name on leaving, or the rejected frame reopens at its defaults.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARejectedFrameKeepsItsRememberedDisplayUnderItsNewName(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);

        // Restored and left as it was, then rejected: the file is renamed and the old name's record forgotten.
        var document = e2e.Controller.Document.ShouldNotBeNull();
        ViewerActions.MarkFrame(e2e.State, document, CaptureRejection.Cause.Cloud).ShouldStartWith("Rejected as cloud");
        e2e.Frame();
        var rejected = Path.Combine(Path.GetDirectoryName(a).ShouldNotBeNull(), "BAD_CLOUD_a.fits");
        File.Exists(rejected).ShouldBeTrue();

        await e2e.OpenAsync(b, ct);
        Tune(e2e.State, 3f);
        await e2e.OpenAsync(rejected, ct);
        ShouldBeTuned(e2e.State, 0f);
    }

    /// <summary>
    /// The same with the rename still running when the owner leaves (a slow folder): the leave waits for it, so the record lands
    /// under the name the file ends with, whether the rename finished before the leave or not.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task LeavingWhileTheRenameRunsKeepsTheDisplayUnderTheNewName(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);

        var document = e2e.Controller.Document.ShouldNotBeNull();
        ViewerActions.StartMarkFrame(e2e.State, document, CaptureRejection.Cause.Cloud);
        await e2e.OpenAsync(b, ct);
        var rejected = Path.Combine(Path.GetDirectoryName(a).ShouldNotBeNull(), "BAD_CLOUD_a.fits");
        File.Exists(rejected).ShouldBeTrue();

        Tune(e2e.State, 3f);
        await e2e.OpenAsync(rejected, ct);
        ShouldBeTuned(e2e.State, 0f);
    }

    /// <summary>
    /// The gap between the leave's follow of the renames and its read of the pending mark (#2012): the rename is held behind a gate and
    /// finished inside that gap by the controller's test seam, so the follow never sees it and the leave must take it itself.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ARenameThatFinishesBetweenTheFollowAndTheLeaveStillMovesTheDisplayToTheNewName(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);

        using var gate = new System.Threading.ManualResetEventSlim();
        var document = e2e.Controller.Document.ShouldNotBeNull();
        ViewerActions.StartMarkFrame(e2e.State, document, CaptureRejection.Cause.Cloud, (path, cause) =>
        {
            gate.Wait(ct);
            return ViewerActions.RenameOnDisk(path, cause);
        });
        var state = e2e.State;
        e2e.Controller.AfterLeavingFollow = () =>
        {
            e2e.Controller.AfterLeavingFollow = null;
            var rename = state.PendingMark.ShouldNotBeNull().Rename;
            rename.IsCompleted.ShouldBeFalse();
            gate.Set();
            rename.Wait(ct);
        };

        await e2e.OpenAsync(b, ct);
        var rejected = Path.Combine(Path.GetDirectoryName(a).ShouldNotBeNull(), "BAD_CLOUD_a.fits");
        File.Exists(rejected).ShouldBeTrue();

        Tune(e2e.State, 3f);
        await e2e.OpenAsync(rejected, ct);
        ShouldBeTuned(e2e.State, 0f);
    }

    [Theory(Timeout = 120_000)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public async Task ResetDisplayPutsTheDefaultsBackAndForgetsTheRecord(float dpi)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = MakeExternal();
        await using var e2e = ViewerE2E.Start(dpi, external: external);
        var a = e2e.WriteColourFits("a.fits");
        var b = e2e.WriteColourFits("b.fits", level: 500f);

        await e2e.OpenAsync(a, ct);
        Tune(e2e.State, 0f);
        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeTuned(e2e.State, 0f);
        File.Exists(ViewerDisplayMemory.PathFor(external, a)).ShouldBeTrue();

        e2e.Key(InputKey.R, InputModifier.Ctrl | InputModifier.Shift);
        ShouldBeDefault(e2e.State);
        File.Exists(ViewerDisplayMemory.PathFor(external, a)).ShouldBeFalse();

        await e2e.OpenAsync(b, ct);
        await e2e.OpenAsync(a, ct);
        ShouldBeDefault(e2e.State);
    }
}
