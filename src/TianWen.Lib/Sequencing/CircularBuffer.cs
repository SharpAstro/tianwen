using System;
using System.Collections;
using System.Collections.Generic;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Thread-safe fixed-capacity circular buffer. When full, new items overwrite the oldest.
/// Implements <see cref="IReadOnlyList{T}"/> for snapshot reads (index 0 = oldest).
/// </summary>
internal sealed class CircularBuffer<T>(int capacity) : IReadOnlyList<T>
{
    private readonly T[] _buffer = new T[capacity];
    private readonly object _lock = new object();
    private int _head; // next write position
    private int _count;

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public T this[int index]
    {
        get
        {
            lock (_lock)
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                // oldest item is at (_head - _count + capacity) % capacity
                var actualIndex = (_head - _count + index + capacity) % capacity;
                return _buffer[actualIndex];
            }
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % capacity;
            if (_count < capacity)
            {
                _count++;
            }
        }
    }

    public List<T> ToList()
    {
        lock (_lock)
        {
            var list = new List<T>(_count);
            for (var i = 0; i < _count; i++)
            {
                var actualIndex = (_head - _count + i + capacity) % capacity;
                list.Add(_buffer[actualIndex]);
            }
            return list;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        // Snapshot to avoid holding lock during enumeration
        var snapshot = ToList();
        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
