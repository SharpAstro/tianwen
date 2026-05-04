using Shouldly;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

public class AsciiRecordReaderTests
{
    private const byte GS = 0x1D;
    private const byte RS = 0x1E;
    private const byte US = 0x1F;

    [Fact]
    public void GivenSingleRecordWhenEnumeratingThenYieldsThatRecord()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");

        var records = AsciiRecordReader.EnumerateRecords(bytes).ToList();

        records.Count.ShouldBe(1);
        Encoding.UTF8.GetString(records[0].Span).ShouldBe("hello");
    }

    [Fact]
    public void GivenMultipleRecordsWhenEnumeratingThenYieldsEachRecordWithoutTheSeparator()
    {
        // a␝b␝c — three records separated by GS (no trailing GS).
        var raw = Encoding.UTF8.GetBytes("a").Concat(new[] { GS })
            .Concat(Encoding.UTF8.GetBytes("bb")).Concat(new[] { GS })
            .Concat(Encoding.UTF8.GetBytes("ccc")).ToArray();

        var records = AsciiRecordReader.EnumerateRecords(raw)
            .Select(m => Encoding.UTF8.GetString(m.Span)).ToList();

        records.ShouldBe(new[] { "a", "bb", "ccc" });
    }

    [Fact]
    public void GivenFieldsWhenTakingThenAdvancesPastSeparator()
    {
        var raw = Encoding.UTF8.GetBytes("alpha")
            .Concat(new[] { RS })
            .Concat(Encoding.UTF8.GetBytes("beta"))
            .Concat(new[] { RS })
            .Concat(Encoding.UTF8.GetBytes("gamma")).ToArray();

        ReadOnlySpan<byte> span = raw;

        AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref span)).ShouldBe("alpha");
        AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref span)).ShouldBe("beta");
        AsciiRecordReader.ReadString(AsciiRecordReader.TakeField(ref span)).ShouldBe("gamma");
        span.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void GivenG17FormattedDoubleWhenParsingThenRoundTripsExactly()
    {
        // Pick values that exercise the G17 round-trip contract: subnormals, irrational binary
        // fractions, common astro magnitudes.
        var samples = new[] { 0.0, 1.0, -1.0, 0.1, 1.0 / 3.0, Math.PI, double.Epsilon, 1e308, -1.4e-305, 24.0, 359.99999999999997 };
        foreach (var v in samples)
        {
            var encoded = v.ToString("G17", CultureInfo.InvariantCulture);
            var parsed = AsciiRecordReader.ReadDouble(Encoding.UTF8.GetBytes(encoded));
            parsed.ShouldBe(v); // exact bit-for-bit match
        }
    }

    [Fact]
    public void GivenEmptyNullableDoubleFieldWhenParsingThenReturnsNull()
    {
        AsciiRecordReader.ReadNullableDouble([]).ShouldBeNull();
        AsciiRecordReader.ReadNullableDouble(Encoding.UTF8.GetBytes("3.14")).ShouldBe(3.14);
    }

    [Fact]
    public void GivenUnitSeparatedListWhenReadingThenReturnsEachItem()
    {
        var raw = Encoding.UTF8.GetBytes("HD 12345")
            .Concat(new[] { US })
            .Concat(Encoding.UTF8.GetBytes("HIP 6789"))
            .Concat(new[] { US })
            .Concat(Encoding.UTF8.GetBytes("* alf Cen")).ToArray();

        AsciiRecordReader.ReadStringArray(raw).ShouldBe(new[] { "HD 12345", "HIP 6789", "* alf Cen" });
    }

    [Fact]
    public void GivenEmptySubArrayWhenReadingThenReturnsEmptyArray()
    {
        AsciiRecordReader.ReadStringArray([]).ShouldBeEmpty();
    }

    [Fact]
    public void GivenSingleItemWithNoSeparatorWhenReadingArrayThenYieldsSingleton()
    {
        AsciiRecordReader.ReadStringArray(Encoding.UTF8.GetBytes("HD 12345")).ShouldBe(new[] { "HD 12345" });
    }
}
