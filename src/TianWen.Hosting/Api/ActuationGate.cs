using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api
{
    /// <summary>
    /// Whether a client's request may command a device: never one a run is driving. One rule, the hub lease
    /// (<see cref="DeviceOwnershipGate"/>), which the Alpaca plane and the GUI already asked.
    /// </summary>
    /// <remarks>
    /// The native mount and OTA routes and every ninaAPI actuation route commanded the session's own drivers
    /// without asking, so a client could slew the mount, move a focuser or abort an exposure under a running
    /// night (P0b item 8 of docs/plans/hardware-in-the-server.md, #752). A route asks as soon as it has the
    /// device, before any capability check or driver access: "a run is using this" is the answer that matters,
    /// and the one that tells the client what to do (stop the run). Reads are never gated.
    /// </remarks>
    internal static class ActuationGate
    {
        /// <summary>The refusal a client gets, naming the owner, or <see langword="null"/> when it may go ahead.</summary>
        internal static string? Refusal(IDeviceHub hub, DeviceBase device)
        {
            var verdict = DeviceOwnershipGate.Evaluate(hub, device.DeviceUri, DeviceAction.Actuate);
            return verdict.Allowed ? null : verdict.Describe();
        }
    }
}
