﻿using System.Collections.Generic;

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
}