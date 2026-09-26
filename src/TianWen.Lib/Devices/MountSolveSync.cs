using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Devices;

/// <summary>
/// Solve and sync: capture a frame, plate-solve it, and sync the mount to where it actually points. One copy for the
/// GUI's sky map and the node's job (P5 part 3 of docs/plans/hardware-in-the-server.md, #934), as the goto is one
/// (<see cref="MountGoto"/>).
/// </summary>
public static class MountSolveSync
{
    /// <summary>Outcome category of <see cref="SolveAndSyncAsync"/>.</summary>
    public enum SolveSyncResult { Synced, NotPossible, CaptureFailed, SolveFailed, SyncFailed }

    /// <summary>
    /// Result of <see cref="SolveAndSyncAsync"/>. <paramref name="CapturedImage"/> is non-null
    /// once a frame was captured, regardless of the solve/sync outcome - ownership transfers
    /// to the caller, who must either retain it (e.g. stash into the preview slot, releasing
    /// the previous occupant) or <see cref="Image.Release"/> it; otherwise the camera buffer
    /// stays pinned. <paramref name="SolveResult"/> is non-null once a solve was attempted
    /// (so the UI can mirror it into <c>LiveSessionState.PreviewPlateSolveResult</c>).
    /// </summary>
    public sealed record SolveSyncOutcome(
        SolveSyncResult Result,
        string StatusMessage,
        Image? CapturedImage,
        PlateSolveResult? SolveResult);

