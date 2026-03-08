using System;
using System.Runtime.Versioning;

namespace TianWen.Lib.Devices.Ascom.ComInterop;

/// <summary>
/// Wraps an ASCOM COM device ProgID as a <see cref="DispatchObject"/>.
/// Provides the common ASCOM device members (Connected, Name, Description, etc.)
/// </summary>
[SupportedOSPlatform("windows")]
[DispatchInterface]
internal sealed partial class AscomDispatchDevice : IDisposable
{
    public AscomDispatchDevice(string progId)
    {
        _dispatch = new DispatchObject(progId);
    }

    // Common ASCOM device members — generated implementations via source generator
    public partial string Name { get; }
    public partial string? Description { get; }
    public partial string? DriverInfo { get; }
    public partial string? DriverVersion { get; }
    public partial bool Connected { get; set; }
    public partial bool Connecting { get; }
    public partial short InterfaceVersion { get; }

    // Connect/Disconnect are methods in ASCOM Platform 7+
    public partial void Connect();
    public partial void Disconnect();

    /// <summary>
    /// Access the underlying dispatch object for device-specific calls.
    /// </summary>
    internal DispatchObject Dispatch => _dispatch;

    public void Dispose() => _dispatch.Dispose();
}
