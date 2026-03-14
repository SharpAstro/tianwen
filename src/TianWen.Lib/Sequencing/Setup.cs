using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderSetup GuiderSetup,
    IReadOnlyList<OTA> Telescopes
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

        GC.SuppressFinalize(this);
    }
}
