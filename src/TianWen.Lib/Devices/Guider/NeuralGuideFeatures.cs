using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Builds the 16-element input feature vector for the neural guide model.
/// Maintains 4-frame error history, 10-frame running mean, and computes
/// altitude from hour angle, declination, and site latitude.
/// </summary>
internal sealed class NeuralGuideFeatures
{
    private const int HistorySize = 4;
    private const int MeanWindowSize = 10;

    private readonly double _sinLat;
    private readonly double _cosLat;

    // 4-frame ring buffer for (raErr, decErr)
    private readonly double[] _histRa = new double[HistorySize];
    private readonly double[] _histDec = new double[HistorySize];
    private int _histWritePos;
    private int _histCount;

    // 10-frame running mean
    private readonly double[] _meanRa = new double[MeanWindowSize];
    private readonly double[] _meanDec = new double[MeanWindowSize];
    private int _meanWritePos;
    private int _meanCount;
    private double _meanRaSum;
    private double _meanDecSum;

    private double _lastCorrectionTimestamp = double.NaN;

    /// <summary>
    /// Creates a new feature builder with the given site latitude for altitude computation.
    /// </summary>
    /// <param name="siteLatitude">Observer latitude in degrees.</param>
    public NeuralGuideFeatures(double siteLatitude = 0)
    {
        var latRad = siteLatitude * Math.PI / 180.0;
        _sinLat = Math.Sin(latRad);
        _cosLat = Math.Cos(latRad);
    }

    /// <summary>
    /// Builds the 16-element feature vector for the neural model.
    /// </summary>
    /// <param name="raErrorPx">Current RA error in pixels.</param>
    /// <param name="decErrorPx">Current Dec error in pixels.</param>
    /// <param name="timestampSec">Current monotonic timestamp in seconds.</param>
    /// <param name="raRmsShort">Short-window RA RMS in pixels.</param>
    /// <param name="decRmsShort">Short-window Dec RMS in pixels.</param>
    /// <param name="hourAngle">Current hour angle in hours (-12 to +12).</param>
    /// <param name="declination">Target declination in degrees (-90 to +90).</param>
    /// <param name="features">Output span to fill (must be length 16).</param>
    public void Build(
        double raErrorPx, double decErrorPx,
        double timestampSec,
        double raRmsShort, double decRmsShort,
        double hourAngle,
        double declination,
        Span<float> features)
    {
        // Push current error into 4-frame history ring buffer
        var hIdx = _histWritePos % HistorySize;
        _histRa[hIdx] = raErrorPx;
        _histDec[hIdx] = decErrorPx;
        _histWritePos = hIdx + 1;
        if (_histCount < HistorySize) _histCount++;

        // Push into 10-frame mean ring buffer
        var mIdx = _meanWritePos % MeanWindowSize;
        _meanRaSum -= _meanRa[mIdx];
        _meanDecSum -= _meanDec[mIdx];
        _meanRa[mIdx] = raErrorPx;
        _meanDec[mIdx] = decErrorPx;
        _meanRaSum += raErrorPx;
        _meanDecSum += decErrorPx;
        _meanWritePos = mIdx + 1;
        if (_meanCount < MeanWindowSize) _meanCount++;

        // [0-1] Current RA/Dec error
        features[0] = (float)raErrorPx;
        features[1] = (float)decErrorPx;

        // [2-7] t-1, t-2, t-3 RA/Dec errors from ring buffer
        for (var i = 1; i <= 3; i++)
        {
            if (i < _histCount)
            {
                // _histWritePos points past the just-written entry;
                // _histWritePos-1 = current, _histWritePos-1-i = t-i
                var prevIdx = ((_histWritePos - 1 - i) % HistorySize + HistorySize) % HistorySize;
                features[i * 2] = (float)_histRa[prevIdx];
                features[i * 2 + 1] = (float)_histDec[prevIdx];
            }
            else
            {
                features[i * 2] = 0f;
                features[i * 2 + 1] = 0f;
            }
        }

        // [8-9] Mean RA/Dec error over last 10 frames
        var meanN = Math.Max(1, _meanCount);
        features[8] = (float)(_meanRaSum / meanN);
        features[9] = (float)(_meanDecSum / meanN);

        // [10-11] Short-window RA/Dec RMS
        features[10] = (float)raRmsShort;
        features[11] = (float)decRmsShort;

        // [12] Time since last correction
        features[12] = (float)(double.IsNaN(_lastCorrectionTimestamp)
            ? 0
            : timestampSec - _lastCorrectionTimestamp);

        // [13] Hour angle / 12 (normalized to [-1, 1])
        features[13] = (float)(hourAngle / 12.0);

        // [14] Altitude / 90 (normalized to [0, 1])
        var decRad = declination * Math.PI / 180.0;
        var haRad = hourAngle * 15.0 * Math.PI / 180.0;
        var sinAlt = _sinLat * Math.Sin(decRad) + _cosLat * Math.Cos(decRad) * Math.Cos(haRad);
        var altDeg = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * 180.0 / Math.PI;
        features[14] = (float)(altDeg / 90.0);

        // [15] Declination / 90 (normalized to [-1, 1])
        features[15] = (float)(declination / 90.0);
    }

    /// <summary>
    /// Records that a correction was applied at this timestamp.
    /// </summary>
    public void RecordCorrection(double timestampSec)
    {
        _lastCorrectionTimestamp = timestampSec;
    }

    /// <summary>
    /// Resets state for a new guide session.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_histRa);
        Array.Clear(_histDec);
        _histWritePos = 0;
        _histCount = 0;

        Array.Clear(_meanRa);
        Array.Clear(_meanDec);
        _meanWritePos = 0;
        _meanCount = 0;
        _meanRaSum = 0;
        _meanDecSum = 0;

        _lastCorrectionTimestamp = double.NaN;
    }
}
