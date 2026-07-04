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
    public AscomDispatchDevice(string progId, IServiceProvider? serviceProvider = null)
    {
        // The transport is in-proc DispatchObject for CET-safe drivers, or an out-of-process CET-off host
        // for in-proc .NET Framework drivers that would otherwise fastfail on connect. See
        // DispatchTransportFactory + docs/plans/ascom-oop-host.md.
        _dispatch = DispatchTransportFactory.Create(progId, serviceProvider);
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
    /// Access the underlying dispatch transport for device-specific calls (in-proc or out-of-process).
    /// </summary>
    internal IDispatchTransport Dispatch => _dispatch;

    public void Dispose() => _dispatch.Dispose();
}
