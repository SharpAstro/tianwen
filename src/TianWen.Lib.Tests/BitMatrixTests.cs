using CommunityToolkit.HighPerformance;
using Shouldly;
using System;
using Xunit;

namespace TianWen.Lib.Tests
{
    [Collection("Imaging")]
    public class BitMatrixTests
    {
        [Theory]
        [InlineData(2, 3, 3, 2)]
        [InlineData(3, 2, 2, 3)]
        public void GivenInvalidXYWhenIndexingThenAnExceptionIsThrown(int d0, int d1, int x, int y)
        {
            // given
            var bm = new BitMatrix(d0, d1);

            // when, then
            Should.Throw<IndexOutOfRangeException>(() => bm[x, y]);
            Should.Throw<IndexOutOfRangeException>(() => bm[x, y] = true);
        }

        [Theory]
        [InlineData(2, 3)]
        [InlineData(3, 2)]
        public void GivenDimsWhenGetLengthThenItIsReturned(int d0, int d1)
        {
            // given
            var bm = new BitMatrix(d0, d1);

            // when
            var d0Len = bm.GetLength(0);
            var d1Len = bm.GetLength(1);

            // then
            d0Len.ShouldBe(d0);
            d1Len.ShouldBe(d1);
        }

        [Theory]
        [InlineData(1,    1)]   // 1 column fits in 1 word
        [InlineData(63,   1)]   // 63 columns still in 1 word
        [InlineData(64,   1)]   // exactly 1 word
        [InlineData(65,   2)]   // spans 2 words
        [InlineData(128,  2)]   // exactly 2 words
        [InlineData(1000, 16)]  // 1000 / 64 = 15.625 -> 16 words
        public void GivenColumnCountWhenWordsPerRowThenCeilDiv64(int d1, int expectedWords)
        {
            // given
            var bm = new BitMatrix(d0: 3, d1: d1);

            // when, then
            bm.WordsPerRow.ShouldBe(expectedWords);
        }

        [Fact]
        public void GivenSetBitWhenGetWordThenBitIsInWordAtCorrectOffset()
        {
            // given -- set a few representative bits across word boundaries
            var bm = new BitMatrix(d0: 2, d1: 130);
            bm[0,   0] = true;   // bit 0 of word 0
            bm[0,  63] = true;   // bit 63 of word 0 (high bit)
            bm[0,  64] = true;   // bit 0 of word 1
            bm[0, 129] = true;   // bit 1 of word 2 (last spanning word)
            // row 1 stays all-zero -- verifies GetWord returns 0 when no bit set

            // when
            var row0Word0 = bm.GetWord(row: 0, wordIndex: 0);
            var row0Word1 = bm.GetWord(row: 0, wordIndex: 1);
            var row0Word2 = bm.GetWord(row: 0, wordIndex: 2);
            var row1Word0 = bm.GetWord(row: 1, wordIndex: 0);

            // then
            row0Word0.ShouldBe((1UL << 0) | (1UL << 63));
            row0Word1.ShouldBe(1UL << 0);
            row0Word2.ShouldBe(1UL << 1);
            row1Word0.ShouldBe(0UL);
        }

        [Fact]
        public void GivenSparseMaskWhenWordZeroThenChunkIsAllUnsetForFastPath()
        {
            // The drizzle hot-path optimisation reads GetWord per 64-pixel
            // chunk; word == 0 means "no masked pixels in this chunk, skip
            // the per-bit test". This test guards the contract: a chunk
            // worth of all-clear bits MUST surface as word == 0 so the
            // fast path actually fires.

            // given
            var bm = new BitMatrix(d0: 1, d1: 128);
            bm[0, 100] = true; // only one bit set, lives in word 1 not word 0

            // when, then
            bm.GetWord(0, 0).ShouldBe(0UL,
                "first 64 columns are clear -- fast path must trigger");
            bm.GetWord(0, 1).ShouldNotBe(0UL,
                "bit 100 lives in word 1 -- slow path must trigger");
        }

        [Fact]
        public void GivenInvalidDimThenArgumentExceptionIsThrown()
        {
            // given
            var bm = new BitMatrix(1, 1);

            // when, then
            Should.Throw<ArgumentOutOfRangeException>(() => bm.GetLength(2));
        }

