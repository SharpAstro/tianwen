using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Astap.Lib;

public static class LookupHelper
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
    public static void AddLookupEntry<TVal>(this (TVal v1, TVal[]? ext)[,] lookupTable, int a, int b, TVal toAdd)
        where TVal : struct => lookupTable[a, b] = AddElementIfNotExist(lookupTable[a, b], toAdd);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetLookupEntries<TKey, TVal>(this Dictionary<TKey, (TVal v1, TVal[]? ext)> lookupTable, TKey key, out IReadOnlyList<TVal> combined)
        where TKey : notnull
        where TVal : struct, Enum
    {
        if (lookupTable.TryGetValue(key, out var lookedUpValues))
        {
            return TryGetLookupEntries(lookedUpValues, out combined);
        }

        combined = Array.Empty<TVal>();
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static bool TryGetLookupEntries<TVal>(this (TVal v1, TVal[]? ext)[,] lookupTable, int a, int b, out IReadOnlyList<TVal> combined)
        where TVal : struct, Enum => TryGetLookupEntries(lookupTable[a, b], out combined);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool TryGetLookupEntries<TVal>(in (TVal v1, TVal[]? ext) lookedUpValues, out IReadOnlyList<TVal> combined)
        where TVal : struct, Enum
    {
        var outputValues = new TVal[(lookedUpValues.v1.Equals(default) ? 0 : 1) + (lookedUpValues.ext?.Length ?? 0)];
        if (outputValues.Length == 0)
        {
            combined = outputValues;
            return false;
        }

        outputValues[0] = lookedUpValues.v1;
        lookedUpValues.ext?.CopyTo(outputValues, 1);
        combined = outputValues;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static (T v1, T[]? ext) AddElementIfNotExist<T>(in (T v1, T[]? ext) existingPair, T toAdd)
        where T : struct => existingPair.v1.Equals(toAdd) || existingPair.ext?.Contains(toAdd) == true
        ? existingPair
        : existingPair.ext == null
            ? (existingPair.v1.Equals(default(T))
                ? (toAdd, null)
                : (existingPair.v1, new[] { toAdd })
            )
            : (existingPair.v1, AppendToArray(existingPair.ext, toAdd));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static T[] AddElementIfNotExist<T>(T[] existingArray, T toAdd)
    {
        if (existingArray.Contains(toAdd))
        {
            return existingArray;
        }

        return AppendToArray(existingArray, toAdd);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static T[] AppendToArray<T>(T[] existingArray, T toAdd)
    {
        var newArray = new T[existingArray.Length + 1];
        Array.Copy(existingArray, newArray, existingArray.Length);
        newArray[^1] = toAdd;
        return newArray;
    }
}
