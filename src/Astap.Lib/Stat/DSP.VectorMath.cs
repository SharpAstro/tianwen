using System;
using System.Diagnostics;
using System.Numerics;

namespace Astap.Lib.Stat;

public partial class DSP
{
    /// <summary>
    /// Double[] Array Math Operations (All Static)
    /// </summary>
    public static class VectorMath
    {

        /// <summary>
        /// result[] = a[] * b[]
        /// </summary>
        public static double[] Multiply(double[] a, double[] b)
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
                var v1 = new Vector<double>(a, i);
                var v2 = new Vector<double>(b, i);
                (v1 * v2).CopyTo(result, i);
            }

            for (int i = length - remaining; i < length; i++)
            {
                result[i] = a[i] + b[i];
            }

            return result;
        }

        /// <summary>
        /// result[] = a[] * b
        /// </summary>
        public static double[] Multiply(double[] a, double b)
        {
            int length = a.Length;
            double[] result = new double[length];

            // Get the number of elements that can't be processed in the vector
            // NOTE: Vector<T>.Count is a JIT time constant and will get optimized accordingly
            int remaining = length % Vector<double>.Count;

            for (int i = 0; i < length - remaining; i += Vector<double>.Count)
            {
                var v1 = new Vector<double>(a, i);
                (v1 * b).CopyTo(result, i);
            }

            for (int i = length - remaining; i < length; i++)
            {
                result[i] = a[i] + b;
            }

            return result;
        }

        /// <summary>
        /// result[] = a[] + b[]
        /// </summary>
        public static double[] Add(double[] a, double[] b)
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
                var v1 = new Vector<double>(a, i);
                var v2 = new Vector<double>(b, i);
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
        public static double[] Add(double[] a, double b)
        {
            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
                result[i] = a[i] + b;

            return result;
        }

        /// <summary>
        /// result[] = a[] - b[]
        /// </summary>
        public static double[] Subtract(double[] a, double[] b)
        {
            Debug.Assert(a.Length == b.Length, "Length of arrays a[] and b[] must match.");

            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
                result[i] = a[i] - b[i];

            return result;
        }

        /// <summary>
        /// result[] = a[] - b
        /// </summary>
        public static double[] Subtract(double[] a, double b)
        {
            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
                result[i] = a[i] - b;

            return result;
        }

        /// <summary>
        /// result[] = a[] / b[]
        /// </summary>
        public static double[] Divide(double[] a, double[] b)
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
                var v1 = new Vector<double>(a, i);
                var v2 = new Vector<double>(b, i);
                (v1 * v2).CopyTo(result, i);
            }

            for (int i = length - remaining; i < length; i++)
            {
                result[i] = a[i] + b[i];
            }

            return result;
        }

        /// <summary>
        /// result[] = a[] / b
        /// </summary>
        public static double[] Divide(double[] a, double b)
        {
            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
                result[i] = a[i] / b;

            return result;
        }

        /// <summary>
        /// Square root of a[].
        /// </summary>
        public static double[] Sqrt(double[] a)
        {
            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
                result[i] = System.Math.Sqrt(a[i]);

            return result;
        }

        /// <summary>
        /// Squares a[].
        /// </summary>
        public static double[] Square(double[] a) => Multiply(a, a);

        /// <summary>
        /// Log10 a[].
        /// </summary>
        public static double[] Log10(double[] a)
        {
            double[] result = new double[a.Length];
            for (uint i = 0; i < a.Length; i++)
            {
                double val = a[i];
                if (val <= 0.0)
                    val = double.Epsilon;

                result[i] = System.Math.Log10(val);
            }

            return result;
        }

        /// <summary>
        /// Removes mean value from a[].
        /// </summary>
        public static double[] RemoveMean(double[] a)
        {
            double sum = 0.0;
            for (uint i = 0; i < a.Length; i++)
                sum += a[i];

            double mean = sum / a.Length;

            return Subtract(a, mean);
        }
    }
}