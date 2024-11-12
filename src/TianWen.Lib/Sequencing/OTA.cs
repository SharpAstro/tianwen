using System;
using System.Threading.Tasks;

namespace TianWen.Lib.Sequencing;

public record OTA(
    string Name,
    int FocalLength,
    Camera Camera,
    Cover? Cover,
    Focuser? Focuser,
    FocusDirection FocusDirection,
    FilterWheel? FilterWheel,
    Switch? Switches
    ) : IAsyncDisposable
{
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