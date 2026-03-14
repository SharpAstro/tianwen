using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Time-windowed online variance accumulator using a circular buffer.
/// Maintains mean, RMS, and peak over a sliding time window.
/// Uses the remove-and-recompute approach for numerical stability.
/// </summary>
internal sealed class RollingAccum
{
    private readonly TimeSpan _window;
    private readonly Queue<(double Timestamp, double Value)> _samples;
    private double _sum;
    private double _sumSq;
    private double _peak;

    /// <summary>
    /// Creates a new rolling accumulator with the specified time window.
    /// </summary>
    /// <param name="window">Duration of the sliding window.</param>
    public RollingAccum(TimeSpan window)
    {
        _window = window;
        _samples = new Queue<(double, double)>();
    }

    /// <summary>
    /// Number of samples currently in the window.
    /// </summary>
    public int Count => _samples.Count;

    /// <summary>
    /// Mean of samples in the window.
    /// </summary>
    public double Mean => _samples.Count > 0 ? _sum / _samples.Count : 0.0;

    /// <summary>
    /// RMS (root mean square) of samples in the window.
    /// </summary>
    public double RMS => _samples.Count > 0 ? Math.Sqrt(_sumSq / _samples.Count) : 0.0;

    /// <summary>
    /// Standard deviation of samples in the window.
    /// </summary>
    public double Stdev
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0.0;
            }

            var mean = Mean;
            var variance = _sumSq / _samples.Count - mean * mean;
            return Math.Sqrt(Math.Max(0, variance));
        }
    }

    /// <summary>
    /// Peak absolute value in the window.
    /// </summary>
    public double Peak => _peak;

    /// <summary>
    /// Adds a sample at the given timestamp and evicts expired samples.
    /// </summary>
    /// <param name="timestampSeconds">Timestamp in seconds (monotonic).</param>
    /// <param name="value">Sample value.</param>
    public void Add(double timestampSeconds, double value)
    {
        _samples.Enqueue((timestampSeconds, value));
        _sum += value;
        _sumSq += value * value;

        var absVal = Math.Abs(value);
        if (absVal > _peak)
        {
            _peak = absVal;
        }

        // Evict expired samples
        var cutoff = timestampSeconds - _window.TotalSeconds;
        while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
        {
            var (_, oldVal) = _samples.Dequeue();
            _sum -= oldVal;
            _sumSq -= oldVal * oldVal;
        }

        // Recompute peak if the old peak might have been evicted
        if (_peak > absVal)
        {
            RecomputePeak();
        }
    }

    /// <summary>
    /// Resets the accumulator, clearing all samples.
    /// </summary>
    public void Reset()
    {
        _samples.Clear();
        _sum = 0;
        _sumSq = 0;
        _peak = 0;
    }

    private void RecomputePeak()
    {
        _peak = 0;
        foreach (var (_, val) in _samples)
        {
            var abs = Math.Abs(val);
            if (abs > _peak)
            {
                _peak = abs;
            }
        }
    }
}
