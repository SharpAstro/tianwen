namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Borrows this frame: on success <paramref name="lease"/> wraps a DISTINCT image that shares
    /// these pixel planes and holds a reference on every recycled channel buffer, so the camera
    /// cannot reuse the pixels mid-read. Disposing the lease is the borrower's whole obligation --
    /// there is no <c>Release</c> to pair by hand and no way to spend the owner's ref by mistake.
    /// <para>
    /// <b>Why this exists rather than just reading <c>LastGuideFrame</c> (or any other live frame)
    /// directly.</b> A published frame is its owner's, and the owner releases it from ITS thread
    /// whenever it is done, so a reader holding a bare reference is reading a buffer the camera may
    /// already have taken back. That is not a matter of awaits: a render-thread reader drawing in the
    /// same frame it read the reference is exposed just the same, and the GUI's live previews read
    /// that way until one threw on the render thread in the live check of 2026-09-25. Every reader
    /// that is not the owner leases, the render thread included.
    /// </para>
    /// <para>
    /// Losing the race is normal and is reported as <see langword="false"/>, not an exception. Every
    /// publisher swaps its pointer BEFORE releasing the frame it replaces, a guider's
    /// <c>LastGuideFrame</c> and a session's <c>LastCapturedImages</c> slot alike (the slot holds a
    /// lease of its own, so a frame stays readable until the next one replaces it, whatever the
    /// session does with its own hold). So a refusal means the frame was superseded between the
    /// caller's read of the pointer and the lease, and re-reading the pointer converges in one step.
    /// </para>
    /// <para>
    /// <b>Frame ownership: this is the BORROW primitive.</b> Everyone who is not the owner reads
    /// through here and disposes the lease, never the original. See the frame-ownership notes on
    /// <see cref="Image"/> and the token rationale on <see cref="ImageLease"/>.
    /// </para>
    /// </summary>
    /// <param name="lease">
    /// The borrow on success; <c>default</c> (inert) on failure. The leased image shares this
    /// image's pixel arrays and metadata, so it must be treated as read-only for exactly the
    /// reasons the class-level mutability notes give.
    /// </param>
    public bool TryLease(out ImageLease lease)
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
            // synthetic fake frame), so no camera can pull them out from under the borrower. A
            // released image is still refused, because its planes may since have been reused.
            //
            // The lease is a DISTINCT image here too, not `this`. Handing out `this` made the
            // borrower's dispose mark the SOURCE released, so one borrow cycle poisoned every later
            // lease of the same published frame -- a repeat-polling preview 404s from the second poll
            // on. Distinct costs one small allocation and cannot poison anything: the harvest finds
            // no buffers, so disposing the lease is a self-owned no-op.
            if (_released)
            {
                lease = default;
                return false;
            }

            lease = new ImageLease(new Image(_planes, bitDepth, pedestal, imageMeta, samplesAreUnitReferred, sourceRaster));
            return true;
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

                lease = default;
                return false;
            }

            taken++;
        }

        // A distinct instance, so the refs just taken get their own one-shot Release when the lease
        // is disposed (Image.Release clears _channelBuffers via Interlocked.Exchange). Wrapping
        // `this` would make the borrower's dispose indistinguishable from the owner's release and
        // drop the frame early.
        //
        // The LIVE planes, not the constructor argument: residency can move after construction, and
        // seeding a lease from the original array would hand the borrower float planes this image has
        // since dropped -- resurrecting exactly the bytes D1 evicted. Passing the evicted form is
        // safe because the raster travels with it, so the borrower can rebuild if it reads.
        //
        // samplesAreUnitReferred and the raster are forwarded because a lease is a VIEW of the same
        // pixels: the scale fact is still true of them, and the raster still describes those very
        // bytes. (The rule against forwarding a raster is about transforms that RECOMPUTE pixels,
        // which is what makes the raster a lie; sharing them recomputes nothing.) Dropping the flag
        // made a leased frame claim ADU scale while carrying unit-referred samples.
        lease = new ImageLease(new Image(_planes, bitDepth, pedestal, imageMeta, samplesAreUnitReferred, sourceRaster));
        return true;
    }
}
