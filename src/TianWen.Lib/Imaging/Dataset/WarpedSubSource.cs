using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// The one way to obtain a registered sub warped onto its session's canvas, whichever way the
/// session was built: it reads the scratch FITS where the warp pass wrote one, and re-warps the sub
/// from its source where it did not.
///
/// <para><b>Why a session may have written none.</b> The warp pass costs subs x canvas bytes of
/// scratch (~117 MB per sub of a 24 MP OSC frame), and on a DRIZZLED session every byte of it was
/// written for a single consumer. <see cref="DrizzleStrategy"/> consumes
/// <see cref="IntegrationJob.RawBayerFrames"/> only, because it forward-projects the raw CFA rather
/// than a debayered frame, and both half-masters take the master's strategy; so the master, the
/// halves and the PSF record never open a warped scratch FITS, and the tiler's per-sub pass is the
/// only thing that does. That pass runs AFTER the master exists (its cells are sampled from the
/// master), which is why the subs could not simply be tiled as they were warped -- but it can warp a
/// sub again when it reaches it, which is what this does.</para>
///
/// <para><b>The two routes must agree exactly</b>, or a tile would depend on which one produced it.
/// They share <see cref="SessionRegistrar.RegisteredSub.TransformToCanvas"/>, the composed
/// source-to-canvas affine, and this class is handed the same calibrator, debayer algorithm,
/// interpolation kernel and canvas the warp pass used;
/// <see cref="FrameRegistration.WarpToCanvasAsync"/> is debayer-then-warp with that transform and
/// nothing else, so re-warping reproduces it. Pinned by
/// <c>WarpedSubSourceTests.ARewarpedSubIsTheSubTheWarpPassWouldHaveWritten</c>.</para>
///
/// <para>The caller owns what it gets back and must not hold it beyond the frame it is working on:
/// a canvas-sized float32 image is hundreds of MB, which is the whole reason the sub-major loop in
/// the tiler exists.</para>
/// </summary>
/// <param name="calibrator">The masters the warp pass calibrated with, or <c>null</c> where it
/// registered uncalibrated. A re-warp that skipped calibration would not be the same frame.</param>
/// <param name="debayerAlgorithm">The demosaic the warp pass used. Different algorithms reconstruct
/// measurably different pixels (see <c>VngFlatFieldBiasTests</c>), so this is not a free choice.</param>
/// <param name="warpInterpolation">The resampling kernel the warp pass placed subs with.</param>
/// <param name="canvasWidth">Union-canvas width, shared by every sub and the master.</param>
/// <param name="canvasHeight">Union-canvas height.</param>
public sealed class WarpedSubSource(
    Calibrator? calibrator,
    DebayerAlgorithm debayerAlgorithm,
    WarpInterpolation warpInterpolation,
    int canvasWidth,
    int canvasHeight)
{
    /// <summary>Whether <paramref name="sub"/> comes off the disk rather than being warped again.
    /// Reported by the tiler so a run's log says which kind of session it was tiling.</summary>
    public static bool IsMaterialised(SessionRegistrar.RegisteredSub sub) => sub.WarpedPath is not null;

    /// <summary>
    /// The sub, warped onto the session canvas: read back from its scratch FITS when the warp pass
    /// wrote one, otherwise loaded, calibrated, debayered and warped again.
    /// </summary>
    /// <exception cref="IOException">The scratch FITS is named but unreadable, which means the
    /// per-session scratch was wiped while a consumer was still reading it -- the failure the build
    /// runner's run lock exists to prevent.</exception>
    public async ValueTask<Image> LoadAsync(SessionRegistrar.RegisteredSub sub, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sub);

        if (sub.WarpedPath is { } path)
        {
            if (!Image.TryReadFitsFile(path, out var fromScratch))
            {
                throw new IOException($"Failed to re-read warped scratch FITS: {path}");
            }
            return fromScratch;
        }

        var raw = await sub.Source.LoadFullAsync(cancellationToken);
        // Calibrator.Apply CONSUMES the light, the one deliberate exception to naming an ownership
        // transfer (CalibratorOwnershipTests), so `raw` must not be touched after this line.
        var calibrated = calibrator?.Apply(raw) ?? raw;
        var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        return await debayered.WarpToReferenceGridAsync(
            sub.TransformToCanvas, canvasWidth, canvasHeight, warpInterpolation, cancellationToken);
    }
}