    /// <summary>
    /// Capture a frame, plate-solve it, and sync the mount to the solved position - the
    /// "where am I ACTUALLY pointing" primitive for the sky map. The mount's marker then
    /// jumps to reality on the next telemetry poll and the user decides whether to re-slew
    /// (deliberately NOT automated here). Flow:
    /// <list type="number">
    ///   <item>Gates: mount connected + <see cref="IMountDriver.CanSync"/>; camera connected.
    ///   Note <see cref="IMountDriver.CanSlew"/> is NOT required - for slew-less trackers
    ///   (iOptron SkyGuider Pro) this is the only way the reported position can ever be
    ///   truthful: aim by hand, solve &amp; sync, check the marker, repeat.</item>
    ///   <item>Believed pointing via the profile-derived transform (same
    ///   <see cref="TransformFactory.FromProfile"/> route as <see cref="SlewToJ2000Async"/>,
    ///   no flaky mount-side UTC reads) - the solver search origin and the baseline the
    ///   revealed pointing error is measured from.</item>
    ///   <item><see cref="CameraExposureActions.StampDenormAsync"/> +
    ///   <see cref="PreviewCapture.CaptureAsync"/> - the same capture path
    ///   as the live-session preview, so FITS headers and fake-camera catalog rendering
    ///   behave identically.</item>
    ///   <item><see cref="IPlateSolver.SolveImageAsync"/> with the believed origin.</item>
    ///   <item>Solved J2000 → mount native via
    ///   <see cref="IMountDriver.TryTransformJ2000ToMountNativeAsync"/>, then
    ///   <see cref="IMountDriver.SyncRaDecAsync"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="mount">A connected mount driver supporting sync.</param>
    /// <param name="camera">A connected camera to capture the solve frame with.</param>
    /// <param name="otaName">OTA display name for FITS denorm stamping.</param>
    /// <param name="focalLengthMm">OTA focal length for denorm + solver scale.</param>
    /// <param name="apertureMm">OTA aperture for denorm (null = unknown).</param>
    /// <param name="focuser">Optional focuser for denorm stamping.</param>
    /// <param name="filterWheel">Optional filter wheel for denorm stamping.</param>
    /// <param name="catalogDb">Catalog DB for denorm target resolution (null = skip).</param>
    /// <param name="solverFactory">Plate solver selection facade.</param>
    /// <param name="profile">Active profile (for site coordinates).</param>
    /// <param name="timeProvider">System time source (for the transform's UTC).</param>
    /// <param name="exposure">Solve-frame exposure duration.</param>
    /// <param name="gain">Optional camera gain override (null = camera default).</param>
    /// <param name="binning">Binning factor (1 = unbinned).</param>
    /// <param name="logger">Optional logger for swallowed faults.</param>
    /// <param name="cancellationToken">Cancellation token. On cancellation the captured
    /// image (if any) is released before the exception propagates.</param>
    public static async Task<SolveSyncOutcome> SolveAndSyncAsync(
        IMountDriver mount,
        ICameraDriver camera,
        string otaName,
        int focalLengthMm,
        int? apertureMm,
        IFocuserDriver? focuser,
        IFilterWheelDriver? filterWheel,
        ICelestialObjectDB? catalogDb,
        IPlateSolverFactory solverFactory,
        Profile profile,
        ITimeProvider timeProvider,
        TimeSpan exposure,
        short? gain = null,
        int binning = 1,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (!mount.Connected)
        {
            return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Mount is not connected", null, null);
        }
        if (!mount.CanSync)
        {
            return new SolveSyncOutcome(SolveSyncResult.NotPossible, $"{mount.Name} does not support sync", null, null);
        }
        if (!camera.Connected)
        {
            return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Camera is not connected", null, null);
        }

        var transform = TransformFactory.FromProfile(profile, timeProvider, out var transformError);
        if (transform is null)
        {
            logger?.LogWarning("Solve & sync: profile-derived transform unavailable: {Reason}", transformError);
            return new SolveSyncOutcome(SolveSyncResult.NotPossible,
                $"Cannot solve & sync: {transformError ?? "profile site coordinates unavailable"}", null, null);
        }

        // Believed pointing: the solver's search origin AND the baseline the revealed
        // pointing error is measured from. The built-in CatalogPlateSolver is not a
        // blind solver, so a missing origin is a hard stop, not a degraded mode.
        if (await mount.GetRaDecJ2000Async(transform, updateTime: false, cancellationToken) is not { } believed)
        {
            return new SolveSyncOutcome(SolveSyncResult.NotPossible, "Mount pointing unavailable", null, null);
        }

        // Same stamp + capture path as the live-session preview (single source of truth):
        // headers match, and the fake camera renders its synthetic catalog field.
        await CameraExposureActions.StampDenormAsync(
            camera, otaName, focalLengthMm, apertureMm, focuser, filterWheel, mount,
            targetName: "SolveSync", catalogDb: catalogDb, logger: logger, ct: cancellationToken);

        var image = await PreviewCapture.CaptureAsync(
            camera, exposure, gain, binning, timeProvider, cancellationToken);
        if (image is null)
        {
            return new SolveSyncOutcome(SolveSyncResult.CaptureFailed, "Capture produced no frame", null, null);
        }

        PlateSolveResult solveResult;
        try
        {
            solveResult = await solverFactory.SolveImageAsync(
                image, searchOrigin: new WCS(believed.RaJ2000, believed.DecJ2000), cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException oce)
        {
            logger?.LogInformation(oce, "Solve & sync cancelled during plate solve");
            image.Release();
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Solve & sync: plate solve threw");
            return new SolveSyncOutcome(SolveSyncResult.SolveFailed, $"Plate solve error: {StatusText.FromException(ex)}", image, null);
        }

        if (solveResult.Solution is not { } wcs)
        {
            return new SolveSyncOutcome(SolveSyncResult.SolveFailed,
                "Plate solve failed: no match", image, solveResult);
        }

        // The revealed pointing error (cone/polar/alignment) in arcminutes.
        var dRaHours = wcs.CenterRA - believed.RaJ2000;
        if (dRaHours > 12.0) dRaHours -= 24.0;
        else if (dRaHours < -12.0) dRaHours += 24.0;
        var cosDec = Math.Cos(believed.DecJ2000 * Math.PI / 180.0);
        var raErrArcmin = dRaHours * 15.0 * 60.0 * cosDec;
        var decErrArcmin = (wcs.CenterDec - believed.DecJ2000) * 60.0;
        var offsetArcmin = Math.Sqrt(raErrArcmin * raErrArcmin + decErrArcmin * decErrArcmin);

        // Solved J2000 -> mount native, same profile-transform route as the slew path.
        if (await mount.TryTransformJ2000ToMountNativeAsync(
                transform, wcs.CenterRA, wcs.CenterDec, updateTime: false, cancellationToken) is not { } native)
        {
            logger?.LogWarning(
                "Solve & sync: coordinate transform produced NaN. Mount={Mount} EquatorialSystem={System}",
                mount.Name, mount.EquatorialSystem);
            return new SolveSyncOutcome(SolveSyncResult.SyncFailed,
                $"Coordinate transform failed (EquatorialSystem={mount.EquatorialSystem})", image, solveResult);
        }

        try
        {
            await mount.SyncRaDecAsync(native.RaMount, native.DecMount, cancellationToken);
        }
        catch (OperationCanceledException oce)
        {
            logger?.LogInformation(oce, "Solve & sync cancelled during mount sync");
            image.Release();
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Solve & sync: SyncRaDec failed on {Mount}", mount.Name);
            return new SolveSyncOutcome(SolveSyncResult.SyncFailed, $"Mount sync failed: {ex.Message}", image, solveResult);
        }

        return new SolveSyncOutcome(SolveSyncResult.Synced,
            $"Synced: mount was {offsetArcmin:F1}' off, now at RA {wcs.CenterRA:F3}h Dec {wcs.CenterDec:F2}\u00B0",
            image, solveResult);
    }
}
