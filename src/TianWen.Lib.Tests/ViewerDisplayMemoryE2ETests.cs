using System;
using System.IO;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
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
