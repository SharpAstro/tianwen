using Shouldly;
using System;
using Xunit;

namespace Astap.Lib.Tests
{
    public class BitMatrixTests
    {
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
            var actualRows = bm.ToString().Split('\n', '\r', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
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
        public void GivenXYWhenSetUsingRangeIndexerThenBitsAreSet(int d0, int d1, int x, int y1, int y2, params string[] expectedRows)
        {
            // given
            var bm = new BitMatrix(d0, d1);

            // when
            bm[x, y1..y2] = true;

            // then
            var actualRows = bm.ToString().Split('\n', '\r', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            actualRows.ShouldBe(expectedRows);
        }
    }
}
