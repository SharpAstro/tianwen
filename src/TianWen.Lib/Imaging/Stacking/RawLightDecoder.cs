using System.IO;
using TianWen.Lib.Imaging.Calibration;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Reads a raw light off disk and applies calibration, owning the raw frame's buffer for exactly
/// as long as the calibration step needs it.
///
/// <para><b>Why this is shared rather than duplicated per strategy.</b> The two tile-pipelined
/// strategies used to carry a copy each, with a comment arguing that a trivial five-line wrapper
/// around <see cref="Image.TryReadFitsFile"/> was not worth coupling them through a third file.
/// That held while the body was a read plus a call. It stopped holding once the body acquired a
/// buffer-ownership rule: two copies of a release rule drift, and the failure mode of a drifted
/// copy is a frame whose pixels are recycled while it is still being stacked -- silent corruption,
/// not an exception. One implementation, one rule.</para>
/// </summary>
internal static class RawLightDecoder
{
    /// <summary>
    /// Decodes <paramref name="source"/> and returns the calibrated frame, which the CALLER owns.
    ///
    /// <para><b>The release guard is gone, because <see cref="Calibrator.Apply"/> now consumes what
    /// it is given</b> (P1 of <c>docs/plans/frame-lifecycle.md</c>). This method hands the raw frame
    /// over and never refers to it again; whether Apply copied or returned the very same instance is
    /// no longer a question anyone downstream has to answer.</para>
    ///
    /// <para><b>The pooling decision is NOT the same question, and it survives.</b> Pooling is safe
    /// only where somebody eventually releases, and with no masters the raw frame IS the returned
    /// frame -- which the tile strategies cache for the whole run and never release. So the read is
    /// pooled only when a master will consume it, which is exactly when Apply hands the arrays back.
    /// Making this unconditional is P3, and it needs the strategies to release their cached frames
    /// first; <see cref="ChannelBufferLeakTracker"/> is the instrument that says when they do.</para>
    ///
    /// <para>This is the workload <see cref="Array2DPool{T}"/> is good at: every frame in a run
    /// shares one shape, so the bucket hits every time and the pool's byte ceiling is never
    /// approached.</para>
    /// </summary>
    public static Image DecodeCalibrate(RawLightSource source, Calibrator calibrator, string strategyName)
    {
        var willConsumeRaw = calibrator.Bias is not null || calibrator.Dark is not null || calibrator.Flat is not null;
        if (!Image.TryReadFitsFile(source.Path, out var raw, out _, pooled: willConsumeRaw))
        {
            throw new InvalidDataException($"{strategyName}: failed to read raw FITS at {source.Path}");
        }

        return calibrator.Apply(raw);
    }
}
