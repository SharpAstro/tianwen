using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Hosting;

/// <summary>
/// A run of the node that streams its camera through a <see cref="PlanetaryCapture"/>: a planetary capture or a Preview live
/// view. A device-plane action that must happen with the stream stopped (a filter change of the wheel it streams through) is
/// that capture's interlude (<see cref="PlanetaryCapture.InterludeAsync"/>), and the node finds the capture through its run,
/// whichever kind it is, never through a UI flag.
/// </summary>
internal interface INodeStreamingRun : INodeRun
{
    /// <summary>The run's capture loop.</summary>
    PlanetaryCapture Capture { get; }

    /// <summary>Until the run's body has ended.</summary>
    bool IsRunning { get; }

    /// <summary>The filter wheel of the OTA the run streams, as the profile named it when the run was prepared; null when it has none.</summary>
    Uri? FilterWheel { get; }

    /// <summary>
    /// Whether this run's stream goes through <paramref name="filterWheel"/> now: it runs, it is streaming, and the wheel is its
    /// OTA's. A filter change of that wheel is then the capture's interlude.
    /// </summary>
    bool StreamsThrough(Uri filterWheel)
        => IsRunning && Capture.IsCapturing && FilterWheel is { } wheel
           && DeviceUriExtensions.DeviceKeyComparer.Equals(wheel.DeviceKey, filterWheel.DeviceKey);
}
