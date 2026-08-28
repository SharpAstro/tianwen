using System;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal async ValueTask<string> WriteImageToFitsFileAsync(QueuedImageWrite imageWrite)
    {
        var target = imageWrite.Observation.Target;
        var targetFolder = target.CatalogIndex is { } idx
            ? External.GetSafeFileName($"{idx.ToCanonical()}_{target.Name}")
            : External.GetSafeFileName(target.Name);
        var dateFolderUtc = imageWrite.ExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);

        var meta = imageWrite.Image.ImageMeta;
        var frameFolder = Path.Combine(
            External.ImageOutputFolder.FullName,
            targetFolder,
            dateFolderUtc,
            External.GetSafeFileName(meta.Filter.Name),
            meta.FrameType.ToString());
        Directory.CreateDirectory(frameFolder);

        var fitsFileName = External.GetSafeFileName($"frame_{imageWrite.ExpStartTime:yyyy-MM-ddTHH_mm_ss}_{imageWrite.FrameNumber:0000}.fits");
        var fitsFilePath = Path.Combine(frameFolder, fitsFileName);

        _logger.LogInformation("Writing FITS file {FitsFilePath}", fitsFilePath);
        await External.WriteFitsFileAsync(imageWrite.Image, fitsFilePath);

        var gcInfo = GC.GetGCMemoryInfo();
        _logger.LogInformation(
            "Memory after FITS write: working={WorkingMB:F0}MB, managed={ManagedMB:F0}MB, GC heap={HeapMB:F0}MB | pool: {Pooled} pooled, {Hits} hits, {Misses} misses, {Returns} returns",
            Environment.WorkingSet / (1024.0 * 1024),
            GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024),
            gcInfo.HeapSizeBytes / (1024.0 * 1024),
            Array2DPool<float>.TotalPooled,
            Array2DPool<float>.HitCount,
            Array2DPool<float>.MissCount,
            Array2DPool<float>.ReturnCount);

        _lastFramePath = fitsFilePath;
        return fitsFilePath;
    }

    /// <summary>
    /// Writes one measurement frame the session would otherwise discard, under
    /// <c>&lt;output&gt;/Intermediates/&lt;date&gt;/&lt;filter&gt;/&lt;frame type&gt;/[group/]</c>.
    /// The single write path behind <see cref="SessionConfiguration.SaveIntermediates"/>; every caller
    /// is gated on that flag.
    /// </summary>
    /// <remarks>
    /// One root for the lot, so a human can see at a glance that nothing under it is science data and
    /// can delete the whole tree without reasoning about it. For the STACKER the layout is cosmetic as
    /// always -- what keeps these frames out of an integration is the <see cref="FrameType"/> card each
    /// one carries -- but it is load-bearing for whoever consumes them, which is the point of keeping
    /// them at all.
    /// </remarks>
    /// <param name="image">The frame. Ownership stays with the caller, which releases it after its own
    /// measurement; this only reads.</param>
    /// <param name="whenUtc">Timestamp for the date folder and, when <paramref name="group"/> is null,
    /// the file name.</param>
    /// <param name="group">Optional sub-folder collecting frames that only mean something together --
    /// an auto-focus V-curve, whose rungs are one ladder. A folder rather than a filename field
    /// because our timestamp format itself contains underscores (<c>:</c> being illegal in a path), so
    /// a reader splitting a name on <c>_</c> to recover the group silently gets an hour-granularity key
    /// and merges two runs of the same evening. <see langword="null"/> for standalone frames.</param>
    /// <param name="name">File name stem, without extension.</param>
    internal async ValueTask<string> WriteIntermediateFrameToFitsFileAsync(
        Image image,
        DateTimeOffset whenUtc,
        string? group,
        string name)
    {
        var meta = image.ImageMeta;
        var dateFolderUtc = whenUtc.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
        var frameFolder = Path.Combine(
            External.ImageOutputFolder.FullName,
            "Intermediates",
            dateFolderUtc,
            External.GetSafeFileName(meta.Filter.Name),
            meta.FrameType.ToString());
        if (group is { Length: > 0 })
        {
            frameFolder = Path.Combine(frameFolder, External.GetSafeFileName(group));
        }
        Directory.CreateDirectory(frameFolder);

        var fitsFilePath = Path.Combine(frameFolder, External.GetSafeFileName($"{name}.fits"));

        _logger.LogInformation("Writing intermediate FITS file {FitsFilePath}", fitsFilePath);
        await External.WriteFitsFileAsync(image, fitsFilePath);

        // Deliberately NOT _lastFramePath, unlike the light and flat writers. That telemetry drives
        // "show me the newest frame", and most of these are out of focus or under-exposed on purpose --
        // pointing a viewer at one mid-run would look like the rig had broken.
        return fitsFilePath;
    }

    /// <summary>Group folder for one auto-focus V-curve: the rungs and the verification frame only
    /// mean anything together, and two OTAs sweeping at the same instant must not share a folder.</summary>
    internal static string AutoFocusRunGroup(int otaIndex, DateTimeOffset runStartUtc)
        => $"ota{otaIndex + 1}_{runStartUtc:yyyy-MM-ddTHH_mm_ss}";
}
