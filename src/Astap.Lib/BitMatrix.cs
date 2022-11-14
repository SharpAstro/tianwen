using System;
using System.Text;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public struct BitMatrix
{
    const int VECTOR_SIZE = 32;
    const int VECTOR_SIZE_SHIFT = 5;
    const int VECTOR_SIZE_MASK = VECTOR_SIZE - 1;

    private readonly BitVector32[,] _vectors;
    private readonly int _d1;

    public BitMatrix(int d0, int d1)
    {
        var div = DivRem(_d1 = d1, out var rem);

        _vectors = new BitVector32[d0, div + (rem > 0 ? 1 : 0)];
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
            var mask = 1 << rem;

            return _vectors[d0, d1div][mask];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            if (d1 < 0 || d1 >= _d1)
            {
                throw new IndexOutOfRangeException();
            }

            var d1div = DivRem(d1, out var rem);
            var mask = 1 << rem;

            _vectors[d0, d1div][mask] = value;
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
        0 => _vectors.GetLength(0),
        1 => _d1,
        _ => throw new ArgumentOutOfRangeException(nameof(dim), dim, "Must be 0 or 1"),
    };

    public void Clear()
    {
        for (var i = 0; i < _vectors.GetLength(0); i++)
        {
            for (var j = 0; j < _vectors.GetLength(1); j++)
            {
                _vectors[i, j] = new BitVector32();
            }
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _vectors.GetLength(0); i++)
        {
            for (var j = 0; j < _vectors.GetLength(1); j++)
            {
                if (j > 0)
                {
                    sb.Append(", ");
                }

                var vectorBits = Convert.ToString(_vectors[i, j].Data, 2).PadLeft(VECTOR_SIZE, '0');

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
