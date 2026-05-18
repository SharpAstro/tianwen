using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Star-quad-match registration of light frames against a reference. Thin
/// orchestration layer over the existing <see cref="Image.FindOffsetAndRotationAsync"/>
/// pipeline (quad-invariant matching + RANSAC-lite outlier removal + affine
/// least-squares fit + scale/skew validation). Persists each result as a
/// <see cref="RegistrationSidecar"/> so subsequent integrator runs reuse the
/// transform without re-detecting stars.
/// </summary>
public static class Registrator
{
    /// <summary>
    /// Picks the best frame to use as the registration reference. Scores each
    /// frame by star count (per <see cref="Image.FindStarsAsync"/>) and picks
    /// the maximum. v1 scoring is intentionally simple — count alone, not
    /// count × (1/FWHM) — because computing FWHM doubles the per-frame load
    /// cost and isn't worth the complexity until profiling shows a star-rich
    /// but defocused frame is dragging registration quality down.
    /// </summary>
    /// <param name="lights">Candidate lights. Each is loaded to detect stars.</param>
    /// <param name="snrMin">Min SNR for star detection. Lower = more stars but
    /// more spurious detections; 20 matches the existing
    /// <see cref="Image.FindOffsetAndRotationAsync"/> default.</param>
    /// <param name="onFrameScanned">Optional per-frame callback invoked after
    /// star detection finishes on a frame. Receives the frame and its detected
    /// star count. Lets test runners / CLI orchestrators log progress and
    /// inspect the picker's view of the dataset without re-detecting stars.</param>
    /// <returns>The frame with the highest star count, or null if no frame
    /// has any detectable stars.</returns>
    public static async Task<FrameInfo?> PickReferenceAsync(
        IReadOnlyList<FrameInfo> lights,
        float snrMin = 20f,
        System.Action<FrameInfo, int>? onFrameScanned = null,
        CancellationToken cancellationToken = default)
    {
        FrameInfo? best = null;
        var bestCount = -1;
        foreach (var frame in lights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = await frame.LoadFullAsync(cancellationToken);
            var stars = await image.FindStarsAsync(channel: 0, snrMin: snrMin, cancellationToken: cancellationToken);
            onFrameScanned?.Invoke(frame, stars.Count);
            if (stars.Count > bestCount)
            {
                bestCount = stars.Count;
                best = frame;
            }
        }
        return best;
    }

    /// <summary>
    /// Registers each light against <paramref name="reference"/>. Emits one
    /// <see cref="RegistrationResult"/> per input light; failed quad matches
    /// surface as <see cref="RegistrationResult.Registered"/> = false rather
    /// than throwing, so a corrupt frame doesn't abort the batch.
    /// </summary>
    /// <param name="lights">Lights to register. Order is preserved; the result
    /// at index N corresponds to <paramref name="lights"/>[N].</param>
    /// <param name="reference">Reference frame. The light with the same Path
    /// as the reference yields an identity transform without re-running the
    /// match.</param>
    /// <param name="persist">When true, writes each result via
    /// <see cref="RegistrationSidecar.WriteAsync"/> as it's computed.</param>
    /// <param name="snrMin">SNR threshold for star detection. Forwarded to
    /// <see cref="Image.FindOffsetAndRotationAsync"/>.</param>
    public static async IAsyncEnumerable<RegistrationResult> AlignAsync(
        IReadOnlyList<FrameInfo> lights,
        FrameInfo reference,
        bool persist = true,
        float snrMin = 20f,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var refImage = await reference.LoadFullAsync(cancellationToken);
        foreach (var light in lights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await AlignOneAsync(light, reference, refImage, snrMin, cancellationToken);
            if (persist)
            {
                await RegistrationSidecar.WriteAsync(result, cancellationToken);
            }
            yield return result;
        }
    }

    /// <summary>
    /// Tries to load a cached sidecar for <paramref name="light"/>; if absent
    /// or stale (light file mtime newer than the cached <c>ComputedUtc</c>),
    /// registers against <paramref name="reference"/>. Use this from the
    /// integrator to skip Phase 5 when a previous run already aligned the
    /// frame.
    /// </summary>
    public static async Task<RegistrationResult> LoadOrAlignAsync(
        FrameInfo light,
        FrameInfo reference,
        Image referenceImage,
        bool persist = true,
        float snrMin = 20f,
        CancellationToken cancellationToken = default)
    {
        var cached = await RegistrationSidecar.TryReadAsync(light.Path, cancellationToken);
        if (cached is not null && IsCacheFresh(light.Path, cached))
        {
            return cached;
        }

        var fresh = await AlignOneAsync(light, reference, referenceImage, snrMin, cancellationToken);
        if (persist)
        {
            await RegistrationSidecar.WriteAsync(fresh, cancellationToken);
        }
        return fresh;
    }

    private static async Task<RegistrationResult> AlignOneAsync(
        FrameInfo light, FrameInfo reference, Image referenceImage, float snrMin, CancellationToken cancellationToken)
    {
        // Reference against itself = identity.
        if (string.Equals(light.Path, reference.Path, System.StringComparison.OrdinalIgnoreCase))
        {
            return RegistrationResult.Identity(light.Path, registered: true);
        }

        var image = await light.LoadFullAsync(cancellationToken);
        var transform = await image.FindOffsetAndRotationAsync(
            other: referenceImage,
            channel: 0,
            otherChannel: 0,
            snrMin: snrMin,
            cancellationToken: cancellationToken);

        if (transform is null)
        {
            // Quad match failed — emit a non-registered result so the
            // integrator can decide whether to skip the frame.
            return RegistrationResult.Identity(light.Path, registered: false);
        }

        return RegistrationResult.FromTransform(light.Path, transform.Value);
    }

    private static bool IsCacheFresh(string lightPath, RegistrationResult cached)
    {
        try
        {
            var lightMtime = new System.IO.FileInfo(lightPath).LastWriteTimeUtc;
            // Cache is fresh when it's no older than the light file. Equal
            // timestamps (sub-second granularity collision) count as fresh.
            return cached.ComputedUtc.UtcDateTime >= lightMtime;
        }
        catch
        {
            return false;
        }
    }
}
