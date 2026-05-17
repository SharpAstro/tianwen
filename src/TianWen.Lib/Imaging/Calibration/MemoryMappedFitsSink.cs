using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// Disk-backed <see cref="IIntegrationSink"/>: stores the master canvas in
/// a memory-mapped scratch file rather than the managed GC heap. The
/// integrator's per-row writes go straight through the mmap, so the
/// resident set never grows beyond the OS page cache's working portion.
/// Sized for the Phase 10 use case where the master canvas alone would
/// pressure or exceed available RAM (8K+ mosaic outputs, 244-frame
/// stacks on tight hosts).
/// </summary>
/// <remarks>
/// <para><b>Byte order.</b> Floats are written and read in host byte
/// order. The file is therefore NOT a valid FITS file on disk; it's a
/// scratch canvas. <see cref="FinaliseAsImage"/> copies into a managed
/// <c>float[][,]</c> and returns a regular <see cref="Image"/> so
/// downstream consumers (plate solve, white balance, FITS write) keep
/// working unchanged. Phase 10 step 3 will add a
/// <c>FinaliseToFitsFile(masterPath, wcs, ...)</c> path that re-reads
/// the mmap in chunks, byte-swaps to BE, and emits a real FITS without
/// the managed-array hop.</para>
/// <para><b>File lifecycle.</b> The sink owns the scratch file: it
/// creates + sizes it in the constructor and deletes it in Dispose. The
/// caller chooses the directory (usually a per-group staging dir) so
/// strategies that already manage their own staging cleanup can place
/// the canvas alongside the per-frame staged files.</para>
/// <para><b>Concurrency.</b> Same contract as <see cref="ArraySink"/>:
/// <see cref="GetRow"/> is called from the integrator's
/// Parallel.For-over-rows body, no two workers ever hold spans into the
/// same row. The mmap pointer is acquired once in the constructor and
/// held until Dispose, so per-call cost is pointer arithmetic only.</para>
/// </remarks>
internal sealed class MemoryMappedFitsSink : IIntegrationSink
{
    private readonly string _scratchPath;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _channelStride;
    private readonly long _rowStride;
    private readonly long _totalBytes;
    private unsafe readonly byte* _basePtr;
    private bool _disposed;

    public unsafe MemoryMappedFitsSink(string scratchPath, int channelCount, int width, int height)
    {
        if (string.IsNullOrEmpty(scratchPath)) throw new ArgumentException("Scratch path required.", nameof(scratchPath));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Shape = (channelCount, width, height);
        _rowStride = (long)width * sizeof(float);
        _channelStride = _rowStride * height;
        _totalBytes = _channelStride * channelCount;
        _scratchPath = scratchPath;

        // Pre-size the file. MemoryMappedFile.CreateFromFile with a non-zero
        // capacity on a freshly-created empty file would also extend it,
        // but doing it via FileStream makes the intent obvious and keeps
        // failures (full disk, no permission) on a normal exception path.
        using (var fs = new FileStream(scratchPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(_totalBytes);
        }

        _mmf = MemoryMappedFile.CreateFromFile(
            scratchPath,
            FileMode.Open,
            mapName: null,
            _totalBytes,
            MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, _totalBytes, MemoryMappedFileAccess.ReadWrite);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _basePtr = ptr;
    }

    public (int ChannelCount, int Width, int Height) Shape { get; }

    public unsafe Span<float> GetRow(int channel, int row)
    {
        if ((uint)channel >= (uint)Shape.ChannelCount) throw new ArgumentOutOfRangeException(nameof(channel));
        if ((uint)row >= (uint)Shape.Height) throw new ArgumentOutOfRangeException(nameof(row));
        var offset = channel * _channelStride + row * _rowStride;
        return new Span<float>(_basePtr + offset, Shape.Width);
    }

    public Image FinaliseAsImage(BitDepth bitDepth, float maxValue, float minValue, float pedestal, ImageMeta meta)
    {
        // Copy mmap content into managed float[][,] and wrap in Image so the
        // downstream pipeline (plate solve, bg neutralisation, FITS writer)
        // sees an ordinary Image. The momentary RAM peak here equals what
        // ArraySink would have allocated upfront -- the win is that during
        // integration itself the canvas lives in the file's page cache, not
        // the GC heap. Step 3 will add a direct-to-FITS finalise path that
        // skips this hop entirely for the mmap-end-to-end use case.
        var data = Image.CreateChannelData(Shape.ChannelCount, Shape.Height, Shape.Width);
        for (var ch = 0; ch < Shape.ChannelCount; ch++)
        {
            for (var row = 0; row < Shape.Height; row++)
            {
                var src = GetRow(ch, row);
                var dst = MemoryMarshal.CreateSpan(ref data[ch][row, 0], Shape.Width);
                src.CopyTo(dst);
            }
        }
        return new Image(data, bitDepth, maxValue, minValue, pedestal, meta);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        unsafe { _accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
        _accessor.Dispose();
        _mmf.Dispose();

        // Best-effort cleanup. If the file delete fails (e.g. AV scan still
        // has a handle on Windows) we leak the scratch file -- the staging
        // directory cleanup the caller does at end-of-group will get it.
        try { File.Delete(_scratchPath); }
        catch { /* best effort */ }
    }
}
