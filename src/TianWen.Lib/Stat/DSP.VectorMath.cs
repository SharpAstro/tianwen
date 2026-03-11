using System;
using System.Numerics.Tensors;

namespace TianWen.Lib.Stat;

/// <summary>
/// Array Math Operations — delegates to <see cref="TensorPrimitives"/> for SIMD-accelerated implementations.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// result[] = a[] * b[]
    /// </summary>
    public static double[] Multiply(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Multiply(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] * b
    /// </summary>
    public static double[] Multiply(ReadOnlySpan<double> a, double b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Multiply(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] + b[]
    /// </summary>
    public static double[] Add(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Add(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] + b
    /// </summary>
    public static double[] Add(ReadOnlySpan<double> a, double b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Add(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] - b[]
    /// </summary>
    public static double[] Subtract(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Subtract(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] - b
    /// </summary>
    public static double[] Subtract(ReadOnlySpan<double> a, double b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Subtract(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] / b[]
    /// </summary>
    public static double[] Divide(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Divide(a, b, result);
        return result;
    }

    /// <summary>
    /// result[] = a[] / b
    /// </summary>
    public static double[] Divide(ReadOnlySpan<double> a, double b)
    {
        var result = new double[a.Length];
        TensorPrimitives.Divide(a, b, result);
        return result;
    }

    /// <summary>
    /// Square root of a[].
    /// </summary>
    public static double[] Sqrt(ReadOnlySpan<double> a)
    {
        var result = new double[a.Length];
        TensorPrimitives.Sqrt(a, result);
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
        var result = new double[a.Length];
        TensorPrimitives.Log10(a, result);
        return result;
    }

    /// <summary>
    /// Sum of a[].
    /// </summary>
    public static double Sum(ReadOnlySpan<double> a) => TensorPrimitives.Sum(a);

    /// <summary>
    /// Removes mean value from a[].
    /// </summary>
    public static double[] RemoveMean(ReadOnlySpan<double> a) => Subtract(a, Sum(a) / a.Length);
}
