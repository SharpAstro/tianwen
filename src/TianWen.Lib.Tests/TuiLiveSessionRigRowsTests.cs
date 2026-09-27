using Console.Lib;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Cli.Tui;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The TUI's Live Session rows are the view's own (P5b part 9 of docs/plans/hardware-in-the-server.md, #935): the real
/// tab over the GUI's real signal handler. An idle rig's rows listed this computer's OTAs, since they were built from this
/// computer's profile, and a running view's mount row printed a pointing not read yet through formatters that throw on NaN.
/// </summary>
public class TuiLiveSessionRigRowsTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2025, 12, 15, 21, 0, 0, TimeSpan.Zero);

    private static TuiLiveSessionTab Render(GuiSignalHarness h)
    {
        var terminal = Substitute.For<IVirtualTerminal>();
        terminal.Size.Returns((120, 40));
        terminal.CellSize.Returns(new TermCell(1, 1));
        var tab = new TuiLiveSessionTab(h.AppState, h.Contexts, terminal, new FakeTimeProviderWrapper(Now), h.Bus);
        tab.Attach(terminal);
        tab.Render();
        return tab;
    }

    [Fact(Timeout = 60_000)]
    public async Task AnIdleRigsRowsAreItsOwnOtas()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        // Two OTAs to this computer's one, so rows built from this computer's profile cannot pass for the rig's.
        var rigCamera = new FakeDevice(DeviceType.Camera, 7);
        var wideCamera = new FakeDevice(DeviceType.Camera, 8);
        var data = new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
        [
            new OTAData("Rig scope", 1000, rigCamera.DeviceUri, null, null, null, null, null),
            new OTAData("Rig wide", 250, wideCamera.DeviceUri, null, null, null, null, null),
        ]);
        var rig = h.Contexts.GetOrAddRemote("observatory-node", "Observatory");
        rig.RigProfile = new Profile(Guid.NewGuid(), "Observatory rig", data);
        RigDevices.Apply(rig.LiveSession, data,
        [
            new DeviceStateDto
            {
                DeviceUri = rigCamera.DeviceUri.ToString(),
                DeviceType = DeviceType.Camera,
                Connected = true,
                Camera = CameraDeviceStateDto.FromReading(new CameraReading(-10, 25, -10, 40, true, CameraState.Idle, true, false,
                    0, 100, 50, [], 1920, 1080, default), intent: null),
            },
        ]);
        h.Contexts.Activate(rig).ShouldBeTrue();

        // The OTAs' headings, numbered; the exposure log below them has a heading of its own.
        var headings = Render(h).InfoRows.OfType<HeadingRow>().Select(r => r.Text).Where(t => t is [>= '1' and <= '9', ..]).ToList();

        headings.ShouldBe([$"1: {rigCamera.DisplayName}", "2: Rig wide"], "a camera its node does not hold is named by its OTA");
    }

    [Fact(Timeout = 60_000)]
    public async Task ARunsMountRowSaysItDoesNotKnowThePointingYet()
    {
        await using var h = await GuiSignalHarness.StartAsync(output, TestContext.Current.CancellationToken);
        var session = Substitute.For<ISessionTelemetry>();
        session.TelescopeDisplays.Returns(ImmutableArray<TelescopeDisplayInfo>.Empty);
        session.Observations.Returns(new ScheduledObservationTree([]));
        session.MountDisplayName.Returns("Test mount");
        var view = h.Contexts.Local.LiveSession;
        view.ActiveSession = session;
        view.IsRunning = true;

        var rows = Render(h).InfoRows.OfType<TextRow>().Select(r => r.Text).ToList();

        rows.ShouldContain("RA --  HA --");
        rows.ShouldContain("Dec --");
    }
}
