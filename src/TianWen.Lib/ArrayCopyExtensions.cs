using System;

namespace TianWen.Lib;

/// <summary>
/// A typed copy of a two-dimensional array. <see cref="Array.Clone"/> returns <see cref="object"/>, so every copy through it is a
/// cast, and a cast copy is never written here (<c>NoCastArrayCloneTests</c>). Ask first whether the copy is needed at all: a
/// reader takes the array or a <see cref="ReadOnlySpan{T}"/>, and a scratch for a routine that writes into its input comes from
/// <see cref="ArrayPoolHelper"/>. A one-dimensional copy needs no helper: <c>float[] copy = [.. source];</c>.
/// </summary>
public static class ArrayCopyExtensions
{
    extension<T>(T[,] array)
    {
        /// <summary>A copy with its own storage, the same shape and the same elements.</summary>
        public T[,] Copy()
        {
            var copy = new T[array.GetLength(0), array.GetLength(1)];
            Array.Copy(array, copy, array.Length);
            return copy;
        }
    }
}
