using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="ViewContexts"/> -- the local node's context plus any observed rigs, and which one
/// the tabs render. The invariant it protects (docs/plans/remote-profile.md, "View context is an
/// overlay, not a rebind"): selecting a rig changes what you LOOK at and nothing else, so the local
/// session keeps running underneath with its state intact and its redraws still scheduled.
/// </summary>
public class ViewContextsTests
{
    private static ISessionTelemetry SessionAt(SessionPhase phase, int framesWritten = 0)
    {
        var session = Substitute.For<ISessionTelemetry>();
        session.Phase.Returns(phase);
        session.TotalFramesWritten.Returns(framesWritten);
        session.MountDisplayName.Returns("Mount");
        return session;
    }

    [Fact]
    public void StartsWithExactlyOneLocalContext()
    {
        var contexts = new ViewContexts();

        contexts.All.Length.ShouldBe(1);
        contexts.All[0].ShouldBeSameAs(contexts.Local);
        contexts.Active.ShouldBeSameAs(contexts.Local);
        contexts.Local.IsLocal.ShouldBeTrue();
        contexts.Local.NodeId.ShouldBeNull();
        contexts.IsRemoteActive.ShouldBeFalse();
    }

    [Fact]
    public void GetOrAddRemote_AddsOneContextPerNodeAndIsIdempotent()
    {
        var contexts = new ViewContexts();

        var first = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        var again = contexts.GetOrAddRemote("node-a", "Observatory Pi (renamed)");
        var other = contexts.GetOrAddRemote("node-b", "Roof Rig");

        again.ShouldBeSameAs(first);
        other.ShouldNotBeSameAs(first);
        contexts.All.Length.ShouldBe(3);
        contexts.All[0].ShouldBeSameAs(contexts.Local);

        // A rediscovered rig keeps its context (and therefore its session state); only the label moves.
        first.DisplayName.ShouldBe("Observatory Pi (renamed)");
        first.IsLocal.ShouldBeFalse();
        first.NodeId.ShouldBe("node-a");
    }

    [Fact]
    public void EachContextOwnsItsOwnLiveSessionState()
    {
        var contexts = new ViewContexts();
        var remote = contexts.GetOrAddRemote("node-a", "Observatory Pi");

        contexts.Local.LiveSession.ShouldNotBeSameAs(remote.LiveSession);
    }

    [Fact]
    public void Activate_SwitchesWhatIsViewedWithoutTouchingLocal()
    {
        var contexts = new ViewContexts();
        var local = contexts.Local;
        local.LiveSession.IsRunning = true;
        local.LiveSession.ActiveSession = SessionAt(SessionPhase.Observing, framesWritten: 12);
        local.LiveSession.PollSession();

        var remote = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        contexts.Activate(remote).ShouldBeTrue();

        // What you are looking at changed...
        contexts.Active.ShouldBeSameAs(remote);
        contexts.IsRemoteActive.ShouldBeTrue();
        contexts.Active.LiveSession.IsRunning.ShouldBeFalse();

        // ...but the local node still owns and runs its session, untouched.
        contexts.Local.ShouldBeSameAs(local);
        local.LiveSession.IsRunning.ShouldBeTrue();
        local.LiveSession.HasActiveRun.ShouldBeTrue();
        local.LiveSession.Phase.ShouldBe(SessionPhase.Observing);
        local.LiveSession.TotalFramesWritten.ShouldBe(12);

        // And coming back is equally ungated.
        contexts.Activate(local).ShouldBeTrue();
        contexts.IsRemoteActive.ShouldBeFalse();
    }

    [Fact]
    public void Activate_RejectsAForeignContext()
    {
        var contexts = new ViewContexts();
        var foreign = new ViewContexts().GetOrAddRemote("node-a", "Someone else's set");

        contexts.Activate(foreign).ShouldBeFalse();
        contexts.Active.ShouldBeSameAs(contexts.Local);
    }

    [Fact]
    public void PollAll_RefreshesOffScreenContextsToo()
    {
        var contexts = new ViewContexts();
        var remote = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        contexts.Activate(remote);

        // The local session is hidden under the remote overlay -- it must still track its run.
        contexts.Local.LiveSession.ActiveSession = SessionAt(SessionPhase.Cooling, framesWritten: 3);
        remote.LiveSession.ActiveSession = SessionAt(SessionPhase.CalibratingGuider, framesWritten: 7);

        contexts.PollAll();

        contexts.Local.LiveSession.Phase.ShouldBe(SessionPhase.Cooling);
        contexts.Local.LiveSession.TotalFramesWritten.ShouldBe(3);
        remote.LiveSession.Phase.ShouldBe(SessionPhase.CalibratingGuider);
        remote.LiveSession.TotalFramesWritten.ShouldBe(7);
    }

    [Fact]
    public void AnyNeedsRedraw_SeesAnOffScreenContext()
    {
        var contexts = new ViewContexts();
        var remote = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        contexts.Activate(remote);
        contexts.ClearNeedsRedraw();
        contexts.AnyNeedsRedraw.ShouldBeFalse();

        // A background callback on the hidden local session must still earn a frame -- its
        // notifications and (later) the chrome's local-session indicator depend on one.
        contexts.Local.LiveSession.NeedsRedraw = true;
        contexts.AnyNeedsRedraw.ShouldBeTrue();

        contexts.ClearNeedsRedraw();
        contexts.AnyNeedsRedraw.ShouldBeFalse();
        contexts.Local.LiveSession.NeedsRedraw.ShouldBeFalse();
        remote.LiveSession.NeedsRedraw.ShouldBeFalse();
    }

    [Fact]
    public void SiteTimeZoneResolvesForAContextAddedAfterAttach()
    {
        var contexts = new ViewContexts();
        var appState = new GuiAppState { SiteTimeZone = TimeSpan.FromHours(10) };

        contexts.All.Single().ShouldBeSameAs(contexts.Local);

        // AttachAppState runs once during composition (AppSignalHandler's ctor), long before a rig is
        // discovered -- a later context has to pick the app state up too, or its times render as UTC.
        // Reachable directly here via InternalsVisibleTo, so no reflection.
        contexts.AttachAppState(appState);

        var remote = contexts.GetOrAddRemote("node-a", "Observatory Pi");
        contexts.Local.LiveSession.SiteTimeZone.ShouldBe(TimeSpan.FromHours(10));
        remote.LiveSession.SiteTimeZone.ShouldBe(TimeSpan.FromHours(10));
    }
}
