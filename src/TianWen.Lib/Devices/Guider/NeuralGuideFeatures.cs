using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Builds the 22-element input feature vector for the neural guide model.
/// Maintains 4-frame error history, multi-scale running means (short 10-frame,
/// medium 60-frame, long 300-frame), and accumulated gear error that traces the
/// PE curve. The accumulated gear error is the key feature for predictive correction:
/// it represents what the mount did wrong (sum of residuals + applied corrections).
/// </summary>
internal sealed class NeuralGuideFeatures
{
    private const int HistorySize = 4;
    private const int ShortMeanSize = 10;   // ~20s at 2s exposure
    private const int MediumMeanSize = 60;  // ~2 min at 2s exposure
    private const int LongMeanSize = 300;   // ~10 min at 2s exposure (~1 PE cycle)

    private readonly double _sinLat;
    private readonly double _cosLat;

    // 4-frame ring buffer for (raErr, decErr)
    private readonly double[] _histRa = new double[HistorySize];
    private readonly double[] _histDec = new double[HistorySize];
    private int _histWritePos;
    private int _histCount;

    // Short-term running mean (10 frames)
    private readonly double[] _shortRa = new double[ShortMeanSize];
    private readonly double[] _shortDec = new double[ShortMeanSize];
    private int _shortWritePos;
    private int _shortCount;
    private double _shortRaSum;
    private double _shortDecSum;

    // Medium-term running mean (60 frames)
    private readonly double[] _medRa = new double[MediumMeanSize];
    private readonly double[] _medDec = new double[MediumMeanSize];
    private int _medWritePos;
    private int _medCount;
    private double _medRaSum;
    private double _medDecSum;

    // Long-term running mean (300 frames)
    private readonly double[] _longRa = new double[LongMeanSize];
    private readonly double[] _longDec = new double[LongMeanSize];
    private int _longWritePos;
    private int _longCount;
    private double _longRaSum;
    private double _longDecSum;

    // Accumulated gear error: sum of (residual_error + correction_applied_in_pixels).
    // This traces the PE curve — even when guiding corrects the error perfectly,
    // the accumulated gear error shows the total periodic displacement the mount produced.
    private double _accumulatedGearErrorRa;
    private double _accumulatedGearErrorDec;

