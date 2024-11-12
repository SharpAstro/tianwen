using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public record Setup(
    Mount Mount,
    Guider Guider,
    GuiderSetup GuiderFocuser,
    IReadOnlyList<OTA> Telescopes
) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Mount.DisposeAsync();
        await Guider.DisposeAsync();
        if (GuiderFocuser.Focuser is { } focuser)
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