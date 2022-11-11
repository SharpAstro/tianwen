namespace Astap.Lib.Stat;

public partial class DSP
{
    public static partial class Window
    {
        public static class ScaleFactor
        {
            /// <summary>
            /// Calculate Signal scale factor from window coefficient array.
            /// Designed to be applied to the "Magnitude" result.
            /// </summary>
            /// <param name="windowCoefficients"></param>
            /// <returns>double scaleFactor</returns>
            public static double Signal(double[] windowCoefficients)
            {
                double s1 = 0;
                foreach (double coeff in windowCoefficients)
                {
                    s1 += coeff;
                }

                s1 = s1 / windowCoefficients.Length;

                return 1.0 / s1;
            }


            /// <summary>
            ///  Calculate Noise scale factor from window coefficient array.
            ///  Takes into account the bin width in Hz for the final result also.
            ///  Designed to be applied to the "Magnitude" result.
            /// </summary>
            /// <param name="windowCoefficients"></param>
            /// <param name="samplingFrequencyHz"></param>
            /// <returns>double scaleFactor</returns>
            public static double Noise(double[] windowCoefficients, double samplingFrequencyHz)
            {
                double s2 = 0;
                foreach (double coeff in windowCoefficients)
                {
                    s2 = s2 + coeff * coeff;
                }

                double n = windowCoefficients.Length;
                double fbin = samplingFrequencyHz / n;

                double sf = System.Math.Sqrt(1.0 / (s2 / n * fbin));

                return sf;
            }


            /// <summary>
            ///  Calculate Normalized, Equivalent Noise BandWidth from window coefficient array.
            /// </summary>
            /// <param name="windowCoefficients"></param>
            /// <returns>double NENBW</returns>
            public static double NENBW(double[] windowCoefficients)
            {
                double s1 = 0;
                double s2 = 0;
                foreach (double coeff in windowCoefficients)
                {
                    s1 = s1 + coeff;
                    s2 = s2 + coeff * coeff;
                }

                double n = windowCoefficients.Length;
                s1 = s1 / n;

                double nenbw = s2 / (s1 * s1) / n;

                return nenbw;
            }
        }
    }
}