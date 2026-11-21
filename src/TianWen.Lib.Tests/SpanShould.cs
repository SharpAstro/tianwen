using System;
using Shouldly;

namespace TianWen.Lib.Tests;

/// <summary>
/// Whole-buffer comparisons for tests: two files' bytes, two planes. One vectorised pass, and a message built only when
/// the two differ, which names the first place they do.
/// </summary>
/// <remarks>
/// Shouldly's <c>ShouldBe</c> on two arrays walks them as sequences and boxes every element on the way, so comparing two
/// files of a few megabytes allocated more than the files: about a sixth of what <c>DatasetDegradationExporterTests</c>
/// allocated was boxed bytes and ints from ten such lines (#2345, measured with the test cost probe of #2346).
/// And when the two do differ its message prints the arrays, not where they part. Keep <c>ShouldBe</c> for a handful of
/// values; use this for a buffer.
/// </remarks>
internal static class SpanShould
{
    /// <summary>Passes when <paramref name="actual"/> holds the same bytes as <paramref name="expected"/>, in order and in number.</summary>
    public static void ShouldBeTheSameBytesAs(this byte[] actual, ReadOnlySpan<byte> expected, string? customMessage = null)
        => ShouldBeTheSameAs<byte>(actual, expected, customMessage);

    /// <summary>Passes when the two spans hold the same elements, in order and in number.</summary>
    public static void ShouldBeTheSameAs<T>(ReadOnlySpan<T> actual, ReadOnlySpan<T> expected, string? customMessage = null)
        where T : IEquatable<T>
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new ShouldAssertException(Difference(actual, expected, customMessage));
        }
    }

    /// <summary>What a failure says: the caller's message, the two lengths, and the first index at which they part.</summary>
    internal static string Difference<T>(ReadOnlySpan<T> actual, ReadOnlySpan<T> expected, string? customMessage)
        where T : IEquatable<T>
    {
        var common = actual.CommonPrefixLength(expected);
        var lengths = actual.Length == expected.Length
            ? $"{actual.Length} elements each"
            : $"{actual.Length} elements where {expected.Length} were expected";
        var where = common < actual.Length && common < expected.Length
            ? $"the first difference is at index {common}: {actual[common]} where {expected[common]} was expected"
            : $"the two agree over the first {common}, which is all of the shorter one";
        return $"{(customMessage is null ? "" : customMessage + ": ")}the two are not the same ({lengths}); {where}";
    }
}
