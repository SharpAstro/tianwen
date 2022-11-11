using System.Numerics;


// =====[ Revision History ]==========================================
// 17Jun16 - 1.0 - First release - Steve Hageman
// 20Jun16 - 1.01 - Made some variable terms consistent - Steve Hageman
// 16Jul16 - 1.02 - Calculated sign of DFT phase was not consistent with that of the FFT. ABS() of phase was right.
//                  FFT with zero padding did not correctly clean up first runs results.
//                  Added UnwrapPhaseDegrees() and UnwrapPhaseRadians() to Analysis Class.
// 04Jul17 - 1.03 - Added zero or negative check to all Log10 operations.
// 15Oct17 - 1.03.1 - Slight interoperability correction to V1.03, same results, different design pattern.
//


namespace Astap.Lib.Stat;





public partial class DSP
{
    /// <summary>
    /// DFT / FFT Format Conversion Functions.
    /// </summary>
    public static class ConvertComplex
    {
        /// <summary>
        /// Convert Complex DFT/FFT Result to: Magnitude Squared V^2 rms
        /// </summary>
        /// <param name="rawFFT"></param>
        /// <returns>double[] MagSquared Format</returns>
        public static double[] ToMagnitudeSquared(Complex[] rawFFT)
        {
            uint np = (uint)rawFFT.Length;
            double[] magSquared = new double[np];
            for (uint i = 0; i < np; i++)
            {
                double mag = rawFFT[i].Magnitude;
                magSquared[i] = mag * mag;
            }

            return magSquared;
        }


        /// <summary>
        /// Convert Complex DFT/FFT Result to: Magnitude Vrms
        /// </summary>
        /// <param name="rawFFT"></param>
        /// <returns>double[] Magnitude Format (Vrms)</returns>
        public static double[] ToMagnitude(Complex[] rawFFT)
        {
            uint np = (uint)rawFFT.Length;
            double[] mag = new double[np];
            for (uint i = 0; i < np; i++)
            {
                mag[i] = rawFFT[i].Magnitude;
            }

            return mag;
        }


        /// <summary>
        /// Convert Complex DFT/FFT Result to: Log Magnitude dBV
        /// </summary>
        /// <param name="rawFFT"> Complex[] input array"></param>
        /// <returns>double[] Magnitude Format (dBV)</returns>
        public static double[] ToMagnitudeDBV(Complex[] rawFFT)
        {
            uint np = (uint)rawFFT.Length;
            double[] mag = new double[np];
            for (uint i = 0; i < np; i++)
            {
                double magVal = rawFFT[i].Magnitude;

                if (magVal <= 0.0)
                    magVal = double.Epsilon;

                mag[i] = 20 * System.Math.Log10(magVal);
            }

            return mag;
        }


        /// <summary>
        /// Convert Complex DFT/FFT Result to: Phase in Degrees
        /// </summary>
        /// <param name="rawFFT"> Complex[] input array"></param>
        /// <returns>double[] Phase (Degrees)</returns>
        public static double[] ToPhaseDegrees(Complex[] rawFFT)
        {
            double sf = 180.0 / System.Math.PI; // Degrees per Radian scale factor

            uint np = (uint)rawFFT.Length;
            double[] phase = new double[np];
            for (uint i = 0; i < np; i++)
            {
                phase[i] = rawFFT[i].Phase * sf;
            }

            return phase;
        }


        /// <summary>
        /// Convert Complex DFT/FFT Result to: Phase in Radians
        /// </summary>
        /// <param name="rawFFT"> Complex[] input array"></param>
        /// <returns>double[] Phase (Degrees)</returns>
        public static double[] ToPhaseRadians(Complex[] rawFFT)
        {
            uint np = (uint)rawFFT.Length;
            double[] phase = new double[np];
            for (uint i = 0; i < np; i++)
            {
                phase[i] = rawFFT[i].Phase;
            }

            return phase;
        }
    }



}