        [Theory]
        [InlineData(2, 64, 1, 1,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000010"
        )]
        [InlineData(2, 64, 0, 63,
            "10000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"
        )]
        [InlineData(1, 64, 0, 4,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00010000"
        )]
        [InlineData(2, 64, 0, 44,
            "00000000 00000000 00010000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"
        )]
        [InlineData(2, 64, 1, 15,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 10000000 00000000"
        )]
        public void GivenXYWhenSetUsingBitIndexerThenBitIsSet(int d0, int d1, int x, int y, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(d0, d1);

            // when
            bm[x, y] = true;

            // then
            var stringOutput = bm.ToString();
            var actualRows = BitMatrixOutputAsRows(stringOutput);
            stringOutput.Count('1').ShouldBe(1);
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(2, 1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "01000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000")]
        [InlineData(32, 1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "01111111 11111111 11111111 11111111 00000000 00000000 00000000 00000000")]
        [InlineData(33, 1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "01111111 11111111 11111111 11111111 10000000 00000000 00000000 00000000")]
        public void GivenRangeBothAreFromEndWhenUsingRangeIndexerThenBitsAreSet(int startFromEnd, int endFromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 64);

            // when
            bm[1, ^startFromEnd..^endFromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(1, 1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "01111111 11111111 11111111 11111111 11111111 11111111 11111111 11111110")]
        [InlineData(1, 0, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111110")]
        [InlineData(0, 0, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111")]
        public void GivenRangeEndIsFromEndWhenUsingRangeIndexerThenBitsAreSet(int start, int fromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 64);

            // when
            bm[1, start..^fromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "10000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000")]
        [InlineData(2, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "01000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000")]
        [InlineData(64, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000001")]
        public void GivenIndexIsFromEndWhenUsingIndexIndexerThenBitsAreSet(int fromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 64);

            // when
            bm[1, ^fromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(0, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000001")]
        [InlineData(1, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000010")]
        [InlineData(2, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000100")]
        [InlineData(63, "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000", "10000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000")]
        public void GivenIndexIsNotFromEndWhenUsingIndexIndexerThenBitsAreSet(int idx, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 64);

            // when
            bm[1, new Index(idx)] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(2, 64, 1, 1, 2,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000010"
        )]
        [InlineData(2, 64, 0, 63, 64,
            "10000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"
        )]
        [InlineData(1, 64, 0, 4, 5,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00010000"
        )]
        [InlineData(1, 64, 0, 4, 7,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 01110000"
        )]
        [InlineData(1, 64, 0, 44, 45,
            "00000000 00000000 00010000 00000000 00000000 00000000 00000000 00000000"
        )]
        [InlineData(2, 64, 1, 15, 16,
            "00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000 00000000 00000000 10000000 00000000"
        )]
        public void GivenXYWhenSetUsingRangeIndexerThenBitsAreSet(int d0, int d1, int x, int y1, int y2, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(d0, d1);

            // when
            bm[x, y1..y2] = true;

            // then
            var stringOutput = bm.ToString();
            string[] actualRows = BitMatrixOutputAsRows(stringOutput);
            stringOutput.Count('1').ShouldBe(y2 - y1);
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(2, 64, 1, 1, 2,
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111101"
        )]
        [InlineData(2, 64, 0, 31, 32,
             "11111111 11111111 11111111 11111111 01111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111"
        )]
        [InlineData(1, 64, 0, 4, 5,
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11101111"
        )]
        [InlineData(1, 64, 0, 4, 7,
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 10001111"
        )]
        [InlineData(2, 64, 0, 44, 45,
             "11111111 11111111 11101111 11111111 11111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111"
        )]
        [InlineData(2, 64, 1, 15, 16,
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111111 11111111 11111111 01111111 11111111"
        )]
        [InlineData(2, 64, 1, 15, 42,
             "11111111 11111111 11111111 11111111 11111111 11111111 11111111 11111111",
             "11111111 11111111 11111100 00000000 00000000 00000000 01111111 11111111"
        )]
        public void GivenXYWhenClearUsingRangeIndexerThenBitsAreCleared(int d0, int d1, int x, int y1, int y2, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(d0, d1);
            bm.SetAll();

            // when
            bm[x, y1..y2] = false;

            // then
            var stringOutput = bm.ToString();
            var actualRows = BitMatrixOutputAsRows(stringOutput);
            stringOutput.Count('0').ShouldBe(y2 - y1);
            actualRows.ShouldBe(expectedRows);
        }

        /// <summary>
        /// Stamping a region ADDS to what is already there. Every caller composes a union of
        /// overlapping stamps (star masks), so an unset bit in the incoming region must leave the
        /// target alone rather than clear it.
        /// </summary>
        /// <remarks>
        /// <para>The two implementations disagreed, and only one of them was ever exercised. The
        /// word-aligned fast path ORs (<c>|=</c>); the general path assigned
        /// (<c>this[m, n] = other[i, j]</c>), so an incoming zero CLEARED a bit some earlier stamp had
        /// set. A circular star mask is mostly zeros -- the four corners of its bounding square are
        /// always unset -- so a star stamped near an already-marked one punched holes in it.</para>
        /// <para>Which path runs is decided by <c>d1Rem + width &lt;= 64</c>, i.e. by the target COLUMN
        /// the stamp lands on, so the theory rows walk a stamp across a word boundary: the low offsets
        /// take the fast path and were correct, and the high ones take the general path and were not.
        /// Nothing covered <c>SetRegionClipped</c> at all before this, which is how a two-line
        /// divergence in one method survived.</para>
        /// </remarks>
        [Theory]
        [InlineData(0)]    // fast path (word-aligned, 0 + 5 <= 64)
        [InlineData(30)]   // fast path, mid-word
        [InlineData(59)]   // fast path, last offset that still fits one word
        [InlineData(60)]   // general path: 60 + 5 > 64, the stamp straddles two words
        [InlineData(62)]
        [InlineData(63)]
        public void GivenOverlappingRegionsWhenSetRegionClippedThenEarlierBitsSurvive(int column)
        {
            // given -- a solid 5x5 already marked, then a holed 5x5 stamped exactly over it
            const int size = 5;
            const int row = 4;
            var bm = new BitMatrix(16, 128);

            var solid = new BitMatrix(size, size);
            var holed = new BitMatrix(size, size);
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    solid[i, j] = true;
                    // A plus sign: the corners are unset, exactly like a circular star mask's.
                    holed[i, j] = i == size / 2 || j == size / 2;
                }
            }

            bm.SetRegionClipped(row, column, solid);

            // when
            bm.SetRegionClipped(row, column, holed);

            // then -- the union, so all 25 bits are still set
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    bm[row + i, column + j].ShouldBeTrue($"bit ({row + i}, {column + j}) was cleared by the second stamp");
                }
            }
        }

        /// <summary>
        /// The two paths must also agree about WHAT they stamp, not merely about not clearing: the
        /// same region written at a word-aligned column and at a straddling one produces the same
        /// pattern, offset by the column.
        /// </summary>
        [Fact]
        public void GivenTheSameRegionWhenStampedAcrossAWordBoundaryThenThePatternIsUnchanged()
        {
            // given
            const int size = 5;
            var region = new BitMatrix(size, size);
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    region[i, j] = (i * size + j) % 3 == 0;
                }
            }

            var aligned = new BitMatrix(16, 128);
            var straddling = new BitMatrix(16, 128);

            // when -- 0 takes the fast path, 62 straddles the 64-bit word boundary
            aligned.SetRegionClipped(4, 0, region);
            straddling.SetRegionClipped(4, 62, region);

            // then
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    straddling[4 + i, 62 + j].ShouldBe(aligned[4 + i, j], $"({i}, {j}) differs between the two paths");
                }
            }
        }

        /// <summary>
        /// A stamp hanging off the top or left edge keeps only its visible part, on both paths.
        /// </summary>
        [Theory]
        [InlineData(-2, 0)]
        [InlineData(-2, 62)]
        [InlineData(0, -2)]
        [InlineData(14, 62)]
        public void GivenARegionHangingOffTheEdgeWhenSetRegionClippedThenOnlyVisibleBitsAreSet(int row, int column)
        {
            // given
            const int size = 5;
            const int rows = 16;
            const int columns = 128;
            var region = new BitMatrix(size, size);
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    region[i, j] = true;
                }
            }
            var bm = new BitMatrix(rows, columns);

            // when
            bm.SetRegionClipped(row, column, region);

            // then -- exactly the in-bounds intersection is set
            var expected = 0;
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j < size; j++)
                {
                    if (row + i >= 0 && row + i < rows && column + j >= 0 && column + j < columns)
                    {
                        bm[row + i, column + j].ShouldBeTrue($"visible bit ({row + i}, {column + j}) was not set");
                        expected++;
                    }
                }
            }

            var set = 0;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    if (bm[r, c])
                    {
                        set++;
                    }
                }
            }
            set.ShouldBe(expected);
        }

        private static string[] BitMatrixOutputAsRows(string stringOutput) =>
            stringOutput.Split(['\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}