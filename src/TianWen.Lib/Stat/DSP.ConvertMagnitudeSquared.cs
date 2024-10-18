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
    public static class ConvertMagnitudeSquared
    {

        /// <summary>
        /// Convert Magnitude Squared FFT Result to: Magnitude Vrms
        /// </summary>
        /// <param name="magSquared"></param>
        /// <returns>double[] array</returns>
        public static double[] ToMagnitude(double[] magSquared)
        {
            uint np = (uint)magSquared.Length;
            double[] mag = new double[np];
            for (uint i = 0; i < np; i++)
            {
                mag[i] = System.Math.Sqrt(magSquared[i]);
            }

            return mag;
        }

        /// <summary>
        /// Convert Magnitude Squared FFT Result to: Magnitude dBVolts
        /// </summary>
        /// <param name="magSquared"></param>
        /// <returns>double[] array</returns>
        public static double[] ToMagnitudeDBV(double[] magSquared)
        {
            uint np = (uint)magSquared.Length;
            double[] magDBV = new double[np];
            for (uint i = 0; i < np; i++)
            {
                double magSqVal = magSquared[i];
                if (magSqVal <= 0.0)
                    magSqVal = double.Epsilon;

                magDBV[i] = 10 * System.Math.Log10(magSqVal);
            }

            return magDBV;
        }
    }



}


