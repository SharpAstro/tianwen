using System;
using System.Collections.Generic;

namespace TianWen.UI.Abstractions;

/// <summary>
/// One sampled snapshot of camera cooler/temperature state, taken by the equipment
/// tab's polling tick while the camera is connected via <c>IDeviceHub</c>.
/// </summary>
public readonly record struct CameraTelemetrySample(
    DateTimeOffset Timestamp,
    double? CcdTempC,
    double? HeatSinkTempC,
    double? SetpointC,
    double? CoolerPowerPct,
    bool CoolerOn,
    bool Busy);

/// <summary>
/// Fixed-capacity ring buffer of recent <see cref="CameraTelemetrySample"/>s for
/// one camera URI. The newest sample is always at index <see cref="Count"/>-1
/// in chronological order.
/// </summary>
public sealed class CameraTelemetryBuffer
{
    private readonly CameraTelemetrySample[] _ring;
    private int _head; // index where the next sample will be written
    private int _count;

    public CameraTelemetryBuffer(int capacity = 240) // ~8 minutes at 2s sampling
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new CameraTelemetrySample[capacity];
    }

    public int Capacity => _ring.Length;
    public int Count => _count;

    public void Add(CameraTelemetrySample sample)
    {
        _ring[_head] = sample;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    /// <summary>Reads the buffer in chronological order (oldest first, newest last).</summary>
    public IEnumerable<CameraTelemetrySample> InOrder()
    {
        if (_count == 0) yield break;
        var start = (_head - _count + _ring.Length) % _ring.Length;
        for (var i = 0; i < _count; i++)
        {
            yield return _ring[(start + i) % _ring.Length];
        }
    }

    public CameraTelemetrySample? Latest =>
        _count == 0 ? null : _ring[(_head - 1 + _ring.Length) % _ring.Length];
}
