using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// The running sums behind one light's <see cref="GuidingStats"/>, folded one guide correction at a time.
/// </summary>
/// <remarks>
/// <para>Why it exists: the telemetry ring of guide samples is sized for the graph, so a light's cards
/// computed over the ring cover only its last few hundred corrections, however long the exposure was
/// (#2100). A tally is fed every correction as it lands and keeps four numbers, so the cards no longer
/// depend on the ring's length.</para>
/// <para>Immutable: <see cref="Add"/> returns a new tally, so a reader on another thread can never see a
/// torn sum, and a writer needs no lock (publish the successor by a reference write or a CAS).</para>
/// <para>The rules are <see cref="GuideStatistics.OverExposure"/>'s: the window is
/// [start, start + duration] inclusive at both ends, settling and dither samples are INCLUDED, a
/// non-finite sample is skipped, and no sample at all is <see langword="null"/>, never zero. A sample
/// whose timestamp is not after the last one included is ignored, so a correction delivered twice
/// is counted once.</para>
/// </remarks>
public sealed record ExposureGuideTally
{
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;
    private readonly double _sumSqRa;
    private readonly double _sumSqDec;
    private readonly double _peakSq;
    private readonly int _count;
    private readonly DateTimeOffset? _last;

    private ExposureGuideTally(DateTimeOffset start, DateTimeOffset end, double sumSqRa, double sumSqDec,
        double peakSq, int count, DateTimeOffset? last)
    {
        _start = start;
        _end = end;
        _sumSqRa = sumSqRa;
        _sumSqDec = sumSqDec;
        _peakSq = peakSq;
        _count = count;
        _last = last;
    }

    /// <summary>An empty tally over the exposure [<paramref name="start"/>, <paramref name="start"/> + <paramref name="duration"/>].</summary>
    public ExposureGuideTally(DateTimeOffset start, TimeSpan duration)
        : this(start, start + duration, 0.0, 0.0, 0.0, 0, null)
    {
    }

    /// <summary>Guide corrections counted so far.</summary>
    public int Count => _count;

    /// <summary>Timestamp of the last sample included, or <see langword="null"/> before the first.</summary>
    public DateTimeOffset? LastIncluded => _last;

    /// <summary>The tally with <paramref name="sample"/> folded in, or this one when the sample does not count.</summary>
    public ExposureGuideTally Add(in GuideErrorSample sample)
    {
        if (sample.Timestamp < _start || sample.Timestamp > _end)
        {
            return this;
        }

        // A non-finite error is a guider that failed to measure, not an error of zero.
        if (!double.IsFinite(sample.RaError) || !double.IsFinite(sample.DecError))
        {
            return this;
        }

        if (_last is { } last && sample.Timestamp <= last)
        {
            return this;
        }

        var raSq = sample.RaError * sample.RaError;
        var decSq = sample.DecError * sample.DecError;

        return new ExposureGuideTally(_start, _end, _sumSqRa + raSq, _sumSqDec + decSq,
            Math.Max(_peakSq, raSq + decSq), _count + 1, sample.Timestamp);
    }

    /// <summary>The statistics so far, or <see langword="null"/> when no sample was counted (not zero).</summary>
    public GuidingStats? ToStats()
    {
        if (_count == 0)
        {
            return null;
        }

        var rmsRa = Math.Sqrt(_sumSqRa / _count);
        var rmsDec = Math.Sqrt(_sumSqDec / _count);

        return new GuidingStats(
            RmsTotal: (float)Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec),
            RmsRa: (float)rmsRa,
            RmsDec: (float)rmsDec,
            Peak: (float)Math.Sqrt(_peakSq),
            SampleCount: _count);
    }
}
