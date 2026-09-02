using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Calibration;

/// <summary>
/// <see cref="IFrameSource"/> that enumerates FITS frames in a folder. Supports
/// <c>.fits</c>, <c>.fit</c>, and gzip-compressed <c>.fits.gz</c> / <c>.fit.gz</c>
/// extensions. Order is case-insensitive lexicographic by path so enumerations
/// are deterministic across runs (matters for reference-frame selection in
/// Phase 5).
/// </summary>
/// <remarks>
/// <para>Header-only reads via <see cref="Image.TryReadFitsHeader"/>; the FITS.Lib
/// 4.5.1 <c>Fits.ReadHDUHeaderOnly</c> call skips the data block, so a 100-frame
/// folder scan stays kilobyte-scale rather than allocating 3.6 GB of throwaway
/// pixel buffers.</para>
///
/// <para><b>Sidecar declarations are applied here, at the source, and that layering is
/// load-bearing.</b> A <see cref="FrameMetaSidecar"/> supplies a filter for frames whose capture
/// software never wrote one (a hand-fitted filter: see its remarks). Applying it in one consumer
/// instead would be actively harmful rather than merely incomplete, because
/// <c>CalibrationResolver.BestFlat</c> scores a filter mismatch at +1000: give the lights a filter
/// while their flats keep none and every flat in the archive becomes a mismatch, which is a worse
/// outcome than leaving both blank. Lights and their calibration frames have to learn it together,
/// so it belongs on the path they share.</para>
/// </remarks>
public sealed class FitsFolderFrameSource : IFrameSource
{
    /// <summary>Extensions recognized as FITS. Matched case-insensitively.</summary>
    public static readonly string[] FitsExtensions = [".fits", ".fit", ".fits.gz", ".fit.gz", ".fz"];

    private readonly string _folder;
    private readonly bool _recursive;
    private readonly FrameMetaSidecarResolver? _sidecars;
    private int _filterFilled;
    private int _filterAlreadyPresent;

    /// <param name="folder">Folder to scan. Must exist.</param>
    /// <param name="recursive">If true, descend into subdirectories. Default false.</param>
    /// <param name="useSidecars">Apply <see cref="FrameMetaSidecar"/> declarations found under
    /// <paramref name="folder"/>. On by default, and deliberately so: a manually fitted filter is
    /// invisible to every consumer of this source, not just one, and a declaration that reached the
    /// lights but not their flats would be worse than none at all (see the class remarks).</param>
    /// <exception cref="ArgumentNullException"><paramref name="folder"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist on disk.</exception>
    public FitsFolderFrameSource(string folder, bool recursive = false, bool useSidecars = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        }
        _folder = folder;
        _recursive = recursive;
        _sidecars = useSidecars ? new FrameMetaSidecarResolver(folder) : null;
    }

    /// <summary>What the sidecars contributed to the most recent enumeration. Meaningful only after
    /// enumerating; a caller reports it so a declaration is never silently inert.</summary>
    public FrameMetaSidecarStats SidecarStats => new(
        _sidecars?.FilesLoaded ?? 0, _sidecars?.FilesMalformed ?? 0, _filterFilled, _filterAlreadyPresent);

    /// <inheritdoc/>
    public async IAsyncEnumerable<FrameInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // FileEnumeration is lazy, so the directory scan streams. It also refuses to enter reparse
        // points (the organized archive's junction farm used to be scanned once per link) and skips
        // a folder it cannot read instead of aborting a multi-hour walk; see its remarks.
        var paths = FileEnumeration.EnumerateFiles(_folder, FitsExtensions, _recursive)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await Task.Run(() => TryReadFrameInfo(path), cancellationToken);
            if (info is not null)
            {
                yield return ApplySidecar(info);
            }
        }
    }

    /// <summary>
    /// Fills in a declared filter when, and only when, the frame recorded none of its own.
    ///
    /// <para><b>Fill-only, never override.</b> The problem this solves is an absent <c>FILTER</c>
    /// card, so filling is sufficient, and it makes the mechanism incapable of relabelling a frame
    /// that told the truth about itself. Correcting a header that is present but wrong is a
    /// different and much rarer job, and it wants a deliberate rewrite of the file rather than a
    /// declaration that silently disagrees with what is in the frame.</para>
    /// </summary>
    private FrameInfo ApplySidecar(FrameInfo frame)
    {
        if (_sidecars?.ResolveForFrame(frame.Path) is not { Filter: { Length: > 0 } declared })
        {
            return frame;
        }
        if (frame.Meta.Filter.IdentityKey.Length > 0)
        {
            _filterAlreadyPresent++;
            return frame;
        }
        _filterFilled++;
        // Canonicalised exactly as Image.Fits.cs canonicalises a real FILTER card, so a declared
        // "Ha" and a recorded "Ha" are indistinguishable downstream.
        var filter = Filter.FromName(declared) with { RawName = declared };
        return frame with { Meta = frame.Meta with { Filter = filter } };
    }

    private static FrameInfo? TryReadFrameInfo(string path)
    {
        return Image.TryReadFitsHeader(path, out var info) ? info : null;
    }
}
