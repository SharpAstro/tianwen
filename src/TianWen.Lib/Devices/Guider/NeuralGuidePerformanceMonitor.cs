using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Monitors neural model performance by comparing actual guide errors
/// when neural corrections are applied against estimated P-controller residuals.
/// Uses rolling windows to detect when the neural model is underperforming.
/// </summary>
internal sealed class NeuralGuidePerformanceMonitor
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(200);

    private readonly RollingAccum _neuralRms = new RollingAccum(Window);
    private readonly RollingAccum _pControllerRms = new RollingAccum(Window);

    /// <summary>
    /// Minimum number of samples before the monitor starts making decisions.
    /// </summary>
    public int MinSamples { get; set; } = 20;

    /// <summary>
    /// The neural model must be at least this fraction better than the P-controller
    /// estimate to be considered "helping". Default 1.15 (15% better).
    /// </summary>
    public double ThresholdRatio { get; set; } = 1.15;

    /// <summary>
    /// Rolling RMS of actual errors when neural model is active.
    /// </summary>
    public double NeuralRms => _neuralRms.RMS;

    /// <summary>
    /// Rolling RMS of estimated P-controller residuals (shadow evaluation).
    /// </summary>
    public double PControllerRms => _pControllerRms.RMS;

    /// <summary>
    /// Whether the neural model is producing better results than the P-controller.
    /// Returns true (benefit of the doubt) when insufficient data.
    /// </summary>
    public bool IsNeuralModelHelping
    {
        get
        {
            if (_neuralRms.Count < MinSamples)
            {
                return true; // not enough data, give neural model a chance
            }

            var pRms = _pControllerRms.RMS;
            if (pRms < 0.01)
            {
                return true; // P-controller residual near zero, both are fine
            }

            // Neural must be better than P * threshold
            return _neuralRms.RMS < pRms * ThresholdRatio;
        }
    }

    /// <summary>
    /// Records the error observed after a neural model correction was applied,
    /// along with the estimated P-controller residual for comparison.
    /// </summary>
    /// <param name="timestamp">Monotonic timestamp in seconds.</param>
    /// <param name="actualError">Actual total error magnitude (pixels) after neural correction.</param>
    /// <param name="pControllerEstimatedResidual">Estimated residual if P-controller had been used.</param>
    public void Record(double timestamp, double actualError, double pControllerEstimatedResidual)
    {
        _neuralRms.Add(timestamp, actualError);
        _pControllerRms.Add(timestamp, pControllerEstimatedResidual);
    }

    /// <summary>
    /// Resets the monitor.
    /// </summary>
    public void Reset()
    {
        _neuralRms.Reset();
        _pControllerRms.Reset();
    }
}
