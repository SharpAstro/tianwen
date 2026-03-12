using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Sequencing;

public sealed class ScheduledObservationTree : IReadOnlyList<ScheduledObservation>
{
    private readonly ImmutableArray<ScheduledObservation> _primary;
    private readonly ImmutableDictionary<int, ImmutableArray<ScheduledObservation>> _spares;

    public ScheduledObservationTree(ImmutableArray<ScheduledObservation> primary, ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>? spares = null)
    {
        _primary = primary;
        _spares = spares ?? ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Empty;
    }

    public ScheduledObservationTree(ReadOnlySpan<ScheduledObservation> observations)
    {
        _primary = [.. observations];
        _spares = ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Empty;
    }

    public ScheduledObservation this[int index] => _primary[index];

    public int Count => _primary.Length;

    public ImmutableArray<ScheduledObservation> GetSparesForSlot(int index)
        => _spares.TryGetValue(index, out var spares) ? spares : [];

    /// <summary>
    /// Tries to get the next spare for a given primary slot.
    /// Advances <paramref name="spareIndex"/> and returns the spare observation, or null if exhausted.
    /// </summary>
    public ScheduledObservation? TryGetNextSpare(int primaryIndex, ref int spareIndex)
    {
        if (_spares.TryGetValue(primaryIndex, out var spares) && spareIndex < spares.Length)
        {
            return spares[spareIndex++];
        }
        return null;
    }

    public IEnumerator<ScheduledObservation> GetEnumerator()
    {
        foreach (var obs in _primary)
        {
            yield return obs;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
