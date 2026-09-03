using Shouldly;
using System.Collections.Specialized;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="BitVectorExtensions.AllSet"/> answers "are the lowest N bits all set", and it is the
/// per-OTA frame gate in <c>Session.ImagingLoopAsync</c>. It used to mask on <c>bitCount - 1</c>,
/// which is a different number for every count except 2 and, decisively, is ZERO for a count of one:
/// <c>(Data &amp; 0) == 0</c> is unconditionally true, so the single-OTA case -- every rig in the
/// shipped device list and every test -- answered "all set" whatever the vector held. The
/// <c>bitCount: 1</c> rows below are the ones that were wrong; they are worth keeping first in mind
/// when reading this file, because every other row passed against the broken version too.
/// </summary>
public class BitVectorExtensionsTests
{
    [Theory]
    // One OTA. Data 0 answering TRUE here was the bug; nothing else in the suite could see it.
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]  // the wrong bit set is not "all set"
    [InlineData(3, 1, true)]   // extra high bits are ignored
    // Two OTAs.
    [InlineData(0, 2, false)]
    [InlineData(1, 2, false)]  // only the first reported in
    [InlineData(2, 2, false)]  // only the second reported in
    [InlineData(3, 2, true)]
    [InlineData(7, 2, true)]
    // Three, where `bitCount - 1` (= 2) and `(1 << bitCount) - 1` (= 7) disagree most visibly.
    [InlineData(3, 3, false)]
    [InlineData(6, 3, false)]
    [InlineData(7, 3, true)]
    public void AllSet_IsTrueOnlyWhenEveryLowBitIsSet(int data, int bitCount, bool expected)
        => new BitVector32(data).AllSet(bitCount).ShouldBe(expected);

    /// <summary>
    /// The shape the imaging loop uses: a flag per OTA, addressed by MASK. <c>BitVector32</c>'s
    /// <c>int</c> indexer takes a bit mask, not an index, so the loop must write <c>1 &lt;&lt; i</c>
    /// -- writing <c>i</c> makes the first OTA mask 0, which reads false forever and writes nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AFlagPerOtaReadsBackOnlyOnceEveryOtaHasReportedIn(int scopes)
    {
        var flags = new BitVector32(0);
        for (var i = 0; i < scopes; i++)
        {
            flags[1 << i] = false;
        }

        flags.AllSet(scopes).ShouldBeFalse("no OTA has reported a frame yet");

        for (var i = 0; i < scopes; i++)
        {
            flags.AllSet(scopes).ShouldBeFalse($"only {i} of {scopes} OTAs have reported in");
            flags[1 << i] = true;
            flags[1 << i].ShouldBeTrue("a flag written by mask must read back by the same mask");
        }

        flags.AllSet(scopes).ShouldBeTrue("every OTA has now reported a frame");
    }
}
