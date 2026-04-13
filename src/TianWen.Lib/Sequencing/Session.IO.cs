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



}
