using NSubstitute;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;
using static TianWen.Lib.Tests.RemoteSessionMirrorDriveTests;

namespace TianWen.Lib.Tests;

/// <summary>
/// A rig's view is of the rig, its site included (P5b part 8 of docs/plans/hardware-in-the-server.md, #935): the planner
/// plans with the profile of the view on show, so a rig's nights are planned at its site and its clock and twilight are
/// drawn in its zone; the sky map draws the rig's schedule, the one being imaged matched as an object is. Before this every
/// rig was planned, clocked and twilit at this computer's site, and its reticle carried this computer's sensor.
/// </summary>
public class RigSiteTests(ITestOutputHelper output)
{
    private const double SydneyLatitude = -33.87;
    private const double SydneyLongitude = 151.21;

    private static Profile RigProfile() => new Profile(Guid.NewGuid(), "Observatory rig", new ProfileData(
        Mount: new FakeDevice(DeviceType.Mount, 7).DeviceUri,
        Guider: new FakeDevice(DeviceType.Guider, 7).DeviceUri,
        OTAs: [new OTAData("Rig scope", 1000, new FakeDevice(DeviceType.Camera, 7).DeviceUri, null, null, null, null, null)],
        SiteLatitude: SydneyLatitude,
        SiteLongitude: SydneyLongitude));

    private static async Task RecomputedAsync(GuiSignalHarness gui)
    {
        gui.Handler.CheckRecompute();
        await gui.Tracker.DrainAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task TheRigOnShowIsPlannedAtItsSiteAndThisComputersViewAtThisComputers()
    {
        await using var gui = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var rig = gui.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        rig.RigProfile = RigProfile();

        gui.Contexts.Activate(rig).ShouldBeTrue();
        gui.Handler.ProfileOnShow.ShouldBeSameAs(rig.RigProfile);
        await RecomputedAsync(gui);

        gui.Planner.SiteLatitude.ShouldBe(SydneyLatitude, 1e-9);
        gui.Planner.SiteLongitude.ShouldBe(SydneyLongitude, 1e-9);
        gui.AppState.SiteTimeZone.TotalHours.ShouldBeGreaterThanOrEqualTo(10, "the rig's clock is Sydney's, +10 or +11");

        gui.Contexts.Activate(gui.Contexts.Local).ShouldBeTrue();
        await RecomputedAsync(gui);

        gui.Planner.SiteLatitude.ShouldBe(48.2, 1e-9, "this computer's view is planned at this computer's site again");
        gui.AppState.SiteTimeZone.TotalHours.ShouldBeLessThanOrEqualTo(2, "Vienna, +1 or +2");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARigWhoseProfileIsNotReadYetIsNotPlannedAtThisComputersSite()
    {
        await using var gui = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var rig = gui.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        gui.Contexts.Activate(rig).ShouldBeTrue();

        await RecomputedAsync(gui);

        gui.Handler.ProfileOnShow.ShouldBeNull();
        gui.Planner.NeedsRecompute.ShouldBeTrue("it waits for the rig's profile rather than planning at the wrong site");
        double.IsNaN(gui.Planner.SiteLatitude).ShouldBeTrue("nothing planned yet");
    }

    /// <summary>
    /// A rig with no pins of its own shows none, not this computer's: a pin load replaces the plan only when the view has
    /// pins saved, so they are dropped on a switch, or the next save would write them into the rig's file.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARigWithNoPinsOfItsOwnShowsNoneOfThisComputers()
    {
        await using var gui = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        gui.Planner.Proposals = [new ProposedObservation(new Target(5.588, -5.39, "M42", CatalogIndex.NGC1976))];
        var rig = gui.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        rig.RigProfile = RigProfile();

        gui.Contexts.Activate(rig).ShouldBeTrue();
        await RecomputedAsync(gui);

        gui.Planner.Proposals.ShouldBeEmpty();
    }

    [Fact(Timeout = 60_000)]
    public async Task EditingThisComputersSiteWhileARigIsOnShowLeavesTheRigsSite()
    {
        await using var gui = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var rig = gui.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        rig.RigProfile = RigProfile();
        gui.Contexts.Activate(rig).ShouldBeTrue();
        await RecomputedAsync(gui);

        // The Equipment tab edits this computer's profile, which a rig's view is not planned with.
        gui.Equipment.LatitudeInput.Text = "51.5";
        gui.Equipment.LongitudeInput.Text = "-0.1";
        gui.Equipment.ElevationInput.Text = "20";
        await gui.Equipment.LatitudeInput.OnCommit.ShouldNotBeNull()("51.5");

        gui.AppState.ActiveProfile.ShouldNotBeNull().Data.ShouldNotBeNull().SiteLatitude.ShouldBe(51.5, "this computer's profile took the edit");
        gui.Planner.SiteLatitude.ShouldBe(SydneyLatitude, 1e-9, "the rig on show is still planned at its own site");
    }

    // -------------------------------------------------------------------------------------------
    // The sky map's schedule
    // -------------------------------------------------------------------------------------------

    private static ScheduledObservation Observation(Target target) =>
        new ScheduledObservation(target, DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), AcrossMeridian: false, FilterPlan: [], Gain: null, Offset: null);

    [Fact]
    public void APlanetBeingImagedIsTheOnePlannedWhereverItHasMovedTo()
    {
        var planned = new Target(5.0, 20.0, "Jupiter", CatalogIndex.Jupiter);
        var imaged = new Target(5.01, 20.02, "Jupiter", CatalogIndex.Jupiter);

        var markers = SkyMapScheduleMarkers.Build(new ScheduledObservationTree([Observation(planned)]), imaged);

        markers.ShouldHaveSingleItem().IsActive.ShouldBeTrue("a planet's coordinates are an instant's, its identity its index");
    }

    [Fact]
    public void OneMosaicPanelBeingImagedLightsThatPanelAlone()
    {
        var panel1 = new Target(5.58, -5.39, "M42 panel 1", CatalogIndex.NGC1976);
        var panel2 = new Target(5.62, -5.39, "M42 panel 2", CatalogIndex.NGC1976);

        var markers = SkyMapScheduleMarkers.Build(new ScheduledObservationTree([Observation(panel1), Observation(panel2)]), panel2);

        markers.Select(m => m.IsActive).ShouldBe([false, true], "panels share an index and are told apart by their centres");
    }

    [Fact]
    public async Task ARigsScheduleIsMappedOncePerPolledState()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mirror, _) = BuildMirror(_ => Json(ResponseEnvelope<SessionStateDto>.Ok(RemoteSessionMirrorTests.RunningState())));
        await using (mirror)
        {
            await mirror.PollOnceAsync(ct);
            var first = mirror.Observations;
            first.Count.ShouldBeGreaterThan(0);
            mirror.Observations.ShouldBeSameAs(first, "the same state, the same tree: the sky map and the view key on it");

            await mirror.PollOnceAsync(ct);
            mirror.Observations.ShouldNotBeSameAs(first, "a new state is mapped anew");
        }
    }
}
