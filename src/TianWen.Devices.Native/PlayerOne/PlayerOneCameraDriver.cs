using System;
using System.Threading;
using System.Threading.Tasks;
using PlayerOne.SDK;
using TianWen.DAL;
using TianWen.Lib.Devices.DAL;
using static PlayerOne.SDK.PlayerOneCamera;

namespace TianWen.Lib.Devices.PlayerOne;

internal class PlayerOneCameraDriver(PlayerOneDevice device, IServiceProvider sp)
    : DALCameraDriver<PlayerOneDevice, POACameraProperties>(device, sp)
{
    public override string? DriverInfo => $"Player One Camera Driver v{DriverVersion}";

    public override string? Description { get; } = $"Player One Camera driver using C# SDK wrapper v{POAGetSDKVersion()}";

    /// <summary>
    /// One microsecond: <c>POA_EXPOSURE</c> is stated in microseconds, with a range starting at 10.
    /// </summary>
    public override double ExposureResolution { get; } = 1E-06;

    protected override INativeDeviceIterator<POACameraProperties> NewIterator() => new DeviceIterator<POACameraProperties>();

    protected override Exception NotConnectedException() => new PlayerOneDriverException(POAErrors.POA_ERROR_NOT_OPENED, "Camera is not connected");

    /// <summary>
    /// Wraps a DAL error back into a vendor-flavoured exception.
    /// </summary>
    /// <remarks>
    /// Unlike the ZWO driver, this does NOT cast the DAL code back to the vendor enum. The two
    /// enums happen to agree numerically for ZWO because the DAL's codes were derived from ASI's;
    /// Player One's ordering is its own, so a cast would name a plausible but wrong error
    /// (<c>POA_ERROR_INVALID_ID</c> for a closed camera, say). The DAL code is the accurate thing
    /// to report here, and the mapping to it already happened in the binding.
    /// </remarks>
    protected override Exception OperationalException(CMOSErrorCode errorCode, string message)
        => new PlayerOneDriverException(POAErrors.POA_ERROR_OPERATION_FAILED, $"{message} ({errorCode})");

    /// <summary>
    /// Nothing further to do: <see cref="POACameraProperties.Open"/> already ran
    /// <c>POAInitCamera</c>.
    /// </summary>
    /// <remarks>
    /// Player One splits open and init like ZWO does, but the two are not usefully separable here:
    /// almost nothing is valid on an opened-but-uninitialised camera, and <c>POAInitCamera</c> is
    /// not documented as safe to call twice, so doing it once inside <c>Open</c> is preferable to
    /// doing it here and risking a second call on a camera discovery already initialised.
    /// </remarks>
    protected override ValueTask<bool> InitDeviceAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
