using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// With a REMOTE rig on screen, the GUI's actions that drive THIS computer's rig refuse, as the session, flat and
/// polar starts already did (<c>EnsureLocalContext</c>), until P6 routes each to its context's node (P0b item 9 of
/// docs/plans/hardware-in-the-server.md, #752). Each drove the local rig from a remote view: planetary Start (the
/// remote mode pill offers Planetary), the planetary nudges, Goto from an object panel, and Solve and Sync, whose
/// reticle is the Active mount's.
/// <para>
/// Each handler either starts its local work at once or hands it to the tracker, so "the tracker was handed
/// nothing" is what shows the local rig was left alone, deterministically and without waiting on a slew.
/// </para>
/// </summary>
public class GuiContextGatingTests(ITestOutputHelper output)
{
    private const string Refusal = "runs on this computer";

    [Fact(Timeout = 30_000)]
    public async Task APlanetaryStartWithARemoteRigOnScreenLeavesTheLocalCameraAlone()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new StartVideoCaptureSignal(OtaIndex: 0));

        h.PlanetaryCapture.IsCapturing.ShouldBeFalse("the local camera started streaming");
        h.Contexts.Local.LiveSession.Mode.ShouldNotBe(LiveSessionMode.Planetary);
        h.ShouldHaveRefused(Refusal);
    }

    /// <summary>
    /// A rig's view shows ABORT once its node reports its run (P5b part 4): the abort goes to THAT rig's node (P5b part
    /// 5), and never cancels this computer's session, which it used to.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAbortOnARemoteRigsViewGoesToThatRigsNodeAndNeverTheLocalSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct, remoteOnScreen: true);
        var rig = h.ConnectRemoteRig();

        h.Post(new ConfirmAbortSessionSignal());

        await h.UntilAsync(() => rig.Contains("POST /api/v1/session/abort"), ct);
    }

    /// <summary>A rig's flat run puts its view in the Flats mode (P5b part 4); its Cancel goes to that rig's node.</summary>
    [Fact(Timeout = 30_000)]
    public async Task ACancelFlatsOnARemoteRigsViewGoesToThatRigsNodeAndNeverTheLocalFlatRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await GuiSignalHarness.StartAsync(output, ct, remoteOnScreen: true);
        var rig = h.ConnectRemoteRig();

        h.Post(new CancelFlatsSignal());

        await h.UntilAsync(() => rig.Contains("POST /api/v1/session/abort"), ct);
    }

    /// <summary>A rig not connected has no node to send the abort to: the view says so, and the local run is left alone.</summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAbortOnARigThatIsNotConnectedSaysSo()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new ConfirmAbortSessionSignal());

        h.ShouldHaveRefused("is not connected");
    }

    /// <summary>
    /// The answer goes to the prompt on screen: a rig's, when its view is (P5b part 5). It used to answer this computer's
    /// prompt whatever view it was given on.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAnswerOnARemoteRigsViewAnswersThatRigsPromptNotTheLocalOne()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);
        var (local, localAnswer) = Prompt("Local panel");
        var (remote, remoteAnswer) = Prompt("Rig panel");
        h.Contexts.Local.LiveSession.PendingPrompt = local;
        h.Contexts.Active.LiveSession.PendingPrompt = remote;

        h.Post(new RespondSessionPromptSignal(Proceed: true));

        remoteAnswer.Task.IsCompletedSuccessfully.ShouldBeTrue("the rig's prompt was answered");
        (await remoteAnswer.Task).ShouldBeTrue();
        localAnswer.Task.IsCompleted.ShouldBeFalse("the local prompt was answered from a remote rig's view");
        h.Contexts.Local.LiveSession.PendingPrompt.ShouldBeSameAs(local);
    }

    private static (SessionPromptEventArgs Prompt, TaskCompletionSource<bool> Answer) Prompt(string title)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (new SessionPromptEventArgs(title, "Switch it on, then Continue.", "Continue", "Cancel", answer), answer);
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountNudgeWithARemoteRigOnScreenDoesNotPulseTheLocalMount()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new JogMountSignal(GuideDirection.North, Arcsec: 10));

        h.ShouldHaveStartedNothing("a pulse on the local mount");
        h.ShouldHaveRefused(Refusal);
    }

    [Fact(Timeout = 30_000)]
    public async Task AGotoWithARemoteRigOnScreenDoesNotSlewTheLocalMount()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new SkyMapSlewToObjectSignal("M 42", 5.588, -5.39, Index: null, ObjectType.Unknown));

        h.ShouldHaveStartedNothing("a slew of the local mount");
        h.ShouldHaveRefused(Refusal);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASolveAndSyncWithARemoteRigOnScreenDoesNotTouchTheLocalRig()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new SkyMapSolveSyncSignal());

        h.SkyMap.SolveSyncInProgress.ShouldBeFalse("a solve on the local camera, to sync the local mount");
        h.ShouldHaveStartedNothing("a solve on the local camera, to sync the local mount");
        h.ShouldHaveRefused(Refusal);
    }

    /// <summary>
    /// The planetary panel's focuser jog, beside its nudges: not in the review's list, and reachable the same way.
    /// Its handler resolves the focuser as the preview panel's jog and goto do, so all three refuse together.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AFocuserJogWithARemoteRigOnScreenDoesNotMoveTheLocalFocuser()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken, remoteOnScreen: true);

        h.Post(new JogFocuserSignal(OtaIndex: 0, Steps: 10));

        h.ShouldHaveStartedNothing("a move of the local focuser");
        h.ShouldHaveRefused(Refusal);
    }
}
