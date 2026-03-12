using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal ValueTask WriteImageToFitsFileAsync(QueuedImageWrite imageWrite)
    {
        var targetName = imageWrite.Observation.Target.Name;
        var dateFolderUtc = imageWrite.ExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);

        // TODO: make configurable, add frame type
        var meta = imageWrite.Image.ImageMeta;
        var frameFolder = External.CreateSubDirectoryInOutputFolder(
            targetName,
            dateFolderUtc,
            meta.Filter.Name,
            meta.FrameType.ToString()
        ).FullName;
        var fitsFileName = External.GetSafeFileName($"frame_{imageWrite.ExpStartTime:o}_{imageWrite.FrameNumber:000000}.fits");
        var fitsFilePath = Path.Combine(frameFolder, fitsFileName);

        External.AppLogger.LogInformation("Writing FITS file {FitsFilePath}", fitsFilePath);
        return External.WriteFitsFileAsync(imageWrite.Image, fitsFilePath);
    }



}
