using System.Linq;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting;

/// <summary>
/// Whether the node holds hardware: ONE rule for <see cref="NodeIdleExit"/> (which never exits while it does) and
/// <see cref="NodeKeepsAwake"/> (which keeps the computer awake while it does). A connected device, a lease, a run or a
/// running job (a warm-up is a job).
/// </summary>
internal static class NodeHoldsHardware
{
    /// <summary>What holds hardware now, in words, or null when nothing does.</summary>
    public static string? What(IDeviceHub hub, IHostedSession hosted, NodeJobs jobs)
    {
        if (hub.ConnectedDevices.Count > 0)
        {
            return "a device is connected";
        }
        if (hub.Leases.Count > 0)
        {
            return "a device is leased";
        }
        if (hosted.RunningKind is { } kind)
        {
            return $"a {kind} run is going on";
        }
        if (jobs.List().Any(static job => job.State is JobState.Running))
        {
            return "a job is running";
        }
        return null;
    }
}
