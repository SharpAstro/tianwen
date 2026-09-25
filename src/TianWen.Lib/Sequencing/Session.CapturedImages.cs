using System;
using System.Threading;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// The preview slots: the last frame each OTA captured, for everything that is not the session to read
/// (the GUI and TUI live panes, the node's previews, a client that attaches mid-sub).
/// </summary>
/// <remarks>
/// <para><b>A published frame stays readable until the next one replaces it.</b> The slot takes its OWN
/// lease on every frame it is given, so the publisher keeps its ref and releases it when IT is done (the
/// imaging loop after the FITS write, autofocus once the rung's stars are counted, flats at once), and
/// what the slot shows is unaffected. The imaging loop used to publish its bare frame and release it after
/// the write, which left the slot pointing at a released frame for the rest of the exposure: a reader that
/// leased properly had nothing to show for all but the second or so between the frame landing and its
/// write, and rough focus never released its frames at all (P0b item 15 of
/// docs/plans/hardware-in-the-server.md, #752).</para>
/// <para><b>Every publisher swaps before it releases</b>, the guider's order, so a refused lease means
/// the frame was replaced between reading the slot and leasing it, and re-reading converges in one step.</para>
/// <para><b>The price is one camera buffer per OTA.</b> The camera cannot recycle the buffer a slot is
/// showing, so it takes a second one for the next download (a 26 MP frame is 104 MB as floats). That is
/// what it costs to have the last frame to show for the whole of the next exposure, which a client that
/// attaches mid-sub cannot do without.</para>
/// <para>The slots are emptied at the start of every run, at the end of its finaliser (after the park and
/// the warm-up, so the last frame stays on show through both) and on disposal. An autofocus run empties
/// its own OTA's slot as it ends.</para>
/// </remarks>
internal partial record Session
{
    public Image?[] LastCapturedImages => _lastCapturedImages;
    private volatile Image?[] _lastCapturedImages = [];

    // The change token per slot. Taken from ONE sequence for the whole session, so a number is never reused:
    // not when a run re-allocates the slots, not across OTAs, and not across targets (the camera's own frame
    // number restarts at every target and names the exposure in progress, a sub ahead of the slot).
    private volatile int[] _lastCapturedImageNumbers = [];
    private int _capturedImageSequence;

    /// <inheritdoc/>
    public int LastCapturedImageNumber(int otaIndex)
    {
        var numbers = _lastCapturedImageNumbers;
        return (uint)otaIndex < (uint)numbers.Length ? Volatile.Read(ref numbers[otaIndex]) : 0;
    }

    /// <summary>
    /// Shows <paramref name="image"/> in OTA <paramref name="otaIndex"/>'s slot until the next frame replaces
    /// it, and releases the frame it replaces. The caller's own ref is untouched: it releases the frame when it
    /// is done with it, whether or not the slot still shows it.
    /// </summary>
    internal void PublishCapturedImage(int otaIndex, Image image)
    {
        // The loops that tests drive on their own publish before any run has sized the slots.
        EnsureCapturedImageSlots();

        var slots = _lastCapturedImages;
        var numbers = _lastCapturedImageNumbers;
        if ((uint)otaIndex >= (uint)slots.Length || (uint)otaIndex >= (uint)numbers.Length)
        {
            return;
        }

        // The caller owns the frame here, so a refusal means it handed over one it had already released:
        // there is nothing to show, and the slot keeps what it has.
        if (!image.TryLease(out var lease))
        {
            return;
        }

        // The slot holds the lease's image, and releasing that image IS disposing the lease (the lease's
        // Dispose is its Release), so the struct itself is not kept.
        //
        // Swap, THEN number, THEN release. A reader takes the number before the frame, so it can pair a number
        // only with a frame at least that new, never a frame older than the number it stores; and a reader
        // that loses the race to the release finds the new frame on its next read.
        var replaced = Interlocked.Exchange(ref slots[otaIndex], lease.Image);
        Volatile.Write(ref numbers[otaIndex], Interlocked.Increment(ref _capturedImageSequence));
        replaced?.Release();
    }

    /// <summary>Empties OTA <paramref name="otaIndex"/>'s slot, giving its frame back to the camera.</summary>
    private void ClearCapturedImage(int otaIndex)
    {
        var slots = _lastCapturedImages;
        if ((uint)otaIndex < (uint)slots.Length)
        {
            Interlocked.Exchange(ref slots[otaIndex], null)?.Release();
        }
    }

    /// <summary>
    /// Empties every slot. The numbers stay where they are: a client holding the last one keeps its picture,
    /// and one that has none is told there is no frame.
    /// </summary>
    internal void ReleaseCapturedImages()
    {
        var slots = _lastCapturedImages;
        for (var i = 0; i < slots.Length; i++)
        {
            Interlocked.Exchange(ref slots[i], null)?.Release();
        }
    }

    /// <summary>
    /// Gives every OTA a slot. A run's entry point empties them first; a publish and the imaging loop (which
    /// tests also drive on its own) only make sure the slots exist. Any frame in a slot that is replaced goes
    /// back to its camera, and the numbers carry over, since they come from the session's sequence.
    /// </summary>
    private void EnsureCapturedImageSlots()
    {
        var count = Setup.Telescopes.Length;
        var slots = _lastCapturedImages;
        if (slots.Length == count)
        {
            return;
        }

        var resizedNumbers = new int[count];
        var numbers = _lastCapturedImageNumbers;
        Array.Copy(numbers, resizedNumbers, Math.Min(count, numbers.Length));

        var resized = new Image?[count];
        var kept = Math.Min(count, slots.Length);
        for (var i = 0; i < slots.Length; i++)
        {
            var frame = Interlocked.Exchange(ref slots[i], null);
            if (i < kept)
            {
                resized[i] = frame;
            }
            else
            {
                frame?.Release();
            }
        }

        _lastCapturedImageNumbers = resizedNumbers;
        _lastCapturedImages = resized;
    }
}
