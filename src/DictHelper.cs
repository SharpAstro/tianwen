using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public static class DictHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, (TVal v1, TVal v2)> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
        where TVal : struct
    {
        if (!lookupTable.TryAdd(master, (toAdd, default)))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (T v1, T v2) AddElementIfNotExist<T>(in (T v1, T v2) existingPair, T toAdd)
        where T : struct => existingPair.v1.Equals(toAdd) || existingPair.v2.Equals(toAdd)
        ? existingPair
        : existingPair.v2.Equals(default(T))
            ? (existingPair.v1, toAdd)
            : throw new ArgumentException($"Cannot add {toAdd} to pair {existingPair} as it contains already two elements", nameof(existingPair));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