    // Normalization: gear error is unbounded, so we normalize by the running RMS of the
    // gear error delta. This keeps the feature in a reasonable range for the neural network.
    private double _gearErrorDeltaSumSqRa;
    private double _gearErrorDeltaSumSqDec;
    private int _gearErrorCount;

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
    /// Builds the 22-element feature vector for the neural model.
    /// </summary>
    /// <param name="raErrorPx">Current RA error in pixels (residual after correction).</param>
    /// <param name="decErrorPx">Current Dec error in pixels (residual after correction).</param>
    /// <param name="raCorrectionPx">RA correction applied this frame in pixels (positive = moved star West).</param>
    /// <param name="decCorrectionPx">Dec correction applied this frame in pixels.</param>
    /// <param name="timestampSec">Current monotonic timestamp in seconds.</param>
    /// <param name="raRmsShort">Short-window RA RMS in pixels.</param>
    /// <param name="decRmsShort">Short-window Dec RMS in pixels.</param>
    /// <param name="hourAngle">Current hour angle in hours (-12 to +12).</param>
    /// <param name="declination">Target declination in degrees (-90 to +90).</param>
    /// <param name="raEncoderPhaseRadians">RA encoder phase in radians (mod worm period), or NaN if unavailable.</param>
    /// <param name="decEncoderPhaseRadians">Dec encoder phase in radians (mod worm period), or NaN if unavailable.</param>
    /// <param name="features">Output span to fill (must be length 26).</param>
    public void Build(
        double raErrorPx, double decErrorPx,
        double raCorrectionPx, double decCorrectionPx,
        double timestampSec,
        double raRmsShort, double decRmsShort,
        double hourAngle,
        double declination,
        double raEncoderPhaseRadians,
        double decEncoderPhaseRadians,
        Span<float> features)
    {
        // Accumulate gear error: what the mount did wrong = residual + what we corrected.
        // This traces the PE curve even when guiding keeps the residual near zero.
        var gearDeltaRa = raErrorPx + raCorrectionPx;
        var gearDeltaDec = decErrorPx + decCorrectionPx;
        _accumulatedGearErrorRa += gearDeltaRa;
        _accumulatedGearErrorDec += gearDeltaDec;
        _gearErrorDeltaSumSqRa += gearDeltaRa * gearDeltaRa;
        _gearErrorDeltaSumSqDec += gearDeltaDec * gearDeltaDec;
        _gearErrorCount++;
        // Push current error into 4-frame history ring buffer
        var hIdx = _histWritePos % HistorySize;
        _histRa[hIdx] = raErrorPx;
        _histDec[hIdx] = decErrorPx;
        _histWritePos = hIdx + 1;
        if (_histCount < HistorySize) _histCount++;

        // Push into short-term mean ring buffer (10 frames)
        PushMean(_shortRa, _shortDec, ref _shortWritePos, ref _shortCount, ref _shortRaSum, ref _shortDecSum,
            ShortMeanSize, raErrorPx, decErrorPx);

        // Push into medium-term mean ring buffer (60 frames)
        PushMean(_medRa, _medDec, ref _medWritePos, ref _medCount, ref _medRaSum, ref _medDecSum,
            MediumMeanSize, raErrorPx, decErrorPx);

        // Push into long-term mean ring buffer (300 frames)
        PushMean(_longRa, _longDec, ref _longWritePos, ref _longCount, ref _longRaSum, ref _longDecSum,
            LongMeanSize, raErrorPx, decErrorPx);

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

        // [8-9] Short-term mean RA/Dec error (10 frames, ~20s)
        features[8] = (float)(_shortRaSum / Math.Max(1, _shortCount));
        features[9] = (float)(_shortDecSum / Math.Max(1, _shortCount));

        // [10-11] Short-window RA/Dec RMS
        features[10] = (float)raRmsShort;
        features[11] = (float)decRmsShort;

        // [12-13] Medium-term mean RA/Dec error (60 frames, ~2min)
        features[12] = (float)(_medRaSum / Math.Max(1, _medCount));
        features[13] = (float)(_medDecSum / Math.Max(1, _medCount));

        // [14-15] Long-term mean RA/Dec error (300 frames, ~10min ≈ 1 PE cycle)
        features[14] = (float)(_longRaSum / Math.Max(1, _longCount));
        features[15] = (float)(_longDecSum / Math.Max(1, _longCount));

        // [16-17] Accumulated gear error RA/Dec, normalized by running RMS of deltas.
        // This traces the PE curve. Normalization keeps it in a learnable range regardless
        // of absolute PE amplitude (varies per mount from 5" to 60"+).
        var gearRmsRa = _gearErrorCount > 1 ? Math.Sqrt(_gearErrorDeltaSumSqRa / _gearErrorCount) : 1.0;
        var gearRmsDec = _gearErrorCount > 1 ? Math.Sqrt(_gearErrorDeltaSumSqDec / _gearErrorCount) : 1.0;
        features[16] = (float)(_accumulatedGearErrorRa / Math.Max(gearRmsRa * 10, 0.1));
        features[17] = (float)(_accumulatedGearErrorDec / Math.Max(gearRmsDec * 10, 0.1));

        // [18] Hour angle / 12 (normalized to [-1, 1])
        features[18] = (float)(hourAngle / 12.0);

        // [19] Altitude / 90 (normalized to [0, 1])
        var decRad = declination * Math.PI / 180.0;
        var haRad = hourAngle * 15.0 * Math.PI / 180.0;
        var sinAlt = _sinLat * Math.Sin(decRad) + _cosLat * Math.Cos(decRad) * Math.Cos(haRad);
        var altDeg = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * 180.0 / Math.PI;
        features[19] = (float)(altDeg / 90.0);

        // [20] Declination / 90 (normalized to [-1, 1])
        features[20] = (float)(declination / 90.0);

        // [21] Time since last correction (seconds, clamped)
        features[21] = (float)Math.Min(double.IsNaN(_lastCorrectionTimestamp)
            ? 0
            : timestampSec - _lastCorrectionTimestamp, 30.0);

        // [22-23] RA encoder phase as sin/cos pair (wraps smoothly, learnable by the network).
        // When the mount doesn't expose encoder data (NaN), both components are 0 — the network
        // learns to ignore them. sin/cos encoding avoids the discontinuity at 0/2π.
        if (!double.IsNaN(raEncoderPhaseRadians))
        {
            features[22] = (float)Math.Sin(raEncoderPhaseRadians);
            features[23] = (float)Math.Cos(raEncoderPhaseRadians);
        }
        else
        {
            features[22] = 0f;
            features[23] = 0f;
        }

        // [24-25] Dec encoder phase as sin/cos pair.
        // Dec PE is less prominent than RA, but belt-driven Dec axes and gear mesh
        // patterns can produce repeatable Dec errors that the model can learn.
        if (!double.IsNaN(decEncoderPhaseRadians))
        {
            features[24] = (float)Math.Sin(decEncoderPhaseRadians);
            features[25] = (float)Math.Cos(decEncoderPhaseRadians);
        }
        else
        {
            features[24] = 0f;
            features[25] = 0f;
        }
    }

    private static void PushMean(
        double[] ra, double[] dec,
        ref int writePos, ref int count,
        ref double raSum, ref double decSum,
        int size, double raVal, double decVal)
    {
        var idx = writePos % size;
        raSum -= ra[idx];
        decSum -= dec[idx];
        ra[idx] = raVal;
        dec[idx] = decVal;
        raSum += raVal;
        decSum += decVal;
        writePos = idx + 1;
        if (count < size) count++;
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

        Array.Clear(_shortRa);
        Array.Clear(_shortDec);
        _shortWritePos = 0;
        _shortCount = 0;
        _shortRaSum = 0;
        _shortDecSum = 0;

        Array.Clear(_medRa);
        Array.Clear(_medDec);
        _medWritePos = 0;
        _medCount = 0;
        _medRaSum = 0;
        _medDecSum = 0;

        Array.Clear(_longRa);
        Array.Clear(_longDec);
        _longWritePos = 0;
        _longCount = 0;
        _longRaSum = 0;
        _longDecSum = 0;

        _accumulatedGearErrorRa = 0;
        _accumulatedGearErrorDec = 0;
        _gearErrorDeltaSumSqRa = 0;
        _gearErrorDeltaSumSqDec = 0;
        _gearErrorCount = 0;

        _lastCorrectionTimestamp = double.NaN;
    }
}
