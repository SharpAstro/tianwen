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
    /// Decodes <paramref name="source"/> and returns the calibrated frame.
    ///
    /// <para>The raw read is pooled only when calibration will produce a SEPARATE image, because
    /// only then does this method own the raw buffer and get to hand it back.
    /// <see cref="Calibrator.Apply"/> opens with <c>var result = light</c> and every master is
    /// optional, so with no bias, dark or flat it returns the very instance it was given; pooling
    /// there would pass a recycling buffer to a caller that never releases it (no strategy calls
    /// <see cref="Image.Release"/>), and the array would be handed out again while the frame is
    /// still live. When a master IS present, <see cref="Image.Subtract"/> / <see cref="Image.Divide"/>
    /// allocate their own destinations, so the raw planes are dead the moment Apply returns and go
    /// straight back to the pool.</para>
    ///
    /// <para>This is the workload <see cref="Array2DPool{T}"/> is good at: every frame in a run
    /// shares one shape, so the bucket hits every time and the pool's byte ceiling is never
    /// approached.</para>
    /// </summary>
    public static Image DecodeCalibrate(RawLightSource source, Calibrator calibrator, string strategyName)
    {
        var willCopy = calibrator.Bias is not null || calibrator.Dark is not null || calibrator.Flat is not null;
        if (!Image.TryReadFitsFile(source.Path, out var raw, out _, pooled: willCopy))
        {
            throw new InvalidDataException($"{strategyName}: failed to read raw FITS at {source.Path}");
        }

        var calibrated = calibrator.Apply(raw);
        if (!ReferenceEquals(calibrated, raw))
        {
            raw.Release();
        }
        return calibrated;
    }
}
