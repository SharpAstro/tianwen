using System;
using DIR.Lib;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The quit's question as both hosts show it (decision 1 of docs/plans/hardware-in-the-server.md, #936): what it offers, which
/// choice is the default, and the key for the other. Its behaviour over a node is <c>QuitThroughTheNodeTests</c>'s.
/// </summary>
public class QuitDialogTests
{
    [Fact]
    public void WithARunGoingOnLeavingItRunningIsTheDefaultAndSStopsTheRig()
    {
        var dialog = QuitDialog.RunGoingOn(new NodeRunDto { Kind = NodeRunKind.Session, Target = "M 81", StartedUtc = DateTimeOffset.UnixEpoch });

        (dialog.Default, dialog.Other, dialog.OtherKey).ShouldBe((QuitAction.LeaveTheRigRunning, QuitAction.StopTheRig, InputKey.S));
        dialog.Message.ShouldStartWith("A session is running (M 81) on this computer.");
        dialog.OneLine.ShouldEndWith("[Enter] Leave the rig running  [S] Stop the rig and quit  [Esc] Stay");
    }

    [Theory]
    [InlineData(1, "A device is connected on this computer.")]
    [InlineData(3, "3 devices are connected on this computer.")]
    public void WithACameraToWarmWarmingThemUpIsTheDefaultAndLLeavesThemConnected(int count, string message)
    {
        var dialog = QuitDialog.DevicesConnected(count, aCameraNeedsWarming: true);

        (dialog.Default, dialog.Other, dialog.OtherKey).ShouldBe((QuitAction.WarmUpAndDisconnect, QuitAction.LeaveConnected, InputKey.L));
        dialog.Message.ShouldStartWith(message);
        dialog.KeyHint.ShouldBe("Enter: Warm up and disconnect   L: Leave connected   Escape: stay");
    }

    /// <summary>
    /// With nothing cooled, the question says nothing of a warm-up: it once offered to warm an uncooled ASI462MC (the ZWO
    /// live check, 2026-09-28).
    /// </summary>
    [Fact]
    public void WithNoCameraToWarmDisconnectingIsTheDefaultAndNothingIsSaidOfAWarmUp()
    {
        var dialog = QuitDialog.DevicesConnected(3, aCameraNeedsWarming: false);

        (dialog.Default, dialog.Other, dialog.OtherKey).ShouldBe((QuitAction.Disconnect, QuitAction.LeaveConnected, InputKey.L));
        dialog.Message.ShouldBe("3 devices are connected on this computer.");
        dialog.KeyHint.ShouldBe("Enter: Disconnect   L: Leave connected   Escape: stay");
    }

    /// <summary>
    /// Only a camera whose cooler is on and whose sensor is below where the warm-up takes it (the heat sink, else +25 °C)
    /// has anything to warm; the node's ramp and the quit question ask the same rule.
    /// </summary>
    [Theory]
    [InlineData(true, -10.0, 18.0, true)]            // cooling, well below the heat sink
    [InlineData(true, 17.5, 18.0, false)]            // cooler on, sensor already at the heat sink
    [InlineData(true, 20.0, double.NaN, true)]       // no heat sink: warmed toward +25 °C
    [InlineData(true, double.NaN, double.NaN, true)] // no sensor reading: taken as cold
    [InlineData(false, -10.0, 18.0, false)]          // cooler off: the ramp leaves it
    [InlineData(false, 20.0, double.NaN, false)]     // no cooler at all (an ASI462MC)
    public void ACameraNeedsWarmingOnlyWhileItsCoolerIsOnAndItsSensorIsBelowAmbient(bool coolerOn, double ccdC, double heatsinkC, bool needs)
    {
        TianWen.Lib.Devices.CameraReading.NeedsWarmUpFrom(coolerOn, ccdC, heatsinkC).ShouldBe(needs);
    }
}
