﻿// =====[ Revision History ]==========================================
// 17Jun16 - 1.0 - First release - Steve Hageman
// 20Jun16 - 1.01 - Made some variable terms consistent - Steve Hageman
// 16Jul16 - 1.02 - Calculated sign of DFT phase was not consistent with that of the FFT. ABS() of phase was right.
//                  FFT with zero padding did not correctly clean up first runs results.
//                  Added UnwrapPhaseDegrees() and UnwrapPhaseRadians() to Analysis Class.
// 04Jul17 - 1.03 - Added zero or negative check to all Log10 operations.
// 15Oct17 - 1.03.1 - Slight interoperability correction to V1.03, same results, different design pattern.
//

namespace TianWen.Lib.Stat;

public partial class DSP
{
    /**
    *
    * Many of the windows functions are based on the article,
    *
    *   Spectrum and spectral density estimation by the Discrete Fourier
    *   transform (DFT), including a comprehensive list of window
    *   functions and some new ﬂat-top windows.
    *
    *   G. Heinzel, A. Rudiger and R. Schilling,
    *   Max-Planck-Institut fur Gravitationsphysik
    *
    *   February 15, 2002
    *
    **/

    public static partial class Window
    {
        /// <summary>
        /// Calculates a set of Windows coefficients for a given number of points and a window type to use.
        /// </summary>
        /// <param name="windowName"></param>
        /// <param name="points"></param>
        /// <returns>double[] array of the calculated window coefficients</returns>
        public static double[] Coefficients(Type windowName, uint points)
        {
            double[] winCoeffs = new double[points];
            double N = points;

            switch (windowName)
            {
                case Type.None:
                case Type.Rectangular:
                    //wc = ones(N,1);
                    for (uint i = 0; i < points; i++)
                        winCoeffs[i] = 1.0;

                    break;

                case Type.Bartlett:
                    //n = (0:N-1)';
                    //wc = 2/N*(N/2-abs(n-(N-1)/2));
                    for (uint i = 0; i < points; i++)
                        winCoeffs[i] = 2.0 / N * (N / 2.0 - System.Math.Abs(i - (N - 1.0) / 2.0));

                    break;

                case Type.Welch:
                    //n = (0:N-1)';
                    //wc = 1 - ( ((2*n)/N) - 1).^2;
                    for (uint i = 0; i < points; i++)
                        winCoeffs[i] = 1.0 - System.Math.Pow(2.0 * i / N - 1.0, 2.0);
                    break;

                case Type.Hann:
                case Type.Hanning:
                    //wc = (0.5 - 0.5*cos (z));
                    winCoeffs = SineExpansion(points, 0.5, -0.5);
                    break;

                case Type.Hamming:
                    //wc = (0.54 - 0.46*cos (z));
                    winCoeffs = SineExpansion(points, 0.54, -0.46);
                    break;

                case Type.BH92: // Also known as: Blackman-Harris
                                       //wc = (0.35875 - 0.48829*cos(z) + 0.14128*cos(2*z) - 0.01168*cos(3*z));
                    winCoeffs = SineExpansion(points, 0.35875, -0.48829, 0.14128, -0.01168);
                    break;

                case Type.Nutall3:
                    //c0 = 0.375; c1 = -0.5; c2 = 0.125;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z);
                    winCoeffs = SineExpansion(points, 0.375, -0.5, 0.125);
                    break;

                case Type.Nutall3A:
                    //c0 = 0.40897; c1 = -0.5; c2 = 0.09103;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z);
                    winCoeffs = SineExpansion(points, 0.40897, -0.5, 0.09103);
                    break;

                case Type.Nutall3B:
                    //c0 = 0.4243801; c1 = -0.4973406; c2 = 0.0782793;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z);
                    winCoeffs = SineExpansion(points, 0.4243801, -0.4973406, 0.0782793);
                    break;

                case Type.Nutall4:
                    //c0 = 0.3125; c1 = -0.46875; c2 = 0.1875; c3 = -0.03125;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z);
                    winCoeffs = SineExpansion(points, 0.3125, -0.46875, 0.1875, -0.03125);
                    break;

                case Type.Nutall4A:
                    //c0 = 0.338946; c1 = -0.481973; c2 = 0.161054; c3 = -0.018027;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z);
                    winCoeffs = SineExpansion(points, 0.338946, -0.481973, 0.161054, -0.018027);
                    break;

                case Type.Nutall4B:
                    //c0 = 0.355768; c1 = -0.487396; c2 = 0.144232; c3 = -0.012604;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z);
                    winCoeffs = SineExpansion(points, 0.355768, -0.487396, 0.144232, -0.012604);
                    break;

                case Type.SFT3F:
                    //c0 = 0.26526; c1 = -0.5; c2 = 0.23474;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z);
                    winCoeffs = SineExpansion(points, 0.26526, -0.5, 0.23474);
                    break;

                case Type.SFT4F:
                    //c0 = 0.21706; c1 = -0.42103; c2 = 0.28294; c3 = -0.07897;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z);
                    winCoeffs = SineExpansion(points, 0.21706, -0.42103, 0.28294, -0.07897);
                    break;

                case Type.SFT5F:
                    //c0 = 0.1881; c1 = -0.36923; c2 = 0.28702; c3 = -0.13077; c4 = 0.02488;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z) + c4*cos(4*z);
                    winCoeffs = SineExpansion(points, 0.1881, -0.36923, 0.28702, -0.13077, 0.02488);
                    break;

                case Type.SFT3M:
                    //c0 = 0.28235; c1 = -0.52105; c2 = 0.19659;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z);
                    winCoeffs = SineExpansion(points, 0.28235, -0.52105, 0.19659);
                    break;

                case Type.SFT4M:
                    //c0 = 0.241906; c1 = -0.460841; c2 = 0.255381; c3 = -0.041872;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z);
                    winCoeffs = SineExpansion(points, 0.241906, -0.460841, 0.255381, -0.041872);
                    break;

                case Type.SFT5M:
                    //c0 = 0.209671; c1 = -0.407331; c2 = 0.281225; c3 = -0.092669; c4 = 0.0091036;
                    //wc = c0 + c1*cos(z) + c2*cos(2*z) + c3*cos(3*z) + c4*cos(4*z);
                    winCoeffs = SineExpansion(points, 0.209671, -0.407331, 0.281225, -0.092669, 0.0091036);
                    break;

                case Type.FTNI:
                    //wc = (0.2810639 - 0.5208972*cos(z) + 0.1980399*cos(2*z));
                    winCoeffs = SineExpansion(points, 0.2810639, -0.5208972, 0.1980399);
                    break;

                case Type.FTHP:
                    //wc = 1.0 - 1.912510941*cos(z) + 1.079173272*cos(2*z) - 0.1832630879*cos(3*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.912510941, 1.079173272, -0.1832630879);
                    break;

                case Type.HFT70:
                    //wc = 1 - 1.90796*cos(z) + 1.07349*cos(2*z) - 0.18199*cos(3*z);
                    winCoeffs = SineExpansion(points, 1, -1.90796, 1.07349, -0.18199);
                    break;

                case Type.FTSRS:
                    //wc = 1.0 - 1.93*cos(z) + 1.29*cos(2*z) - 0.388*cos(3*z) + 0.028*cos(4*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.93, 1.29, -0.388, 0.028);
                    break;

                case Type.HFT90D:
                    //wc = 1 - 1.942604*cos(z) + 1.340318*cos(2*z) - 0.440811*cos(3*z) + 0.043097*cos(4*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.942604, 1.340318, -0.440811, 0.043097);
                    break;

                case Type.HFT95:
                    //wc = 1 - 1.9383379*cos(z) + 1.3045202*cos(2*z) - 0.4028270*cos(3*z) + 0.0350665*cos(4*z);
                    winCoeffs = SineExpansion(points, 1, -1.9383379, 1.3045202, -0.4028270, 0.0350665);
                    break;

                case Type.HFT116D:
                    //wc = 1 - 1.9575375*cos(z) + 1.4780705*cos(2*z) - 0.6367431*cos(3*z) + 0.1228389*cos(4*z) - 0.0066288*cos(5*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.9575375, 1.4780705, -0.6367431, 0.1228389, -0.0066288);
                    break;

                case Type.HFT144D:
                    //wc = 1 - 1.96760033*cos(z) + 1.57983607*cos(2*z) - 0.81123644*cos(3*z) + 0.22583558*cos(4*z) - 0.02773848*cos(5*z) + 0.00090360*cos(6*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.96760033, 1.57983607, -0.81123644, 0.22583558, -0.02773848, 0.00090360);
                    break;

                case Type.HFT169D:
                    //wc = 1 - 1.97441842*cos(z) + 1.65409888*cos(2*z) - 0.95788186*cos(3*z) + 0.33673420*cos(4*z) - 0.06364621*cos(5*z) + 0.00521942*cos(6*z) - 0.00010599*cos(7*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.97441842, 1.65409888, -0.95788186, 0.33673420, -0.06364621, 0.00521942, -0.00010599);
                    break;

                case Type.HFT196D:
                    //wc = 1 - 1.979280420*cos(z) + 1.710288951*cos(2*z) - 1.081629853*cos(3*z)+ 0.448734314*cos(4*z) - 0.112376628*cos(5*z) + 0.015122992*cos(6*z) - 0.000871252*cos(7*z) + 0.000011896*cos(8*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.979280420, 1.710288951, -1.081629853, 0.448734314, -0.112376628, 0.015122992, -0.000871252, 0.000011896);
                    break;

                case Type.HFT223D:
                    //wc = 1 - 1.98298997309*cos(z) + 1.75556083063*cos(2*z) - 1.19037717712*cos(3*z) + 0.56155440797*cos(4*z) - 0.17296769663*cos(5*z) + 0.03233247087*cos(6*z) - 0.00324954578*cos(7*z) + 0.00013801040*cos(8*z) - 0.00000132725*cos(9*z);
                    winCoeffs = SineExpansion(points, 1.0, -1.98298997309, 1.75556083063, -1.19037717712, 0.56155440797, -0.17296769663, 0.03233247087, -0.00324954578, 0.00013801040, -0.00000132725);
                    break;

                case Type.HFT248D:
                    //wc = 1 - 1.985844164102*cos(z) + 1.791176438506*cos(2*z) - 1.282075284005*cos(3*z) + 0.667777530266*cos(4*z) - 0.240160796576*cos(5*z) + 0.056656381764*cos(6*z) - 0.008134974479*cos(7*z) + 0.000624544650*cos(8*z) - 0.000019808998*cos(9*z) + 0.000000132974*cos(10*z);
                    winCoeffs = SineExpansion(points, 1, -1.985844164102, 1.791176438506, -1.282075284005, 0.667777530266, -0.240160796576, 0.056656381764, -0.008134974479, 0.000624544650, -0.000019808998, 0.000000132974);
                    break;

                default:
                    //throw new NotImplementedException("Window type fell through to 'Default'.");
                    break;
            }

            return winCoeffs;
        }

