using System;
using System.Numerics;

namespace TianWen.Lib.Stat;

/// <summary>
/// Array Math Operations
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// result[] = a[] * b[]
    /// </summary>
    public static double[] Multiply(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"{nameof(a)} and {nameof(b)} are not the same length", nameof(b));
        }

        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            var v2 = new Vector<double>(b[i..]);
            (v1 * v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] * b[i];
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] * b
    /// </summary>
    public static double[] Multiply(ReadOnlySpan<double> a, double b)
    {
        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            (v1 * b).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] * b;
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] + b[]
    /// </summary>
    public static double[] Add(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"{nameof(a)} and {nameof(b)} are not the same length", nameof(b));
        }

        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            var v2 = new Vector<double>(b[i..]);
            (v1 + v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] + b[i];
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] + b
    /// </summary>
    public static double[] Add(ReadOnlySpan<double> a, double b)
    {
        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        var v2 = new Vector<double>(b);

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            (v1 + v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] + b;
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] - b[]
    /// </summary>
    public static double[] Subtract(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"{nameof(a)} and {nameof(b)} are not the same length", nameof(b));
        }

        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            var v2 = new Vector<double>(b[i..]);
            (v1 - v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] - b[i];
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] - b
    /// </summary>
    public static double[] Subtract(ReadOnlySpan<double> a, double b)
    {
        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        var v2 = new Vector<double>(b);
        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            (v1 - v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] - b;
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] / b[]
    /// </summary>
    public static double[] Divide(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"{nameof(a)} and {nameof(b)} are not the same length", nameof(b));
        }

        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            var v2 = new Vector<double>(b[i..]);
            (v1 / v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] / b[i];
        }

        return result;
    }

    /// <summary>
    /// result[] = a[] / b
    /// </summary>
    public static double[] Divide(ReadOnlySpan<double> a, double b)
    {
        int length = a.Length;
        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        var v2 = new Vector<double>(b);
        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            (v1 / v2).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = a[i] / b;
        }

        return result;
    }

    /// <summary>
    /// Square root of a[].
    /// </summary>
    public static double[] Sqrt(ReadOnlySpan<double> a)
    {
        int length = a.Length;

        double[] result = new double[length];

        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            Vector.SquareRoot(v1).CopyTo(result, i);
        }

        for (int i = length - remaining; i < length; i++)
        {
            result[i] = Math.Sqrt(a[i]);
        }

        return result;
    }

    /// <summary>
    /// Squares a[].
    /// </summary>
    public static double[] Square(ReadOnlySpan<double> a) => Multiply(a, a);

    /// <summary>
    /// Log10 a[].
    /// </summary>
    public static double[] Log10(ReadOnlySpan<double> a)
    {
        double[] result = new double[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            double val = a[i];
            if (val <= 0.0)
                val = double.Epsilon;

            result[i] = Math.Log10(val);
        }

        return result;
    }

    /// <summary>
    /// Sum of a[].
    /// </summary>
    /// <param name="a"></param>
    /// <returns></returns>
    public static double Sum(ReadOnlySpan<double> a)
    {
        double sum = 0.0;
        int length = a.Length;
        // Get the number of elements that can't be processed in the vector
        // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
        int remaining = length % Vector<double>.Count;

        for (int i = 0; i < length - remaining; i += Vector<double>.Count)
        {
            var v1 = new Vector<double>(a[i..]);
            sum += Vector.Sum(v1);
        }

        for (int i = length - remaining; i < length; i++)
        {
            sum += a[i];
        }

        return sum;
    }

    /// <summary>
    /// Removes mean value from a[].
    /// </summary>
    public static double[] RemoveMean(ReadOnlySpan<double> a) => Subtract(a, Sum(a) / a.Length);
}