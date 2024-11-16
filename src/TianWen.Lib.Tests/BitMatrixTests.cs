using CommunityToolkit.HighPerformance;
using Shouldly;
using System;
using Xunit;

namespace TianWen.Lib.Tests
{
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

        [Fact]
        public void GivenInvalidDimThenArgumentExceptionIsThrown()
        {
            // given
            var bm = new BitMatrix(1, 1);

            // when, then
            Should.Throw<ArgumentOutOfRangeException>(() => bm.GetLength(2));
        }

        [Theory]
        [InlineData(2, 32, 1, 1,
            "00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000010"
        )]
        [InlineData(2, 32, 0, 31,
            "10000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000"
        )]
        [InlineData(1, 32, 0, 4,
            "00000000 00000000 00000000 00010000"
        )]
        [InlineData(2, 64, 0, 44,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00010000 00000000",
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000"
        )]
        [InlineData(2, 64, 1, 15,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000",
            "00000000 00000000 10000000 00000000, 00000000 00000000 00000000 00000000"
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
        [InlineData(2, 1, "00000000 00000000 00000000 00000000", "01000000 00000000 00000000 00000000")]
        [InlineData(32, 1, "00000000 00000000 00000000 00000000", "01111111 11111111 11111111 11111111")]
        public void GivenRangeBothAreFromEndWhenUsingRangeIndexerThenBitsAreSet(int startFromEnd, int endFromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 32);

            // when
            bm[1, ^startFromEnd..^endFromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(1, 1, "00000000 00000000 00000000 00000000", "01111111 11111111 11111111 11111110")]
        [InlineData(1, 0, "00000000 00000000 00000000 00000000", "11111111 11111111 11111111 11111110")]
        [InlineData(0, 0, "00000000 00000000 00000000 00000000", "11111111 11111111 11111111 11111111")]
        public void GivenRangeEndIsFromEndWhenUsingRangeIndexerThenBitsAreSet(int start, int fromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 32);

            // when
            bm[1, start..^fromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(1, "00000000 00000000 00000000 00000000", "10000000 00000000 00000000 00000000")]
        [InlineData(2, "00000000 00000000 00000000 00000000", "01000000 00000000 00000000 00000000")]
        [InlineData(32, "00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000001")]
        public void GivenIndexIsFromEndWhenUsingIndexIndexerThenBitsAreSet(int fromEnd, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 32);

            // when
            bm[1, ^fromEnd] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(0, "00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000001")]
        [InlineData(1, "00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000010")]
        [InlineData(2, "00000000 00000000 00000000 00000000", "00000000 00000000 00000000 00000100")]
        [InlineData(31, "00000000 00000000 00000000 00000000", "10000000 00000000 00000000 00000000")]
        public void GivenIndexIsNotFromEndWhenUsingIndexIndexerThenBitsAreSet(int idx, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(2, 32);

            // when
            bm[1, new Index(idx)] = true;

            // then
            var actualRows = BitMatrixOutputAsRows(bm.ToString());
            actualRows.ShouldBe(expectedRows);
        }

        [Theory]
        [InlineData(2, 32, 1, 1, 2,
            "00000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000010"
        )]
        [InlineData(2, 32, 0, 31, 32,
            "10000000 00000000 00000000 00000000",
            "00000000 00000000 00000000 00000000"
        )]
        [InlineData(1, 32, 0, 4, 5,
            "00000000 00000000 00000000 00010000"
        )]
        [InlineData(1, 32, 0, 4, 7,
            "00000000 00000000 00000000 01110000"
        )]
        [InlineData(2, 64, 0, 44, 45,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00010000 00000000",
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000"
        )]
        [InlineData(2, 64, 1, 15, 16,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000",
            "00000000 00000000 10000000 00000000, 00000000 00000000 00000000 00000000"
        )]
        [InlineData(2, 64, 1, 15, 42,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000",
            "11111111 11111111 10000000 00000000, 00000000 00000000 00000011 11111111"
        )]
        [InlineData(2, 3 * 32, 1, 13, 75,
            "00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000, 00000000 00000000 00000000 00000000",
            "11111111 11111111 11100000 00000000, 11111111 11111111 11111111 11111111, 00000000 00000000 00000111 11111111"
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
        [InlineData(2, 32, 1, 1, 2,
             "11111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111101"
        )]
        [InlineData(2, 32, 0, 31, 32,
             "01111111 11111111 11111111 11111111",
             "11111111 11111111 11111111 11111111"
        )]
        [InlineData(1, 32, 0, 4, 5,
             "11111111 11111111 11111111 11101111"
        )]
        [InlineData(1, 32, 0, 4, 7,
             "11111111 11111111 11111111 10001111"
        )]
        [InlineData(2, 64, 0, 44, 45,
             "11111111 11111111 11111111 11111111, 11111111 11111111 11101111 11111111",
             "11111111 11111111 11111111 11111111, 11111111 11111111 11111111 11111111"
        )]
        [InlineData(2, 64, 1, 15, 16,
             "11111111 11111111 11111111 11111111, 11111111 11111111 11111111 11111111",
             "11111111 11111111 01111111 11111111, 11111111 11111111 11111111 11111111"
        )]
        [InlineData(2, 64, 1, 15, 42,
             "11111111 11111111 11111111 11111111, 11111111 11111111 11111111 11111111",
             "00000000 00000000 01111111 11111111, 11111111 11111111 11111100 00000000"
        )]
        [InlineData(2, 3 * 32, 1, 13, 75,
             "11111111 11111111 11111111 11111111, 11111111 11111111 11111111 11111111, 11111111 11111111 11111111 11111111",
             "00000000 00000000 00011111 11111111, 00000000 00000000 00000000 00000000, 11111111 11111111 11111000 00000000"
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

        private static string[] BitMatrixOutputAsRows(string stringOutput) =>
            stringOutput.Split(['\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}