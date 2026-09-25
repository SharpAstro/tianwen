using System;
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

    /// <summary>
    /// The control: the same Goto with this computer's rig on screen does drive it, so a refusal above is the
    /// context's doing and not a rig the harness failed to wire.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AGotoWithTheLocalRigOnScreenSlewsIt()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);

        h.Post(new SkyMapSlewToObjectSignal("M 42", 5.588, -5.39, Index: null, ObjectType.Unknown));

        h.Tracker.PendingCount.ShouldBeGreaterThan(h.PendingBefore, "the local mount's slew was handed to the tracker");
        h.AppState.Notifications.ShouldNotContain(n => n.Message.Contains(Refusal));
    }
}