        private static double[] SineExpansion(uint points, double c0, double c1 = 0.0, double c2 = 0.0, double c3 = 0.0, double c4 = 0.0, double c5 = 0.0, double c6 = 0.0, double c7 = 0.0, double c8 = 0.0, double c9 = 0.0, double c10 = 0.0)
        {
            // z = 2 * pi * (0:N-1)' / N;   // Cosine Vector
            double[] z = new double[points];
            for (uint i = 0; i < points; i++)
                z[i] = 2.0 * System.Math.PI * i / points;

            double[] winCoeffs = new double[points];

            for (uint i = 0; i < points; i++)
            {
                double wc = c0;
                wc += c1 * System.Math.Cos(z[i]);
                wc += c2 * System.Math.Cos(2.0 * z[i]);
                wc += c3 * System.Math.Cos(3.0 * z[i]);
                wc += c4 * System.Math.Cos(4.0 * z[i]);
                wc += c5 * System.Math.Cos(5.0 * z[i]);
                wc += c6 * System.Math.Cos(6.0 * z[i]);
                wc += c7 * System.Math.Cos(7.0 * z[i]);
                wc += c8 * System.Math.Cos(8.0 * z[i]);
                wc += c9 * System.Math.Cos(9.0 * z[i]);
                wc += c10 * System.Math.Cos(10.0 * z[i]);

                winCoeffs[i] = wc;
            }

            return winCoeffs;
        }
    }
}