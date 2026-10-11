using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.IOptron;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// The SkyGuider Pro driver over <see cref="FakeSgpSerialDevice"/>. It knows where it TRULY points once a test places the
/// rig (<see cref="FakeSgpSerialDevice.PlaceAt"/>), so the fake camera renders the sky a move or the operator's hand put
/// in front of it, while the driver's own reads keep reporting only what it believes (its last sync), as on the real mount.
/// </summary>
internal class FakeSgpMountDriver(FakeDevice device, IServiceProvider serviceProvider)
    : SgpMountDriverBase<FakeDevice>(device, serviceProvider), IFakeTruePointingSource, IFakeMechanicalPointingStateSource
{
    /// <summary>
    /// The tube's mechanical side, which only the operator's swing of Dec over the pole changes
    /// (<see cref="FakeSgpSerialDevice.SwingDecOverThePoleByHand"/>), so the fake camera turns the field over on a flip
    /// and on nothing else; unknown before the rig is placed.
    /// </summary>
    public ValueTask<PointingState> GetMechanicalPointingStateAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(SerialDevice?.MechanicalPointingState ?? PointingState.Unknown);

    /// <summary>The fake serial device the true pointing is kept on; null before the driver connects.</summary>
    internal FakeSgpSerialDevice? SerialDevice => _deviceInfo.SerialDevice as FakeSgpSerialDevice;

    /// <summary>
    /// Places the rig, as the operator does at setup, on a J2000 position, converted to the native frame the true pointing
    /// is kept in (they differ by about 20 arcmin of precession in 2025).
    /// </summary>
    internal async ValueTask PlaceAtJ2000Async(double raJ2000, double decJ2000, CancellationToken cancellationToken)
    {
        var serial = SerialDevice ?? throw new InvalidOperationException("Connect the fake SkyGuider Pro before placing it");
        var transform = await ((IMountDriver)this).TryGetTransformAsync(cancellationToken)
            ?? throw new InvalidOperationException("The fake SkyGuider Pro has no site to place it with");
        var native = await ((IMountDriver)this).TryTransformJ2000ToMountNativeAsync(transform, raJ2000, decJ2000, updateTime: true, cancellationToken)
            ?? throw new InvalidOperationException("The J2000 position cannot be converted to the mount's native frame");
        serial.PlaceAt(native.RaMount, native.DecMount);
    }

    public async ValueTask<(double Ra, double Dec)> GetTruePointingNativeAsync(CancellationToken cancellationToken)
        => SerialDevice?.TruePointing is { } truth
            ? truth
            : (await GetRightAscensionAsync(cancellationToken), await GetDeclinationAsync(cancellationToken));
}
