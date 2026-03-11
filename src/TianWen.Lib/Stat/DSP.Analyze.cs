// =====[ Revision History ]==========================================
// 17Jun16 - 1.0 - First release - Steve Hageman
// 20Jun16 - 1.01 - Made some variable terms consistent - Steve Hageman
// 16Jul16 - 1.02 - Calculated sign of DFT phase was not consistent with that of the FFT. ABS() of phase was right.
//                  FFT with zero padding did not correctly clean up first runs results.
//                  Added UnwrapPhaseDegrees() and UnwrapPhaseRadians() to Analysis Class.
// 04Jul17 - 1.03 - Added zero or negative check to all Log10 operations.
// 15Oct17 - 1.03.1 - Slight interoperability correction to V1.03, same results, different design pattern.
//

using System;
using System.Numerics.Tensors;

namespace TianWen.Lib.Stat;

public partial class DSP
{
    /// <summary>
    /// DFT / FFT Output Analysis Functions
    /// </summary>
    public static class Analyze
    {
        private static ReadOnlySpan<double> GetSlice(double[] a, uint startBin, uint stopBin)
        {
            var start = (int)startBin;
            var length = a.Length - (int)startBin - (int)stopBin;
            return a.AsSpan(start, length);
        }

        /// <summary>
        /// Find the RMS value of a[].
        /// </summary>
        /// <param name="a"> = of N data points, 0 based.</param>
        /// <param name="startBin"> = Bin to start the counting at (0 based)."></param>
        /// <param name="stopBin"> = Bin FROM END to stop counting at (Max = N - 1)."></param>
        /// <returns>RMS value of input array between start and stop bins.</returns>
        public static double FindRms(double[] a, uint startBin = 10, uint stopBin = 10)
        {
            var slice = GetSlice(a, startBin, stopBin);
            return Math.Sqrt(TensorPrimitives.SumOfSquares(slice) / slice.Length);
        }

        /// <summary>
        /// Finds the mean of the input array.
        /// </summary>
        /// <param name="inData"> = of N data points, 0 based.</param>
        /// <param name="startBin"> = Bin to start the counting at (0 based)."></param>
        /// <param name="stopBin"> = Bin FROM END to stop counting at (Max = N - 1)."></param>
        /// <returns>Mean value of input array between start and stop bins.</returns>
        public static double FindMean(double[] inData, uint startBin = 10, uint stopBin = 10)
        {
            var slice = GetSlice(inData, startBin, stopBin);
            return TensorPrimitives.Sum(slice) / slice.Length;
        }

        /// <summary>
        /// Finds the maximum value in an array.
        /// </summary>
        /// <param name="inData"></param>
        /// <returns>Maximum value of input array</returns>
        public static double FindMaxAmplitude(double[] inData) => TensorPrimitives.Max(inData);


        /// <summary>
        /// Finds the position in the inData array where the maximum value happens.
        /// </summary>
        /// <param name="inData"></param>
        /// <returns>Position of maximum value in input array</returns>
        public static uint FindMaxPosition(double[] inData) => (uint)TensorPrimitives.IndexOfMax(inData);

        /// <summary>
        /// Finds the maximum frequency from the given inData and fSpan arrays.
        /// </summary>
        /// <param name="inData"></param>
        /// <param name="fSpan"></param>
        /// <returns>Maximum frequency from input arrays</returns>
        public static double FindMaxFrequency(double[] inData, double[] fSpan) => fSpan[TensorPrimitives.IndexOfMax(inData)];


        /// <summary>
        /// Unwraps the phase so that it is continuous, without jumps.
        /// </summary>
        /// <param name="inPhaseDeg">Array of Phase Data from FT in Degrees</param>
        /// <returns>Continuous Phase data</returns>
        public static double[] UnwrapPhaseDegrees(double[] inPhaseDeg)
        {
            uint N = (uint)inPhaseDeg.Length;
            double[] unwrappedPhase = new double[N];

            double[] tempInData = new double[N];
            inPhaseDeg.CopyTo(tempInData, 0);

            // First point is unchanged
            unwrappedPhase[0] = tempInData[0];

            for (uint i = 1; i < N; i++)
            {
                double delta = System.Math.Abs(tempInData[i - 1] - tempInData[i]);
                if (delta >= 180)
                {
                    // Phase jump!
                    if (tempInData[i - 1] < 0.0)
                    {
                        for (uint j = i; j < N; j++)
                            tempInData[j] += -360;
                    }
                    else
                    {
                        for (uint j = i; j < N; j++)
                            tempInData[j] += 360;
                    }
                }
                unwrappedPhase[i] = tempInData[i];
            }
            return unwrappedPhase;
        }


        /// <summary>
        /// Unwraps the phase so that it is continuous, without jumps.
        /// </summary>
        /// <param name="inPhaseRad">Array of Phase Data from FT in Radians</param>
        /// <returns>Continuous Phase data</returns>
        public static double[] UnwrapPhaseRadians(double[] inPhaseRad)
        {
            double pi = System.Math.PI;
            double twoPi = System.Math.PI * 2.0;

            uint N = (uint)inPhaseRad.Length;

            double[] tempInData = new double[N];
            inPhaseRad.CopyTo(tempInData, 0);

            double[] unwrappedPhase = new double[N];

            // First point is unchanged
            unwrappedPhase[0] = tempInData[0];

            for (uint i = 1; i < N; i++)
            {
                double delta = System.Math.Abs(tempInData[i - 1] - tempInData[i]);
                if (delta >= pi)
                {
                    // Phase jump!
                    if (tempInData[i - 1] < 0.0)
                    {
                        for (uint j = i; j < N; j++)
                            tempInData[j] += -twoPi;
                    }
                    else
                    {
                        for (uint j = i; j < N; j++)
                            tempInData[j] += twoPi;
                    }
                }
                unwrappedPhase[i] = tempInData[i];
            }
            return unwrappedPhase;
        }
    }



}


