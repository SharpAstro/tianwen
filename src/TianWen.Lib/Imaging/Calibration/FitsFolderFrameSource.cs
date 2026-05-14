using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// <see cref="IFrameSource"/> that enumerates FITS frames in a folder. Supports
/// <c>.fits</c>, <c>.fit</c>, and gzip-compressed <c>.fits.gz</c> / <c>.fit.gz</c>
/// extensions. Order is case-insensitive lexicographic by path so enumerations
/// are deterministic across runs (matters for reference-frame selection in
/// Phase 5).
/// </summary>
/// <remarks>
/// Header-only reads via <see cref="Image.TryReadFitsHeader"/> — the FITS.Lib
/// 4.5.1 <c>Fits.ReadHDUHeaderOnly</c> call skips the data block, so a 100-frame
/// folder scan stays kilobyte-scale rather than allocating 3.6 GB of throwaway
/// pixel buffers.
/// </remarks>
public sealed class FitsFolderFrameSource : IFrameSource
{
    /// <summary>Extensions recognized as FITS. Matched case-insensitively.</summary>
    public static readonly string[] FitsExtensions = [".fits", ".fit", ".fits.gz", ".fit.gz"];

    private readonly string _folder;
    private readonly bool _recursive;

    /// <param name="folder">Folder to scan. Must exist.</param>
    /// <param name="recursive">If true, descend into subdirectories. Default false.</param>
    /// <exception cref="ArgumentNullException"><paramref name="folder"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist on disk.</exception>
    public FitsFolderFrameSource(string folder, bool recursive = false)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        }
        _folder = folder;
        _recursive = recursive;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FrameInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchOption = _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        // EnumerateFiles itself is lazy, so the directory scan also streams.
        var paths = Directory.EnumerateFiles(_folder, "*.*", searchOption)
            .Where(IsFitsPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await Task.Run(() => TryReadFrameInfo(path), cancellationToken);
            if (info is not null)
            {
                yield return info;
            }
        }
    }

    private static bool IsFitsPath(string path)
    {
        foreach (var ext in FitsExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static FrameInfo? TryReadFrameInfo(string path)
    {
        return Image.TryReadFitsHeader(path, out var info) ? info : null;
    }
}
