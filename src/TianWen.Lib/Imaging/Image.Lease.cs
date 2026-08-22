using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Borrows this image for a consumer that outlives the frame's owner, taking a reference on every
    /// recycled channel buffer so the camera cannot reuse the pixels mid-read. On success the caller
    /// owns the returned image and MUST <see cref="Release"/> it; on failure the frame is already gone
    /// and the caller has nothing to release.
    /// <para>
    /// <b>Why this exists rather than just reading <c>LastGuideFrame</c> (or any other live frame)
    /// directly.</b> A frame published by a driver is valid only until that driver publishes the next
    /// one: the guide loop does <c>LastFrame?.Release(); LastFrame = frame;</c> on every exposure, so
    /// at guiding cadence a reader that holds the reference across any await is reading a buffer the
    /// camera has already taken back. The GUI gets away with a bare reference because it draws on the
    /// render thread within the same frame it read; a hosted request encoding a JPEG does not, and
    /// that difference is the whole reason for a lease.
    /// </para>
    /// <para>
    /// Losing the race is normal and is reported as <see langword="false"/>, not an exception: the
    /// honest answer for a poller is "no frame right now", and the next poll succeeds.
    /// </para>
    /// </summary>
    /// <param name="leased">
    /// The borrowed image on success. It shares this image's pixel arrays and metadata, so it must be
    /// treated as read-only for exactly the reasons the class-level mutability notes give.
    /// </param>
    public bool TryLease([NotNullWhen(true)] out Image? leased)
    {
        // Read the buffer array BEFORE consulting _released, which is what makes the two branches below
        // sound. Release() nulls the array, so a null read is ambiguous on its own (an image that never
        // had recyclable buffers looks identical to one whose buffers went back to the camera) and only
        // the flag can separate them. Reading in this order means the ambiguous case can only be
        // reached when the array was ALREADY null on entry, so the flag's answer is not stale; a
        // Release that lands after a non-null read routes to the ref-counted path, where TryAddRef is
        // authoritative regardless of interleaving.
        var buffers = _channelBuffers;

        if (buffers is null)
        {
            // Nothing to recycle: the planes are ordinary GC arrays (a file load, a debayer output, a
            // synthetic fake frame), so no camera can pull them out from under the borrower and the
            // image itself is the lease. Release() on it is a no-op, so the caller's release still
            // balances. A released image is refused, because its planes may since have been reused.
            leased = _released ? null : this;
            return !_released;
        }

        // All-or-nothing: a partially referenced multi-channel frame is not a frame, and unwinding is
        // what keeps a failed lease from leaking refs that nobody will ever release.
        var taken = 0;
        for (var c = 0; c < buffers.Length; c++)
        {
            if (buffers[c] is { } buffer && !buffer.TryAddRef())
            {
                for (var undo = 0; undo < taken; undo++)
                {
                    buffers[undo]?.Release();
                }

                leased = null;
                return false;
            }

            taken++;
        }

        // A distinct instance, so the refs just taken get their own one-shot Release (Image.Release
        // clears _channelBuffers via Interlocked.Exchange). Handing back `this` would make the
        // borrower's release indistinguishable from the owner's and drop the frame early.
        //
        // The LIVE planes, not the constructor argument: residency can move after construction, and
        // seeding a lease from the original array would hand the borrower float planes this image has
        // since dropped -- resurrecting exactly the bytes D1 released. Passing the released form is
        // safe because the raster travels with it, so the borrower can rebuild if it reads.
        //
        // samplesAreUnitReferred and the raster are forwarded because a lease is a VIEW of the same
        // pixels: the scale fact is still true of them, and the raster still describes those very
        // bytes. (The rule against forwarding a raster is about transforms that RECOMPUTE pixels,
        // which is what makes the raster a lie; sharing them recomputes nothing.) Dropping the flag
        // made a leased frame claim ADU scale while carrying unit-referred samples.
        leased = new Image(_planes, bitDepth, pedestal, imageMeta, samplesAreUnitReferred, sourceRaster);
        return true;
    }
}
