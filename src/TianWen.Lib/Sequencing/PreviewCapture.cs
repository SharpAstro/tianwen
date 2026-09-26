using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// A preview frame outside a session: taken, plate-solved and saved as a snapshot. One copy for every host, the GUI's
/// Live Session preview and the node's preview job alike (P5 part 2 of docs/plans/hardware-in-the-server.md, #934), so the
/// frame the node takes for a client is the frame the GUI takes for itself, headers and all.
/// </summary>
public static class PreviewCapture
{
    /// <summary>
    /// Resolves the connected optional drivers around an OTA's camera for per-capture FITS denorm stamping: the OTA's
    /// focuser and filter wheel plus the profile mount (each null when unassigned or not connected).
    /// </summary>
    public static (IFocuserDriver? Focuser, IFilterWheelDriver? FilterWheel, IMountDriver? Mount) ResolveOtaCaptureDevices(
        IDeviceHub hub, ProfileData data, int otaIndex)
    {
        var ota = data.OTAs[otaIndex];
        IFocuserDriver? focuser = null;
        if (ota.Focuser is { } focUri && hub.TryGetConnectedDriver<IFocuserDriver>(focUri, out var focDrv))
        {
            focuser = focDrv;
        }
        IFilterWheelDriver? filterWheel = null;
        if (ota.FilterWheel is { } fwUri && hub.TryGetConnectedDriver<IFilterWheelDriver>(fwUri, out var fwDrv))
        {
            filterWheel = fwDrv;
        }
        IMountDriver? mount = null;
        if (data.Mount is { } mountUri && hub.TryGetConnectedDriver<IMountDriver>(mountUri, out var mountDrv))
        {
            mount = mountDrv;
        }
        return (focuser, filterWheel, mount);
    }

    /// <summary>
    /// Captures a single preview frame: applies optional gain / binning, starts the exposure, polls until ready, and
    /// returns the image. Caller owns the returned image's lifetime (must call <see cref="Image.Release"/> when done).
    /// </summary>
    public static async Task<Image?> CaptureAsync(
        ICameraDriver camera,
        TimeSpan exposure,
        short? gain,
        int binning,
        ITimeProvider timeProvider,
        CancellationToken ct)
    {
        // Apply gain if user explicitly set one (null = keep camera default).
        // Both numeric (ZWO/ASCOM) and mode (DSLR ISO) cameras expose SetGainAsync.
        if (gain.HasValue && (camera.UsesGainValue || camera.UsesGainMode))
        {
            try { await camera.SetGainAsync(gain.Value, ct); } catch { }
        }
        if (binning > 1)
        {
            try { camera.BinX = binning; } catch { }
        }

        await camera.StartExposureAsync(exposure, FrameType.Light, ct);

        while (!await camera.GetImageReadyAsync(ct))
        {
            await timeProvider.SleepAsync(TimeSpan.FromMilliseconds(200), ct);
        }

        return await camera.GetImageAsync(ct);
    }

    /// <summary>
    /// Plate-solves a captured preview frame. Feeds the frame's stamped pointing (<see cref="ImageMeta.TargetRA"/> /
    /// <c>TargetDec</c>, written by <c>CameraExposureActions.StampDenormAsync</c> before each exposure) in as a search
    /// origin: the built-in <c>CatalogPlateSolver</c> isn't a blind solver and ASTAP converges dramatically faster with a
    /// hint. Returns the solve result plus a user-facing message and a solved flag.
    /// </summary>
    public static async Task<(PlateSolveResult Result, string Message, bool Solved)> SolveAsync(
        IPlateSolverFactory solver, Image image, CancellationToken ct)
    {
        WCS? searchOrigin = !double.IsNaN(image.ImageMeta.TargetRA)
                            && !double.IsNaN(image.ImageMeta.TargetDec)
            ? new WCS(image.ImageMeta.TargetRA, image.ImageMeta.TargetDec)
            : null;
        var result = await solver.SolveImageAsync(image, searchOrigin: searchOrigin, cancellationToken: ct);
        if (result.Solution is { } wcs)
        {
            return (result, $"Solved: RA {wcs.CenterRA:F3}h Dec {wcs.CenterDec:F2}\u00B0", true);
        }
        return (result, "Plate solve failed: no match", false);
    }

    /// <summary>
    /// Writes a preview image to a per-date Snapshot folder under the configured image output root, and returns the file's
    /// full path. The caller holds the image for the write: a frame it does not own it LEASES first.
    /// </summary>
    public static async Task<string> SaveSnapshotAsync(
        Image image, int otaIndex, IExternal external, ITimeProvider timeProvider)
    {
        var utcNow = timeProvider.GetUtcNow();
        var dateFolderUtc = utcNow.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);
        var snapshotFolder = Path.Combine(
            external.ImageOutputFolder.FullName,
            "Snapshot",
            dateFolderUtc);
        Directory.CreateDirectory(snapshotFolder);

        var fileName = external.GetSafeFileName(
            $"snapshot_{utcNow:yyyy-MM-ddTHH_mm_ss}_OTA{otaIndex + 1}.fits");
        var filePath = Path.Combine(snapshotFolder, fileName);

        await external.WriteFitsFileAsync(image, filePath);
        return filePath;
    }
}
