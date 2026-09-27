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
    public void WithDevicesConnectedWarmingThemUpIsTheDefaultAndLLeavesThemConnected(int count, string message)
    {
        var dialog = QuitDialog.DevicesConnected(count);

        (dialog.Default, dialog.Other, dialog.OtherKey).ShouldBe((QuitAction.WarmUpAndDisconnect, QuitAction.LeaveConnected, InputKey.L));
        dialog.Message.ShouldStartWith(message);
        dialog.KeyHint.ShouldBe("Enter: Warm up and disconnect   L: Leave connected   Escape: stay");
    }
}
