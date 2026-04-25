using System;
using System.Collections.Immutable;

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
/// one camera URI. The newest sample is always last in <see cref="InOrder"/>.
/// <para>
/// <b>Thread safety:</b> backed by an <see cref="ImmutableArray{T}"/> with lock-free
/// CAS-loop updates via <see cref="ImmutableInterlocked.Update{T}(ref ImmutableArray{T}, Func{ImmutableArray{T}, ImmutableArray{T}})"/>.
/// Readers (render thread, every frame) snapshot <c>_samples</c> in a single
/// reference read; concurrent writes never tear the snapshot. Multiple concurrent
/// writers are also safe — the CAS retries on conflict.
/// </para>
/// </summary>
public sealed class CameraTelemetryBuffer
{
    private readonly int _capacity;
    private ImmutableArray<CameraTelemetrySample> _samples = [];

    public CameraTelemetryBuffer(int capacity = 240) // ~8 minutes at 2s sampling
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity => _capacity;
    public int Count => _samples.Length;

    public void Add(CameraTelemetrySample sample)
    {
        ImmutableInterlocked.Update(
            ref _samples,
            static (current, args) =>
                current.Length < args.cap
                    ? current.Add(args.sample)
                    : current.RemoveAt(0).Add(args.sample),
            (cap: _capacity, sample));
    }

    /// <summary>Returns the buffer in chronological order (oldest first, newest last).</summary>
    public ImmutableArray<CameraTelemetrySample> InOrder() => _samples;

    public CameraTelemetrySample? Latest
    {
        get
        {
            var s = _samples;
            return s.Length == 0 ? null : s[^1];
        }
    }
}
