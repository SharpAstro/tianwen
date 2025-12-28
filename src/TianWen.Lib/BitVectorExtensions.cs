using System.Collections.Specialized;

namespace TianWen.Lib;

public static class BitVectorExtensions
{
    public static bool AllSet(this BitVector32 vector, int maxValue)
    {
        var mask = maxValue - 1;
        return (vector.Data & mask) == mask;
    }
}
