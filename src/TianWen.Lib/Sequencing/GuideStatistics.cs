using System;
using System.Collections.Immutable;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Reduces the guide-error history to the per-frame <see cref="GuidingStats"/> that rides in a light's
/// FITS header. Pure and allocation-free over the caller's snapshot.
/// </summary>
public static class GuideStatistics
{
    /// <summary>
    /// Statistics over the guide samples whose timestamps fall inside <paramref name="exposureStart"/> ..
    /// <paramref name="exposureStart"/> + <paramref name="exposureDuration"/>, or <see langword="null"/>
    /// when no sample does.
    /// </summary>
    /// <remarks>
    /// <para><b>The window is the frame's own exposure and nothing else.</b> A rolling session RMS answers
    /// a different question ("how is the rig doing tonight") and would be actively misleading stamped on
    /// a sub, because the sub it describes may have been taken during the good hour or the bad one.</para>
    ///
    /// <para><b>Settling and dither samples inside the window are INCLUDED.</b> They are excluded from a
    /// live guiding-performance display for good reason -- a dither is a commanded move, not an error --
    /// but that reasoning inverts here. If the guider had not settled while the shutter was open then
    /// this sub is smeared, and the header exists to say so. Filtering them out would make the worst
    /// subs of the night report the cleanest numbers.</para>
    ///
    /// <para><b>Null is a real answer and is not the same as zero.</b> An unguided rig, a sub shorter than
    /// one guide exposure, and a ring buffer that has already discarded the window all produce no samples;
    /// stamping <c>GUIDERMS = 0</c> for any of them would claim perfect guiding. Absence of the card means
    /// "not known", which is what <see cref="ImageMeta.Guiding"/> being null encodes.</para>
    ///
    /// <para>Sample count is returned so a consumer can tell a two-sample RMS from a hundred-sample one;
    /// see <see cref="GuidingStats.SampleCount"/>.</para>
    /// </remarks>
    public static GuidingStats? OverExposure(
        ImmutableArray<GuideErrorSample> samples,
        DateTimeOffset exposureStart,
        TimeSpan exposureDuration)
    {
        if (samples.IsDefaultOrEmpty)
        {
            return null;
        }

        var exposureEnd = exposureStart + exposureDuration;
        var sumSqRa = 0.0;
        var sumSqDec = 0.0;
        var peakSq = 0.0;
        var n = 0;

        foreach (var sample in samples)
        {
            if (sample.Timestamp < exposureStart || sample.Timestamp > exposureEnd)
            {
                continue;
            }

            // A non-finite error is a guider that failed to measure, not an error of zero; counting it
            // would poison the whole frame's RMS with a NaN that every downstream comparison then reads
            // as false.
            if (!double.IsFinite(sample.RaError) || !double.IsFinite(sample.DecError))
            {
                continue;
            }

            var raSq = sample.RaError * sample.RaError;
            var decSq = sample.DecError * sample.DecError;
            sumSqRa += raSq;
            sumSqDec += decSq;

            var radialSq = raSq + decSq;
            if (radialSq > peakSq)
            {
                peakSq = radialSq;
            }

            n++;
        }

        if (n == 0)
        {
            return null;
        }

        var rmsRa = Math.Sqrt(sumSqRa / n);
        var rmsDec = Math.Sqrt(sumSqDec / n);

        return new GuidingStats(
            RmsTotal: (float)Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec),
            RmsRa: (float)rmsRa,
            RmsDec: (float)rmsDec,
            Peak: (float)Math.Sqrt(peakSq),
            SampleCount: n);
    }
}
