using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Builds the input feature vector for the neural guide model from
/// current and historical guide error state.
/// </summary>
internal sealed class NeuralGuideFeatures
{
    private double _prevRaError;
    private double _prevDecError;
    private double _prevTimestamp = double.NaN;
    private double _lastCorrectionTimestamp = double.NaN;

    /// <summary>
    /// Builds the feature vector for the neural model.
    /// </summary>
    /// <param name="raErrorPx">Current RA error in pixels.</param>
    /// <param name="decErrorPx">Current Dec error in pixels.</param>
    /// <param name="timestampSec">Current monotonic timestamp in seconds.</param>
    /// <param name="raRmsShort">Short-window RA RMS in pixels.</param>
    /// <param name="decRmsShort">Short-window Dec RMS in pixels.</param>
    /// <param name="hourAngle">Current hour angle in hours (-12 to +12).</param>
    /// <param name="features">Output span to fill (must be length 10).</param>
    public void Build(
        double raErrorPx, double decErrorPx,
        double timestampSec,
        double raRmsShort, double decRmsShort,
        double hourAngle,
        Span<float> features)
    {
        var dt = double.IsNaN(_prevTimestamp) ? 1.0 : Math.Max(0.01, timestampSec - _prevTimestamp);

        features[0] = (float)raErrorPx;
        features[1] = (float)decErrorPx;
        features[2] = (float)_prevRaError;
        features[3] = (float)_prevDecError;
        features[4] = (float)((raErrorPx - _prevRaError) / dt);    // RA rate
        features[5] = (float)((decErrorPx - _prevDecError) / dt);  // Dec rate
        features[6] = (float)raRmsShort;
        features[7] = (float)decRmsShort;
        features[8] = (float)(double.IsNaN(_lastCorrectionTimestamp) ? 0 : timestampSec - _lastCorrectionTimestamp);
        features[9] = (float)(hourAngle / 12.0); // Normalize to [-1, 1]

        _prevRaError = raErrorPx;
        _prevDecError = decErrorPx;
        _prevTimestamp = timestampSec;
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
        _prevRaError = 0;
        _prevDecError = 0;
        _prevTimestamp = double.NaN;
        _lastCorrectionTimestamp = double.NaN;
    }
}
