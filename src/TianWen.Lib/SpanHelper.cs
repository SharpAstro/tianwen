using System;

namespace TianWen.Lib;

public static class SpanHelper
{
    public static bool StartsWithAny(this ReadOnlySpan<byte> span, ReadOnlySpan<byte> startsWith1, ReadOnlySpan<byte> startsWith2)
        => span.StartsWith(startsWith1) || span.StartsWith(startsWith2);

    public static bool StartsWithAny(this ReadOnlySpan<byte> span, ReadOnlySpan<byte> startsWith1, ReadOnlySpan<byte> startsWith2, ReadOnlySpan<byte> startsWith3)
        => span.StartsWithAny(startsWith1, startsWith2) || span.StartsWith(startsWith3);
}
