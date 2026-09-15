using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace TianWen.Lib;

/// <summary>
/// Represents a matrix of bits.
/// </summary>
/// <remarks>
/// <para><b>Use it a WORD at a time wherever the work is bulk, and a bit at a time only where the work
/// genuinely is.</b> That is the whole performance story of this type, and the gap is three orders of
/// magnitude: setting 9.1M bits (3025 x 3024) costs <b>16.5 ms</b> through the indexer and <b>under
/// 0.05 ms</b> through <see cref="RowWords"/>, because a bit write is a read-modify-write of a word
/// with a shift and a mask where a word write is a store. <see cref="ClearAll"/> is 0.015 ms and
/// <see cref="PopCount()"/> 0.31 ms over the same plane, both for the same reason.</para>
/// <para><b>And it does not replace a small <c>bool[]</c> in a write-hot loop.</b> Measured on the
/// classify pass of <c>Image.ScanAbsence</c>, whose two per-row scratch buffers are written up to once
/// per pixel per channel: <c>bool[3024]</c> takes it 28.1 ms and a <c>BitMatrix</c> of one row takes it
/// 72.5 ms. Bit packing buys memory traffic, and there is none to buy back when the buffer already fits
/// in L1 -- 3 KB as bytes against 378 bytes as bits. Pack the planes, not the scratch.</para>
/// <para>The backing is a FLAT <c>ulong[]</c> with an explicit row stride rather than a
/// <c>ulong[,]</c>, so that a row can be handed out as a <see cref="Span{T}"/> and so that indexing is
/// the shape the JIT optimises best.</para>
/// </remarks>
public readonly struct BitMatrix
{
    const int VECTOR_SIZE = 64;
    const int VECTOR_SIZE_SHIFT = 6;
    const int VECTOR_SIZE_MASK = VECTOR_SIZE - 1;

    // FLAT, with an explicit row stride, rather than ulong[,]. A multi-dimensional array costs a
    // multiply plus bounds checks the JIT does not reliably elide, on a type whose whole point is to be
    // cheaper than the bool plane it replaces; a 1D array indexed by an offset is the shape it
    // optimises best, and it is what lets a row be handed out as a Span for word-level work.
    private readonly ulong[] _words;
    private readonly int _d0;
    private readonly int _d1;
    private readonly int _wordsPerRow;

    /// <summary>
    /// Initializes a new instance of the <see cref="BitMatrix"/> struct.
    /// </summary>
    /// <param name="d0">The number of rows.</param>
    /// <param name="d1">The number of columns.</param>
    public BitMatrix(int d0, int d1)
    {
        var div = DivRem(_d1 = d1, out var rem);
        _d0 = d0;
        _wordsPerRow = div + (rem > 0 ? 1 : 0);
        _words = new ulong[(long) d0 * _wordsPerRow <= int.MaxValue ? d0 * _wordsPerRow : throw new ArgumentOutOfRangeException(nameof(d0))];
    }

    /// <summary>Index of the word holding <paramref name="row"/>'s <paramref name="word"/>-th word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private readonly int WordIndex(int row, int word) => (row * _wordsPerRow) + word;

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
            return (_words[WordIndex(d0, d1div)] & shift) == shift;
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
                _words[WordIndex(d0, d1div)] |= shift;
            }
            else
            {
                _words[WordIndex(d0, d1div)] &= ~shift;
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
                var startData = _words[WordIndex(d0, d1StartDiv)];
                var shiftedStartMask = setMask << d1StartRem;
                var shiftedEndMask = setMask >> (VECTOR_SIZE - d1EndRem - 1);

                if (d1StartDiv == d1EndDiv)
                {
                    var capMask = shiftedEndMask & shiftedStartMask;
                    _words[WordIndex(d0, d1StartDiv)] = value ? startData | capMask : startData & ~capMask;
                }
                else
                {
                    var d1Div = d1StartDiv;
                    _words[WordIndex(d0, d1Div++)] = value ? startData | shiftedStartMask : startData & ~shiftedStartMask;

                    var midData = value ? setMask : 0ul;
                    for (; d1Div < d1EndDiv; d1Div++)
                    {
                        _words[WordIndex(d0, d1Div)] = midData;
                    }

                    var endData = _words[WordIndex(d0, d1Div)];
                    _words[WordIndex(d0, d1Div)] = value ? endData | shiftedEndMask : endData & ~shiftedEndMask;
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
        0 => _d0,
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
        get => _wordsPerRow;
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
    public readonly ulong GetWord(int row, int wordIndex) => _words[WordIndex(row, wordIndex)];

    /// <summary>
    /// Clears all bits in the matrix.
    /// </summary>
    public readonly void ClearAll() => Array.Clear(_words);

    /// <summary>
    /// Sets all bits in the matrix.
    /// </summary>
    public readonly void SetAll() => Array.Fill(_words, ulong.MaxValue);

    /// <summary>
    /// The words of one row, to read or write directly. Bit <c>k</c> of word <c>w</c> is column
    /// <c>w * 64 + k</c>, and the span is exactly <see cref="WordsPerRow"/> long.
    /// </summary>
    /// <remarks>
    /// <b>The point of the type, for anything that touches more than a handful of bits.</b> A whole row
    /// ORs, masks or clears in <c>ceil(columns / 64)</c> operations instead of one per column, and the
    /// JIT vectorises the obvious loops over it. Two cautions: the bits past the last column are
    /// padding and a caller that writes words must leave them clear (see <see cref="ClearPadding"/>),
    /// and nothing here bounds-checks the row.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public readonly Span<ulong> RowWords(int row) => _words.AsSpan(row * _wordsPerRow, _wordsPerRow);

    /// <summary>All the words, row-major, <see cref="WordsPerRow"/> per row.</summary>
    public readonly Span<ulong> AllWords
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => _words.AsSpan();
    }

    /// <summary>
    /// Clears the padding bits past the last column in every row, which a caller that has written whole
    /// words may have set. Every counting or scanning method here assumes they are clear.
    /// </summary>
    public readonly void ClearPadding()
    {
        var rem = _d1 & VECTOR_SIZE_MASK;
        if (rem == 0 || _wordsPerRow == 0)
        {
            return;
        }

        var mask = (1ul << rem) - 1;
        for (var row = 0; row < _d0; row++)
        {
            _words[WordIndex(row, _wordsPerRow - 1)] &= mask;
        }
    }

    /// <summary>How many bits are set, over the whole matrix.</summary>
    /// <remarks>
    /// One <c>POPCNT</c> per 64 columns rather than a test per column, which is the difference between
    /// counting a 9 megapixel plane in microseconds and in milliseconds. Padding bits are assumed clear.
    /// </remarks>
    public readonly int PopCount()
    {
        var total = 0;
        foreach (var w in _words)
        {
            total += BitOperations.PopCount(w);
        }

        return total;
    }

    /// <summary>How many bits are set in one row.</summary>
    public readonly int PopCount(int row)
    {
        var total = 0;
        foreach (var w in RowWords(row))
        {
            total += BitOperations.PopCount(w);
        }

        return total;
    }

    /// <summary>Whether any bit is set at all, stopping at the first word that has one.</summary>
    public readonly bool Any()
    {
        foreach (var w in _words)
        {
            if (w != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The column of the next set bit in <paramref name="row"/> at or after <paramref name="column"/>,
    /// or -1 when the row has none left.
    /// </summary>
    /// <remarks>
    /// <b>Skips 64 columns per test where the row is empty</b>, which is what makes walking a sparse
    /// plane proportional to the bits that are SET rather than to the pixels that might have been. A
    /// drizzle hole census is 0.02 percent of a frame; a per-column loop pays for the other 99.98.
    /// <c>TZCNT</c> then names the bit without a search.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public readonly int NextSetBit(int row, int column)
    {
        if (column < 0)
        {
            column = 0;
        }

        if (column >= _d1)
        {
            return -1;
        }

        var w = DivRem(column, out var bit);
        var word = _words[WordIndex(row, w)] & (ulong.MaxValue << bit);
        while (true)
        {
            if (word != 0)
            {
                var found = (w << VECTOR_SIZE_SHIFT) + BitOperations.TrailingZeroCount(word);
                return found < _d1 ? found : -1;
            }

            if (++w >= _wordsPerRow)
            {
                return -1;
            }

            word = _words[WordIndex(row, w)];
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
    /// <summary>
    /// Dilates every set bit into the (2r+1)-square round it, in place, on the words: r shift-by-one
    /// passes each way with the carry across words, then an OR of the rows in the window. O(rows x
    /// words x r), no per-bit work and no boolean plane, which is why the source masks use it (a bool
    /// plane per mask cost 90 MB on a 16 Mpx frame). Padding bits past the last column stay clear.
    /// </summary>
    internal readonly void DilateSquare(int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        var rows = _d0;
        var words = _wordsPerRow;
        if (rows == 0 || words == 0)
        {
            return;
        }

        var padRem = _d1 & VECTOR_SIZE_MASK;
        var lastMask = padRem == 0 ? ulong.MaxValue : (1ul << padRem) - 1;
        var horizontal = new ulong[rows * words];
        var left = new ulong[words];
        var right = new ulong[words];
        for (var y = 0; y < rows; y++)
        {
            for (var w = 0; w < words; w++)
            {
                var v = _words[WordIndex(y, w)];
                horizontal[(y * words) + w] = v;
                left[w] = v;
                right[w] = v;
            }

            for (var s = 0; s < radius; s++)
            {
                // Toward higher columns: bit k moves to k+1, the top bit of word w-1 into the bottom of w.
                for (var w = words - 1; w >= 0; w--)
                {
                    left[w] = (left[w] << 1) | (w > 0 ? left[w - 1] >> (VECTOR_SIZE - 1) : 0ul);
                }

                // Toward lower columns.
                for (var w = 0; w < words; w++)
                {
                    right[w] = (right[w] >> 1) | (w + 1 < words ? right[w + 1] << (VECTOR_SIZE - 1) : 0ul);
                }

                for (var w = 0; w < words; w++)
                {
                    horizontal[(y * words) + w] |= left[w] | right[w];
                }
            }

            horizontal[(y * words) + words - 1] &= lastMask;
        }

        for (var y = 0; y < rows; y++)
        {
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(rows - 1, y + radius);
            for (var w = 0; w < words; w++)
            {
                var acc = 0ul;
                for (var yy = y0; yy <= y1; yy++)
                {
                    acc |= horizontal[(yy * words) + w];
                }

                _words[WordIndex(y, w)] = acc;
            }
        }
    }

    public readonly void SetRegionClipped(int d0, int d1, in BitMatrix other)
    {
        if (ReferenceEquals(other._words, _words))
        {
            throw new ArgumentException("Cannot set clip region from the same matrix", nameof(other));
        }

        var rows = _d0;
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
                    var bits = (other._words[other.WordIndex(i, srcWord)] >> srcBit) & keep;
                    if (bits != 0)
                    {
                        _words[WordIndex(d0 + i, dstWord)] |= bits << dstBit;
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
        for (var i = 0; i < _d0; i++)
        {
            for (var j = 0; j < _wordsPerRow; j++)
            {
                if (j > 0)
                {
                    sb.Append(", ");
                }

                var bytes = BitConverter.GetBytes(_words[WordIndex(i, j)]);
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
