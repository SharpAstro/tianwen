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






    public static partial class Window
    {
        /// <summary>
        /// ENUM Types for included Windows.
        /// </summary>
        public enum Type
        {
            None,
            Rectangular,
            Welch,
            Bartlett,
            Hanning,
            Hann,
            Hamming,
            Nutall3,
            Nutall4,
            Nutall3A,
            Nutall3B,
            Nutall4A,
            BH92,
            Nutall4B,

            SFT3F,
            SFT3M,
            FTNI,
            SFT4F,
            SFT5F,
            SFT4M,
            FTHP,
            HFT70,
            FTSRS,
            SFT5M,
            HFT90D,
            HFT95,
            HFT116D,
            HFT144D,
            HFT169D,
            HFT196D,
            HFT223D,
            HFT248D
        }



    }



}


