using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Header-only handle to a FITS frame on disk. A single instance holds path,
/// shape, bit depth, and the parsed <see cref="ImageMeta"/>; a few hundred
/// bytes of state, no pixel data. Folder enumerations carry only these
/// records around, so a 1000-frame scan stays comfortably in cache.
/// </summary>
/// <param name="Path">Absolute path to the FITS file on disk.</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="ChannelCount">Number of channel planes (1 for mono / Bayer, 3 for true RGB).</param>
/// <param name="BitDepth">Pixel bit depth as read from the FITS BITPIX header.</param>
/// <param name="Meta">Parsed <see cref="ImageMeta"/>; frame type, exposure,
/// filter, sensor type, temperature, etc. All FITS header reads needed by the
/// stacking pipeline are routed through this field.</param>
/// <param name="StackedFrameCount">Value of the FITS <c>STACK_N</c> header, or 0
/// when the keyword is absent. Non-zero marks the file as a stacking product
/// (master integrated by <c>IntegrationFitsWriter</c>); these must be filtered
/// out at scan time when they sit alongside lights, otherwise they get treated
/// as fresh frames and pollute the next run's groups.</param>
public sealed record FrameInfo(
    string Path,
    int Width,
    int Height,
    int ChannelCount,
    BitDepth BitDepth,
    ImageMeta Meta,
    int StackedFrameCount = 0)
{
    /// <summary>Convenience accessor: <c>Meta.FrameType</c>.</summary>
    public FrameType FrameType => Meta.FrameType;

    /// <summary>Convenience accessor: <c>Meta.IsMaster</c>: this frame is an already-integrated
    /// MASTER calibration frame (e.g. IMAGETYP=MASTERDARK), not a raw sub. Its <see cref="FrameType"/>
    /// still reports the underlying type (Dark / Flat / Bias).</summary>
    public bool IsMaster => Meta.IsMaster;

    /// <summary>
    /// Loads the full pixel data from disk via <see cref="Image.TryReadFitsFile"/>.
    /// Runs the synchronous FITS read on the thread pool. Caller releases the
    /// returned image as soon as the pipeline stage is done with it (especially
    /// important inside <c>foreach</c> loops over an <see cref="IFrameSource"/>
    /// where each yield iteration should hold one image at a time).
    /// </summary>
    /// <exception cref="IOException">The file disappeared between enumeration
    /// and load, or could not be parsed as FITS.</exception>
    public Task<Image> LoadFullAsync(CancellationToken cancellationToken = default)
        => LoadFullAsync(pooled: false, cancellationToken);

    /// <summary>
    /// As <see cref="LoadFullAsync(CancellationToken)"/>, but with the channel arrays optionally
    /// rented from <c>Array2DPool</c> so a bulk reader recycles them instead of handing the GC a
    /// large-object array per frame.
    /// </summary>
    /// <param name="pooled">
    /// <see langword="true"/> turns the release obligation the summary above already describes into
    /// a REAL one: the frame becomes pool-owned (frame-ownership convention 3 on
    /// <see cref="Image"/>), <see cref="Image.Release"/> stops being a no-op, and touching the frame
    /// afterwards reads pixels the pool may already have lent to someone else. Pass it only from a
    /// stage that owns the frame outright for a bounded scope and releases it there -- P3 of
    /// <c>docs/plans/frame-lifecycle.md</c> has the audit of which stages those are.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task<Image> LoadFullAsync(bool pooled, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (!Image.TryReadFitsFile(Path, out var image, out _, pooled))
            {
                throw new IOException($"Failed to read FITS file: {Path}");
            }
            return image;
        }, cancellationToken);
}
