using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Tracks guide errors over time, maintaining both all-time and rolling-window statistics.
/// Provides RA/Dec error metrics for guide quality assessment and drift detection.
/// </summary>
internal sealed class GuideErrorTracker
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromSeconds(100);
    private static readonly TimeSpan LongWindow = TimeSpan.FromSeconds(200);

    private readonly Accum _raAll = new Accum();
    private readonly Accum _decAll = new Accum();
    private readonly RollingAccum _raShort = new RollingAccum(ShortWindow);
    private readonly RollingAccum _raLong = new RollingAccum(LongWindow);
    private readonly RollingAccum _decShort = new RollingAccum(ShortWindow);
    private readonly RollingAccum _decLong = new RollingAccum(LongWindow);

    private double _startTimestamp = double.NaN;
    private double _lastTimestamp = double.NaN;

    /// <summary>
    /// Records a guide error sample.
    /// </summary>
    /// <param name="timestampSeconds">Monotonic timestamp in seconds.</param>
    /// <param name="raErrorPixels">RA error in pixels (from calibration transform).</param>
    /// <param name="decErrorPixels">Dec error in pixels (from calibration transform).</param>
    public void Add(double timestampSeconds, double raErrorPixels, double decErrorPixels)
    {
        if (double.IsNaN(_startTimestamp))
        {
            _startTimestamp = timestampSeconds;
        }

        _lastTimestamp = timestampSeconds;

        _raAll.Add(raErrorPixels);
        _decAll.Add(decErrorPixels);
        _raShort.Add(timestampSeconds, raErrorPixels);
        _raLong.Add(timestampSeconds, raErrorPixels);
        _decShort.Add(timestampSeconds, decErrorPixels);
        _decLong.Add(timestampSeconds, decErrorPixels);
    }

    /// <summary>
    /// Total number of samples recorded.
    /// </summary>
    public uint TotalSamples => _raAll.Count;

    /// <summary>
    /// Elapsed time since the first sample in seconds.
    /// </summary>
    public double ElapsedSeconds => double.IsNaN(_startTimestamp) ? 0 : _lastTimestamp - _startTimestamp;

    /// <summary>
    /// All-time RA RMS in pixels.
    /// </summary>
    public double RaRmsAll => _raAll.Stdev;

    /// <summary>
    /// All-time Dec RMS in pixels.
    /// </summary>
    public double DecRmsAll => _decAll.Stdev;

    /// <summary>
    /// All-time total RMS (sqrt(ra² + dec²)) in pixels.
    /// </summary>
    public double TotalRmsAll => Math.Sqrt(_raAll.Stdev * _raAll.Stdev + _decAll.Stdev * _decAll.Stdev);

    /// <summary>
    /// Short-window (100s) RA RMS in pixels.
    /// </summary>
    public double RaRmsShort => _raShort.RMS;

    /// <summary>
    /// Short-window (100s) Dec RMS in pixels.
    /// </summary>
    public double DecRmsShort => _decShort.RMS;

    /// <summary>
    /// Short-window total RMS.
    /// </summary>
    public double TotalRmsShort => Math.Sqrt(_raShort.RMS * _raShort.RMS + _decShort.RMS * _decShort.RMS);

    /// <summary>
    /// Long-window (200s) RA RMS in pixels.
    /// </summary>
    public double RaRmsLong => _raLong.RMS;

    /// <summary>
    /// Long-window (200s) Dec RMS in pixels.
    /// </summary>
    public double DecRmsLong => _decLong.RMS;

    /// <summary>
    /// Long-window total RMS.
    /// </summary>
    public double TotalRmsLong => Math.Sqrt(_raLong.RMS * _raLong.RMS + _decLong.RMS * _decLong.RMS);

    /// <summary>
    /// Peak RA error (absolute) in pixels (all-time).
    /// </summary>
    public double PeakRa => _raAll.Peak;

    /// <summary>
    /// Peak Dec error (absolute) in pixels (all-time).
    /// </summary>
    public double PeakDec => _decAll.Peak;

    /// <summary>
    /// Last RA error in pixels, or null if no samples.
    /// </summary>
    public double? LastRaError => _raAll.Last;

    /// <summary>
    /// Last Dec error in pixels, or null if no samples.
    /// </summary>
    public double? LastDecError => _decAll.Last;

    /// <summary>
    /// Number of samples in the short window.
    /// </summary>
    public int ShortWindowCount => _raShort.Count;

    /// <summary>
    /// Number of samples in the long window.
    /// </summary>
    public int LongWindowCount => _raLong.Count;

    /// <summary>
    /// Detects if there is a significant drift trend by comparing short vs long RMS.
    /// A ratio significantly above 1.0 indicates growing errors (drift).
    /// </summary>
    /// <returns>Ratio of short-window RMS to long-window RMS, or null if insufficient data.</returns>
    public double? GetDriftRatio()
    {
        if (_raLong.Count < 10 || _raShort.Count < 5)
        {
            return null;
        }

        var longRms = TotalRmsLong;
        if (longRms < 0.01)
        {
            return null;
        }

        return TotalRmsShort / longRms;
    }

    /// <summary>
    /// Populates a <see cref="GuideStats"/> instance with current all-time statistics.
    /// </summary>
    public GuideStats ToGuideStats()
    {
        return new GuideStats
        {
            TotalRMS = TotalRmsAll,
            RaRMS = RaRmsAll,
            DecRMS = DecRmsAll,
            PeakRa = PeakRa,
            PeakDec = PeakDec,
            LastRaErr = LastRaError,
            LastDecErr = LastDecError
        };
    }

    /// <summary>
    /// Resets all accumulators.
    /// </summary>
    public void Reset()
    {
        _raAll.Reset();
        _decAll.Reset();
        _raShort.Reset();
        _raLong.Reset();
        _decShort.Reset();
        _decLong.Reset();
        _startTimestamp = double.NaN;
        _lastTimestamp = double.NaN;
    }
}
