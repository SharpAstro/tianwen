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
    /// DFT / FFT Format Conversion Functions
    /// </summary>
    public static class ConvertMagnitude
    {
        /// <summary>
        /// Convert Magnitude FT Result to: Magnitude Squared Format
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns></returns>
        public static double[] ToMagnitudeSquared(double[] magnitude)
        {
            uint np = (uint)magnitude.Length;
            double[] mag2 = new double[np];
            for (uint i = 0; i < np; i++)
            {
                mag2[i] = magnitude[i] * magnitude[i];
            }

            return mag2;
        }


        /// <summary>
        /// Convert Magnitude FT Result to: Magnitude dBVolts
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>double[] array</returns>
        public static double[] ToMagnitudeDBV(double[] magnitude)
        {
            uint np = (uint)magnitude.Length;
            double[] magDBV = new double[np];
            for (uint i = 0; i < np; i++)
            {
                double magVal = magnitude[i];
                if (magVal <= 0.0)
                    magVal = double.Epsilon;

                magDBV[i] = 20 * System.Math.Log10(magVal);
            }

            return magDBV;
        }

    }
}

