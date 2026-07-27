using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

public record OTA(
    string Name,
    int FocalLength,
    Camera Camera,
    Cover? Cover,
    Focuser? Focuser,
    FocusDirection FocusDirection,
    FilterWheel? FilterWheel,
    Switch? Switches,
    int? Aperture = null,
    OpticalDesign OpticalDesign = OpticalDesign.Unknown
    ) : IAsyncDisposable
{
    /// <summary>
    /// Every device URI on this OTA. Walks the same slots as <see cref="DisposeAsync"/> -- see
    /// <see cref="Setup.DeviceUris"/> for why the two are kept adjacent.
    /// </summary>
    public IEnumerable<Uri> DeviceUris()
    {
        yield return Camera.Device.DeviceUri;

        if (Cover is { } cover)
        {
            yield return cover.Device.DeviceUri;
        }

        if (Focuser is { } focuser)
        {
            yield return focuser.Device.DeviceUri;
        }

        if (FilterWheel is { } filterWheel)
        {
            yield return filterWheel.Device.DeviceUri;
        }

        if (Switches is { } switches)
        {
            yield return switches.Device.DeviceUri;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Camera.DisposeAsync();
        if (Cover is { } cover)
        {
            await cover.DisposeAsync();
        }

        if (Focuser is { } focuser)
        {
            await focuser.DisposeAsync();
        }

        if (FilterWheel is { } filterWheel)
        {
            await filterWheel.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}