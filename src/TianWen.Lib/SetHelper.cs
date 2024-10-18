using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Astap.Lib;

public static class SetHelper
{
    /// <summary>
    /// Creates a a new set by unifying <paramref name="this"/> with <paramref name="other"/>, making use of
    /// the efficient copy mechanism.
    /// </summary>
    /// <param name="this">HashSet that will not be modified</param>
    /// <param name="other"></param>
    /// <returns>The union with trimmed capacity</returns>
    public static IReadOnlySet<T> UnionWithAsReadOnlyCopy<T>(this HashSet<T> @this, IReadOnlySet<T> other)
    {
        var @new = new HashSet<T>(@this);
        @new.UnionWith(other);
        @new.TrimExcess();
        return @new;
    }
}
