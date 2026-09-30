using FC.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Canon;

/// <summary>
/// What <see cref="CanonDeviceSource"/> needs from Windows Portable Devices, so a test can answer it without a camera.
/// </summary>
internal interface ICanonWpd
{
    /// <summary>The Canon cameras Windows lists, as their WPD ids and friendly names. Opens nothing.</summary>
    IEnumerable<(string Id, string Name)> Enumerate();

    /// <summary>
    /// The body's model and DeviceInfo serial, read with no PTP session (one <c>GetDeviceInfo</c>); null when the body
    /// reports no serial. This opens the device, which is why the source reads it once per path and never for a camera a
    /// driver holds (#1097).
    /// </summary>
    Task<CanonBodyIdentity?> ReadIdentityAsync(string wpdId, CancellationToken cancellationToken);
}

/// <summary><see cref="ICanonWpd"/> through FC.SDK. On any other platform nothing is listed and nothing is read.</summary>
internal sealed class FcSdkCanonWpd : ICanonWpd
{
    public IEnumerable<(string Id, string Name)> Enumerate() => OperatingSystem.IsWindows() ? EnumerateOnWindows() : [];

    public Task<CanonBodyIdentity?> ReadIdentityAsync(string wpdId, CancellationToken cancellationToken)
        => OperatingSystem.IsWindows()
            ? CanonCamera.ReadWpdIdentityAsync(wpdId, cancellationToken)
            : Task.FromResult<CanonBodyIdentity?>(null);

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Id, string Name)> EnumerateOnWindows()
        => CanonCamera.EnumerateWpdCameras().Select(static camera => (camera.DeviceId, camera.FriendlyName));
}
