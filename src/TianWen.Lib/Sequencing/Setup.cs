using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Equipment bundle bound to a <see cref="Session"/>: one mount, one guider, one or more
/// OTAs, and optional weather.
///
/// <para><b>Single-mount / multi-OTA invariant.</b> <see cref="Telescopes"/> is plural
/// for dual- / triple-saddle rigs (e.g. a side-by-side + piggyback setup). All OTAs
/// ride the same <see cref="Mount"/> at all times, so they share pointing and therefore
/// share the current target. The session never images two OTAs on two different targets
/// simultaneously: it can't, there is only one mount. What multi-OTA does give us is
/// parallel capture (each OTA has its own camera, filter wheel, and focuser) and
/// per-OTA focus / filter / baseline state. Any future "branch" or "re-order" logic in
/// the observation loop must operate on the whole OTA set as a unit.</para>
/// </summary>
public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderSetup GuiderSetup,
    ImmutableArray<OTA> Telescopes,
    Weather? Weather = null
) : IAsyncDisposable
{
    /// <summary>
    /// Every device URI this setup drives, in no particular order and possibly with duplicates (the same
    /// physical camera can fill both an OTA slot and the OAG guide-camera slot).
    /// <para>
    /// This is what a run claims from <see cref="TianWen.Lib.Devices.IDeviceHub"/> so nothing else can
    /// disconnect or command its hardware mid-night -- see
    /// <see cref="TianWen.Lib.Devices.DeviceLeaseSet.Acquire"/>, which de-duplicates. Kept beside
    /// <see cref="DisposeAsync"/> deliberately: the two must walk the same set, so a device added to one
    /// and forgotten in the other is visible in a single screenful.
    /// </para>
    /// </summary>
    public IEnumerable<Uri> DeviceUris()
    {
        yield return Mount.Device.DeviceUri;
        yield return Guider.Device.DeviceUri;

        if (GuiderSetup.Camera is { } guideCamera)
        {
            yield return guideCamera.Device.DeviceUri;
        }

        if (GuiderSetup.Focuser is { } guideFocuser)
        {
            yield return guideFocuser.Device.DeviceUri;
        }

        foreach (var telescope in Telescopes)
        {
            foreach (var uri in telescope.DeviceUris())
            {
                yield return uri;
            }
        }

        if (Weather is { } weather)
        {
            yield return weather.Device.DeviceUri;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Mount.DisposeAsync();
        await Guider.DisposeAsync();
        if (GuiderSetup.Camera is { } camera)
        {
            await camera.DisposeAsync();
        }
        if (GuiderSetup.Focuser is { } focuser)
        {
            await focuser.DisposeAsync();
        }

        foreach (var telescope in Telescopes)
        {
            await telescope.DisposeAsync();
        }

        if (Weather is { } weather)
        {
            await weather.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
