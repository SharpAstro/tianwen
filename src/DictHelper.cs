using System;
using System.Collections.Generic;
using System.Linq;

namespace Astap.Lib;

public static class DictHelper
{
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, TVal[]> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
    {
        if (!lookupTable.TryAdd(master, new[] { toAdd }))
        {
            lookupTable[master] = AddElementIfNotExist(lookupTable[master], toAdd);
        }
    }

    private static T[] AddElementIfNotExist<T>(T[] existingArray, T toAdd)
    {
        if (existingArray.Contains(toAdd))
        {
            return existingArray;
        }

        var newArray = new T[existingArray.Length + 1];
        Array.Copy(existingArray, newArray, existingArray.Length);
        newArray[^1] = toAdd;
        return newArray;
    }
}
