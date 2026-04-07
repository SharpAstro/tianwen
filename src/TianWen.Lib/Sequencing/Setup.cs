using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderSetup GuiderSetup,
    ImmutableArray<OTA> Telescopes,
    Weather? Weather = null
) : IAsyncDisposable
{
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
