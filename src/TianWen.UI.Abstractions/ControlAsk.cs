namespace TianWen.UI.Abstractions;

/// <summary>Where this client's own request for control of a node stands (P6b of docs/plans/hardware-in-the-server.md, #1021).</summary>
public enum ControlAskState
{
    /// <summary>Not asking: never asked, granted, or stopped.</summary>
    None,

    /// <summary>Asked, and waiting for the rig's machine to answer.</summary>
    Asking,

    /// <summary>The rig's machine declined.</summary>
    Declined,

    /// <summary>The request went nowhere: refused, lapsed, or the rig stopped answering. <see cref="ControlAsk.Message"/> says which.</summary>
    Failed,
}

/// <summary>A request for control as the view shows it: its state and, once it ended without a grant, why.</summary>
public sealed record ControlAsk(ControlAskState State, string? Message)
{
    /// <summary>Nothing asked.</summary>
    public static readonly ControlAsk None = new ControlAsk(ControlAskState.None, null);
}
