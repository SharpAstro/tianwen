using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Astap.Lib;

public struct BitMatrix
{
    const int VECTOR_SIZE = 32;
    const int VECTOR_SIZE_SHIFT = 5;
    const int VECTOR_SIZE_MASK = VECTOR_SIZE - 1;

    private readonly uint[,] _data;
    private readonly int _d1;

    public BitMatrix(int d0, int d1)
    {
        var div = DivRem(_d1 = d1, out var rem);

        _data = new uint[d0, div + (rem > 0 ? 1 : 0)];
    }

    public bool this[int d0, int d1]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (d1 < 0 || d1 >= _d1)
            {
                throw new IndexOutOfRangeException();
            }

            var d1div = DivRem(d1, out var rem);
            var shift = 1u << rem;
            return (_data[d0, d1div] & shift) == shift;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            if (d1 < 0 || d1 >= _d1)
            {
                throw new IndexOutOfRangeException();
            }

            var d1div = DivRem(d1, out var rem);
            var shift = 1u << rem;
            if (value)
            {
                _data[d0, d1div] |= shift;
            }
            else
            {
                _data[d0, d1div] &= ~shift;
            }
        }
    }

    public bool this[int d0, Range d1]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            if (d1.Start.Value < 0 || d1.End.Value > _d1)
            {
                throw new IndexOutOfRangeException();
            }

            unchecked
            {
                const uint setMask = (uint)-1;

                var d1StartDiv = DivRem(d1.Start.Value, out var d1StartRem);
                var d1EndDiv = DivRem(d1.End.Value - 1, out var d1EndRem);
                var startData = _data[d0, d1StartDiv];
                var shiftedStartMask = setMask << d1StartRem;
                var shiftedEndMask = setMask >> (VECTOR_SIZE - d1EndRem - 1);

                if (d1StartDiv == d1EndDiv)
                {
                    var capMask = shiftedEndMask & shiftedStartMask;
                    _data[d0, d1StartDiv] = value ? startData | capMask : startData & ~capMask;
                }
                else
                {
                    var d1Div = d1StartDiv;
                    _data[d0, d1Div++] = value ? startData | shiftedStartMask : startData & ~shiftedStartMask;

                    var midData = value ? setMask : 0u;
                    for (; d1Div < d1EndDiv; d1Div++)
                    {
                        _data[d0, d1Div] = midData;
                    }

                    var endData = _data[d0, d1Div];
                    _data[d0, d1Div] = value ? endData | shiftedEndMask : endData & ~shiftedEndMask;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int DivRem(int d1, out int rem)
    {
        rem = d1 & VECTOR_SIZE_MASK;
        return d1 >> VECTOR_SIZE_SHIFT;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int GetLength(int dim) => dim switch
    {
        0 => _data.GetLength(0),
        1 => _d1,
        _ => throw new ArgumentOutOfRangeException(nameof(dim), dim, "Must be 0 or 1"),
    };

    public void ClearAll()
    {
        for (var i = 0; i < _data.GetLength(0); i++)
        {
            for (var j = 0; j < _data.GetLength(1); j++)
            {
                _data[i, j] = 0;
            }
        }
    }

    public void SetAll()
    {
        unchecked
        {
            for (var i = 0; i < _data.GetLength(0); i++)
            {
                for (var j = 0; j < _data.GetLength(1); j++)
                {
                    _data[i, j] = (uint)-1;
                }
            }
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _data.GetLength(0); i++)
        {
            for (var j = 0; j < _data.GetLength(1); j++)
            {
                if (j > 0)
                {
                    sb.Append(", ");
                }

                var vectorBits = Convert.ToString(_data[i, j], 2).PadLeft(VECTOR_SIZE, '0');

                for (var k = 0; k < VECTOR_SIZE / 8; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(vectorBits.AsSpan(k * 8, 8));
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
