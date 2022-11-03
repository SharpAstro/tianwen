using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public static class DictHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, (TVal v1, TVal[]? ext)> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
        where TVal : struct
    {
        if (!lookupTable.TryAdd(master, (toAdd, null)))
        {
            lookupTable[master] = AddElementIfNotExist(lookupTable[master], toAdd);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void AddLookupEntry<TKey, TVal>(this Dictionary<TKey, TVal[]> lookupTable, TKey master, TVal toAdd)
        where TKey : notnull
    {
        if (!lookupTable.TryAdd(master, new[] { toAdd }))
        {
            lookupTable[master] = AddElementIfNotExist(lookupTable[master], toAdd);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetLookupEntries<TKey, TVal>(this Dictionary<TKey, (TVal v1, TVal[]? ext)> lookupTable, TKey key, out IReadOnlyList<TVal> crossIndices)
        where TKey : struct, Enum
        where TVal : struct, Enum
    {
        if (lookupTable.TryGetValue(key, out var lookedUpValues))
        {
            var outputValues = new TVal[(lookedUpValues.v1.Equals(default) ? 0 : 1) + (lookedUpValues.ext?.Length ?? 0)];
            if (outputValues.Length == 0)
            {
                crossIndices = outputValues;
                return false;
            }

            outputValues[0] = lookedUpValues.v1;
            lookedUpValues.ext?.CopyTo(outputValues, 1);
            crossIndices = outputValues;
            return true;
        }

        crossIndices = Array.Empty<TVal>();
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static (T v1, T[]? ext) AddElementIfNotExist<T>(in (T v1, T[]? ext) existingPair, T toAdd)
        where T : struct => existingPair.v1.Equals(toAdd) || existingPair.ext?.Contains(toAdd) == true
        ? existingPair
        : existingPair.ext == null
            ? (existingPair.v1, new[] { toAdd })
            : (existingPair.v1, AddElementIfNotExist(existingPair.ext, toAdd));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
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
