using System.Runtime.CompilerServices;

namespace Astap.Lib.Stat;

public partial class FFT
{
    // Element for linked list to store input/output data.
    private sealed class FFTElement
    {
        public double Re { [MethodImpl(MethodImplOptions.AggressiveInlining)]  get; internal set; } = 0.0;
        public double Im { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; internal set; } = 0.0;
        public FFTElement? Next { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; internal set; }

        /// <summary>
        /// Target position post bit-reversal
        /// </summary>
        public uint RevTgt { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; internal set; } = 0;
    }
}