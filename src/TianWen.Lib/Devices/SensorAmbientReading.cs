using System;
using System.Collections.Generic;

namespace TianWen.Lib.Devices;

/// <summary>
/// The sensor temperature a cool-down started from, taken with the cooler off, and whether it had SETTLED
/// (<see cref="SensorAmbientSampler"/>). A camera whose body sits at the air around it (a fan that always runs, no
/// heat-sink reading) reads its ambient this way, and a warm-up can then end at it instead of waiting out a +25 C
/// fallback the sensor never reaches (#1842). Only a settled reading is a sound ambient; an unsettled one is recorded
/// beside it so the warm-up falls back to the next source rather than trusting it.
/// </summary>
/// <param name="TempC">The window's mean in degrees Celsius (the last sample alone when no window was ever full).</param>
/// <param name="Settled">Whether the sensor had been steady over <see cref="SensorAmbientSampler.Window"/> with the cooler off.</param>
/// <param name="AtUtc">When the reading was taken, on the node's clock.</param>
public readonly record struct SensorAmbientReading(double TempC, bool Settled, DateTimeOffset AtUtc);

/// <summary>
/// Decides whether a sensor's temperature has SETTLED: steady over a window with no trend. Pure: it is fed
/// (time, temperature) samples and owns no clock, so a test drives it with any times it likes.
/// </summary>
/// <remarks>
/// The owner's rule (#1842, 2026-10-26): the sensor counts as ambient only once it has been steady for about a minute
/// with the cooler off. A camera just brought in from the sun or the cold, or just powered up, is still moving and is
/// NOT ambient. Settled needs BOTH a small spread (a noisy sensor is not steady) and a small slope (a slow monotonic
/// drift can stay inside the spread over a short window, and still be a camera warming or cooling).
/// A NaN sample (the driver could not read) breaks the window: what was seen before it says nothing about after.
/// </remarks>
public sealed class SensorAmbientSampler
{
    /// <summary>How long the sensor must have been steady: the owner's "about a minute".</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>The widest spread (max minus min) a settled window may hold, in degrees Celsius.</summary>
    public const double MaxSpreadC = 0.2;

    /// <summary>
    /// The steepest least-squares trend a settled window may show, in degrees Celsius per minute. Half the spread
    /// bound over the window, so a drift that the spread test alone would pass at its edge is still refused.
    /// </summary>
    public const double MaxSlopeCPerMinute = 0.1;

    private readonly List<(DateTimeOffset Time, double TempC)> _samples = [];
    private DateTimeOffset? _runStart;

    /// <summary>The most recent reading: settled when the window was steady, else the window's mean; null before any sample.</summary>
    public SensorAmbientReading? Current { get; private set; }

    /// <summary>The spread of the window the last sample closed, for a log line; NaN when there was none.</summary>
    public double WindowSpreadC { get; private set; } = double.NaN;

    /// <summary>Whether the last sample left the sensor settled.</summary>
    public bool Settled => Current is { Settled: true };

    /// <summary>Feeds one sample; returns whether the sensor is now settled.</summary>
    public bool Add(DateTimeOffset time, double tempC)
    {
        if (!double.IsFinite(tempC))
        {
            _samples.Clear();
            _runStart = null;
            Current = null;
            WindowSpreadC = double.NaN;
            return false;
        }

        _runStart ??= time;
        _samples.Add((time, tempC));
        _samples.RemoveAll(s => time - s.Time > Window);

        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0.0;
        foreach (var (_, t) in _samples)
        {
            min = Math.Min(min, t);
            max = Math.Max(max, t);
            sum += t;
        }
        var mean = sum / _samples.Count;
        WindowSpreadC = max - min;

        var full = time - _runStart.Value >= Window && _samples.Count >= 2;
        var settled = full && WindowSpreadC <= MaxSpreadC && Math.Abs(SlopeCPerMinute()) <= MaxSlopeCPerMinute;
        Current = new SensorAmbientReading(full ? mean : tempC, settled, time);
        return settled;
    }

    // Least-squares slope over the window, in degrees per minute.
    private double SlopeCPerMinute()
    {
        var t0 = _samples[0].Time;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (time, temp) in _samples)
        {
            var x = (time - t0).TotalMinutes;
            sx += x;
            sy += temp;
            sxx += x * x;
            sxy += x * temp;
        }
        var n = _samples.Count;
        var denominator = n * sxx - sx * sx;
        return denominator <= 0 ? 0 : (n * sxy - sx * sy) / denominator;
    }
}
