using System;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="SpanShould"/> stands in for <c>ShouldBe</c> on whole buffers (#2345), so it has to fail wherever that did:
/// on a changed element, a missing one and an extra one, and say where.
/// </summary>
public class SpanShouldTests
{
    [Fact]
    public void TheSameBytesInAnotherArrayPass()
    {
        new byte[] { 1, 2, 3, 250 }.ShouldBeTheSameBytesAs(new byte[] { 1, 2, 3, 250 });
        Array.Empty<byte>().ShouldBeTheSameBytesAs(ReadOnlySpan<byte>.Empty);
    }

    [Fact]
    public void AChangedByteFailsAndTheMessageNamesItsIndexBothValuesAndTheCallersWords()
    {
        var expected = new byte[4096];
        var actual = new byte[4096];
        actual[3000] = 7;

        var failure = Should.Throw<ShouldAssertException>(() => actual.ShouldBeTheSameBytesAs(expected, "tile 3"));

        failure.Message.ShouldBe("tile 3: the two are not the same (4096 elements each); the first difference is at index 3000: 7 where 0 was expected");
    }

    [Theory]
    [InlineData(3, 4, "3 elements where 4 were expected")]
    [InlineData(4, 3, "4 elements where 3 were expected")]
    public void AMissingOrAnExtraByteFailsThoughEveryByteOfTheShorterOneAgrees(int actualLength, int expectedLength, string lengths)
    {
        var failure = Should.Throw<ShouldAssertException>(
            () => new byte[actualLength].ShouldBeTheSameBytesAs(new byte[expectedLength]));

        failure.Message.ShouldBe($"the two are not the same ({lengths}); the two agree over the first 3, which is all of the shorter one");
    }

    [Fact]
    public void OtherElementTypesAreComparedTheSameWay()
    {
        ReadOnlySpan<float> plane = [0.5f, 0.25f, 0.125f];

        SpanShould.ShouldBeTheSameAs(plane, [0.5f, 0.25f, 0.125f]);
        Should.Throw<ShouldAssertException>(() => SpanShould.ShouldBeTheSameAs<float>([0.5f, 0.25f, 0.125f], [0.5f, 0.2f, 0.125f]))
            .Message.ShouldContain("index 1: 0.25 where 0.2 was expected");
    }
}
