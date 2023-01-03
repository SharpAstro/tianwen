using System.Collections.Generic;

namespace Astap.Lib;

public static class CollectionHelper
{
    public static void CopyTo<T>(this IReadOnlyList<T> @this, T[] array, int arrayIndex)
    {
        if (@this is ICollection<T> collection)
        {
            collection.CopyTo(array, arrayIndex);
        }
        else
        {
            for (var i = 0; i < @this.Count; i++)
            {
                array[arrayIndex + i] = @this[i];
            }
        }
    }

    public static void CopyTo<T>(this IReadOnlyCollection<T> @this, T[] array, int arrayIndex)
    {
        if (@this is ICollection<T> collection)
        {
            collection.CopyTo(array, arrayIndex);
        }
        else
        {
            var i = arrayIndex;
            foreach (T elem in @this)
            {
                array[i++] = elem;
            }
        }
    }

    public static int IndexOf<T>(this IReadOnlyList<T> @this, T item)
    {
        if (@this is IList<T> list)
        {
            return list.IndexOf(item);
        }
        else
        {
            for (var i = 0; i < @this.Count; i++)
            {
                if (@this[i]?.Equals(item) == true)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public static IReadOnlyList<T> ConcatToReadOnlyList<T>(T first, ICollection<T> rest)
    {
        var list = new List<T>(1 + rest.Count) { first };
        list.AddRange(rest);
        return list;
    }
}
