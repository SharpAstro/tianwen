using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace TianWen.Lib;

/// <summary>
/// Represents a matrix of bits.
/// </summary>
public readonly struct BitMatrix
{
    const int VECTOR_SIZE = 64;
    const int VECTOR_SIZE_SHIFT = 6;
    const int VECTOR_SIZE_MASK = VECTOR_SIZE - 1;

    private readonly ulong[,] _data;
    private readonly int _d1;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitMatrix"/> struct.
    /// </summary>
    /// <param name="d0">The number of rows.</param>
    /// <param name="d1">The number of columns.</param>
    public BitMatrix(int d0, int d1)
    {
        var div = DivRem(_d1 = d1, out var rem);
        _data = new ulong[d0, div + (rem > 0 ? 1 : 0)];
    }

    /// <summary>
    /// Gets or sets the bit at the specified position.
    /// </summary>
    /// <param name="d0">The row index.</param>
    /// <param name="d1">The column index.</param>
    /// <returns>The bit value at the specified position.</returns>
    /// <exception cref="IndexOutOfRangeException">Thrown when the column index is out of range.</exception>
    public readonly bool this[int d0, int d1]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (d1 < 0 || d1 >= _d1)
            {
                throw new IndexOutOfRangeException();
            }

            var d1div = DivRem(d1, out var rem);
            var shift = 1ul << rem;
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
            var shift = 1ul << rem;
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

    /// <summary>
    /// Gets or sets the bit at the specified position using an <see cref="Index"/>.
    /// </summary>
    /// <param name="d0">The row index.</param>
    /// <param name="d1">The column index as an <see cref="Index"/>.</param>
    /// <returns>The bit value at the specified position.</returns>
    public readonly bool this[int d0, Index d1]
    {
        get => this[d0, d1.IsFromEnd ? _d1 - d1.Value : d1.Value];
        set => this[d0, d1.IsFromEnd ? _d1 - d1.Value : d1.Value] = value;
    }

    /// <summary>
    /// Sets the bits in the specified range to the specified value.
    /// </summary>
    /// <param name="d0">The row index.</param>
    /// <param name="d1">The range of columns.</param>
    /// <param name="value">The value to set the bits to.</param>
    /// <exception cref="IndexOutOfRangeException">Thrown when the range is out of bounds.</exception>
    public readonly bool this[int d0, Range d1]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            var start = d1.Start.IsFromEnd ? _d1 - d1.Start.Value : d1.Start.Value;
            var end = (d1.End.IsFromEnd ? _d1 - d1.End.Value : d1.End.Value) - 1;
            if (start < 0 || end > _d1)
            {
                throw new IndexOutOfRangeException();
            }

            unchecked
            {
                const ulong setMask = (ulong)-1;

                var d1StartDiv = DivRem(start, out var d1StartRem);
                var d1EndDiv = DivRem(end, out var d1EndRem);
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

                    var midData = value ? setMask : 0ul;
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

    /// <summary>
    /// Divides the specified value by the vector size and returns the quotient and remainder.
    /// </summary>
    /// <param name="d1">The value to divide.</param>
    /// <param name="rem">The remainder of the division.</param>
    /// <returns>The quotient of the division.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int DivRem(int d1, out int rem)
    {
        rem = d1 & VECTOR_SIZE_MASK;
        return d1 >> VECTOR_SIZE_SHIFT;
    }

    /// <summary>
    /// Gets the length of the specified dimension.
    /// </summary>
    /// <param name="dim">The dimension (0 for rows, 1 for columns).</param>
    /// <returns>The length of the specified dimension.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the dimension is not 0 or 1.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly int GetLength(int dim) => dim switch
    {
        0 => _data.GetLength(0),
        1 => _d1,
        _ => throw new ArgumentOutOfRangeException(nameof(dim), dim, "Must be 0 or 1"),
    };

    /// <summary>
    /// Number of 64-bit words per row -- equals <c>ceil(_d1 / 64)</c>. Useful
    /// for callers that want to walk the matrix at word granularity instead
    /// of bit granularity (e.g. for chunked sparse-skip fast paths).
    /// </summary>
    public readonly int WordsPerRow
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => _data.GetLength(1);
    }

    /// <summary>
    /// Returns the raw 64-bit word at <c>(row, wordIndex)</c>. Bit <c>k</c>
    /// of the returned word maps to column <c>wordIndex * 64 + k</c>.
    /// Lets a caller test 64 mask bits with a single load + compare, and
    /// skip the entire chunk on <c>word == 0</c> (no masked pixels in the
    /// chunk) -- much cheaper than 64 invocations of the <c>this[d0, d1]</c>
    /// indexer when masks are sparse (typical hot-pixel densities are
    /// 1 in 10k+).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly ulong GetWord(int row, int wordIndex) => _data[row, wordIndex];

    /// <summary>
    /// Clears all bits in the matrix.
    /// </summary>
    public readonly void ClearAll()
    {
        for (var i = 0; i < _data.GetLength(0); i++)
        {
            for (var j = 0; j < _data.GetLength(1); j++)
            {
                _data[i, j] = 0;
            }
        }
    }

    /// <summary>
    /// Sets all bits in the matrix.
    /// </summary>
    public readonly void SetAll()
    {
        unchecked
        {
            for (var i = 0; i < _data.GetLength(0); i++)
            {
                for (var j = 0; j < _data.GetLength(1); j++)
                {
                    _data[i, j] = (ulong)-1;
                }
            }
        }
    }

    /// <summary>
    /// ORs <paramref name="other"/> into this matrix with its top-left corner at
    /// (<paramref name="d0"/>, <paramref name="d1"/>), clipping whatever falls outside.
    /// </summary>
    /// <remarks>
    /// <para><b>It is a union, and an unset incoming bit means "nothing here", never "clear this".</b>
    /// Every caller composes overlapping stamps -- circular star masks over a whole frame -- and a
    /// circle's bounding square is mostly zeros, so assignment semantics would let each stamp punch
    /// holes in its neighbours. The general path did exactly that (<c>this[m, n] = other[i, j]</c>)
    /// while the word-aligned path ORed, and which of the two ran was decided by the target COLUMN, so
    /// the same pair of stars behaved differently depending on where in a 64-bit word they landed.
    /// That is the geometry behind the one duplicate star pair
    /// <c>FindStarsFromFitsFileTests</c> had pinned as unexplained: a later stamp cleared the centre
    /// bit of an earlier star's mask, so a halo pixel of that star triggered again and its centroid
    /// was no longer claimed.</para>
    /// <para>A NEGATIVE column offset was also mishandled on the word path: it shifted the region LEFT
    /// by the remainder of <c>-d1</c> instead of dropping the clipped-off columns, so a star mask within
    /// its own radius of the left edge landed several pixels to the RIGHT of the star. The visible
    /// source range is now computed once, up front, for both axes and both paths.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly void SetRegionClipped(int d0, int d1, in BitMatrix other)
    {
        if (ReferenceEquals(other._data, _data))
        {
            throw new ArgumentException("Cannot set clip region from the same matrix", nameof(other));
        }

        var rows = _data.GetLength(0);
        var columns = _d1;

        // The visible window in SOURCE coordinates, so both paths below agree about what is being
        // copied and neither has to reason about clipping again.
        var i0 = d0 < 0 ? -d0 : 0;
        var i1 = Math.Min(other.GetLength(0), rows - d0);
        var j0 = d1 < 0 ? -d1 : 0;
        var j1 = Math.Min(other.GetLength(1), columns - d1);

        if (i0 >= i1 || j0 >= j1)
        {
            return;
        }

        unchecked
        {
            var srcWord = DivRem(j0, out var srcBit);
            var dstWord = DivRem(d1 + j0, out var dstBit);
            var width = j1 - j0;

            // Word path: the whole visible span sits inside one source word AND one target word, so a
            // row is a single shift-and-OR. Small masks at most column offsets take this.
            if (srcBit + width <= VECTOR_SIZE && dstBit + width <= VECTOR_SIZE)
            {
                var keep = width == VECTOR_SIZE ? (ulong)-1 : (1ul << width) - 1;
                for (var i = i0; i < i1; i++)
                {
                    var bits = (other._data[i, srcWord] >> srcBit) & keep;
                    if (bits != 0)
                    {
                        _data[d0 + i, dstWord] |= bits << dstBit;
                    }
                }
            }
            else
            {
                for (var i = i0; i < i1; i++)
                {
                    var targetRow = d0 + i;
                    for (var j = j0; j < j1; j++)
                    {
                        if (other[i, j])
                        {
                            this[targetRow, d1 + j] = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns a string representation of the matrix.
    /// </summary>
    /// <returns>A string representation of the matrix.</returns>
    public override readonly string ToString()
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

                var bytes = BitConverter.GetBytes(_data[i, j]);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }

                for (var k = 0; k < VECTOR_SIZE / 8; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(Convert.ToString(bytes[k], 2).PadLeft(8, '0'));
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
