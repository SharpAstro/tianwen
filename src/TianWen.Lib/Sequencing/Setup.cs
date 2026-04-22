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
/// simultaneously — it can't, there is only one mount. What multi-OTA does give us is
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
