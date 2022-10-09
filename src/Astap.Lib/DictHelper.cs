using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public static class DictHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, (TVal v1, TVal[]? ext)> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
        where TVal : struct
    {
        if (!lookupTable.TryAdd(master, (toAdd, null)))
        {
            lookupTable[master] = AddElementIfNotExist(lookupTable[master], toAdd);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, TVal[]> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
    {
        if (!lookupTable.TryAdd(master, new[] { toAdd }))
        {
            lookupTable[master] = AddElementIfNotExist(lookupTable[master], toAdd);
        }
    }

    private static (T v1, T[]? ext) AddElementIfNotExist<T>(in (T v1, T[]? ext) existingPair, T toAdd)
        where T : struct => existingPair.v1.Equals(toAdd) || existingPair.ext?.Contains(toAdd) == true
        ? existingPair
        : existingPair.ext == null
            ? (existingPair.v1, new[] { toAdd })
            : (existingPair.v1, AddElementIfNotExist(existingPair.ext, toAdd));

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
