using DIR.Lib;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

/// <summary>What a quit can do with the rig this computer's node holds (decision 1 of docs/plans/hardware-in-the-server.md).</summary>
public enum QuitAction
{
    /// <summary>Close this window and leave the node's run going: the night goes on without it.</summary>
    LeaveTheRigRunning,

    /// <summary>Stop the node's run through its own ending, then warm up and disconnect every device, and quit once done.</summary>
    StopTheRig,

    /// <summary>Warm up and disconnect every device as the node's jobs, which finish after this window has gone.</summary>
    WarmUpAndDisconnect,

    /// <summary>Close this window and leave the devices connected on the node.</summary>
    LeaveConnected,

    /// <summary>
    /// Disconnect every device as the node's jobs, when no camera needs warming: the same jobs as
    /// <see cref="WarmUpAndDisconnect"/>, which warm only a camera that needs it, under the words that say what they will do.
    /// </summary>
    Disconnect,
}

/// <summary>
/// The question a quit asks, and only the last client of this computer's node asks it (decision 1 of
/// docs/plans/hardware-in-the-server.md, #936): with a run going on, whether to leave it running (the default) or stop the
/// rig; with devices connected and no run, whether to disconnect them (the default), warming up first only a camera that
/// needs it (<see cref="CameraReading.NeedsWarmUp"/>), or leave them connected. Enter takes the default, <see cref="OtherKey"/> the other, Escape stays. One description for the GUI's card
/// and the TUI's line.
/// </summary>
public sealed record QuitDialog(string Title, string Message, QuitAction Default, QuitAction Other)
{
    /// <summary>The words on a choice's button.</summary>
    public static string LabelOf(QuitAction action) => action switch
    {
        QuitAction.LeaveTheRigRunning => "Leave the rig running",
        QuitAction.StopTheRig => "Stop the rig and quit",
        QuitAction.WarmUpAndDisconnect => "Warm up and disconnect",
        QuitAction.Disconnect => "Disconnect",
        _ => "Leave connected",
    };

    /// <summary>The key that takes the choice that is not the default: S to stop the rig, L to leave it connected.</summary>
    public InputKey OtherKey => Other is QuitAction.StopTheRig ? InputKey.S : InputKey.L;

    /// <summary>The keys, as the dialog says them.</summary>
    public string KeyHint => $"Enter: {LabelOf(Default)}   {OtherKey}: {LabelOf(Other)}   Escape: stay";

    /// <summary>The whole question on one line, for a host with one line to show it on (the TUI's status bar).</summary>
    public string OneLine => $"{Message} [Enter] {LabelOf(Default)}  [{OtherKey}] {LabelOf(Other)}  [Esc] Stay";

    /// <summary>A run is going on: it is left running unless the user asks for the rig to be stopped.</summary>
    public static QuitDialog RunGoingOn(NodeRunDto run) => new QuitDialog("Quit TianWen",
        $"{Describe(run)} on this computer. Leaving it running closes only this window, and the run goes on without it.",
        QuitAction.LeaveTheRigRunning, QuitAction.StopTheRig);

    /// <summary>
    /// Devices are connected and nothing runs: they are disconnected unless the user leaves them, and a warm-up is spoken of
    /// only when <paramref name="aCameraNeedsWarming"/> (a cooler on, the sensor below ambient). A camera with no cooler has
    /// nothing to warm, and the question once offered to warm an uncooled ASI462MC (the ZWO live check, 2026-09-28).
    /// </summary>
    public static QuitDialog DevicesConnected(int count, bool aCameraNeedsWarming)
    {
        var connected = $"{(count == 1 ? "A device is" : $"{count} devices are")} connected on this computer.";
        return aCameraNeedsWarming
            ? new QuitDialog("Quit TianWen", $"{connected} A warm-up goes on after this window has closed.",
                QuitAction.WarmUpAndDisconnect, QuitAction.LeaveConnected)
            : new QuitDialog("Quit TianWen", connected, QuitAction.Disconnect, QuitAction.LeaveConnected);
    }

    /// <summary>The run, as the dialog names it: its kind, and its target when it has one.</summary>
    public static string Describe(NodeRunDto run)
    {
        var what = run.Kind switch
        {
            NodeRunKind.Session => "A session is running",
            NodeRunKind.Flats => "A flat run is running",
            NodeRunKind.Darks => "A dark library is being taken",
            NodeRunKind.Polar => "Polar alignment is running",
            NodeRunKind.Planetary => "A planetary capture is running",
            _ => "A run is going on",
        };
        return run.Target is { Length: > 0 } target ? $"{what} ({target})" : what;
    }
}
