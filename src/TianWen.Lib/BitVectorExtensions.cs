using System.Collections.Specialized;

namespace TianWen.Lib;

public static class BitVectorExtensions
{
    /// <summary>
    /// True when the lowest <paramref name="bitCount"/> bits of <paramref name="vector"/> are all set.
    /// </summary>
    /// <remarks>
    /// The mask is <c>(1 &lt;&lt; bitCount) - 1</c>. It used to be <c>bitCount - 1</c>, which is a
    /// different number for every count except 2 and, worse, is ZERO for a count of one -- and
    /// <c>(Data &amp; 0) == 0</c> is unconditionally true. So the single-OTA case, which is every rig
    /// in the shipped device list and every test, answered "all set" whatever the vector held.
    /// </remarks>
    /// <param name="vector">The vector to test.</param>
    /// <param name="bitCount">How many low bits must be set (for a per-OTA flag set, the OTA count).</param>
    public static bool AllSet(this BitVector32 vector, int bitCount)
    {
        var mask = (1 << bitCount) - 1;
        return (vector.Data & mask) == mask;
    }
}
