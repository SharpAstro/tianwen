// This C# code is derived from routines published by the International
// Astronomical Union's Standards of Fundamental Astronomy (SOFA) service
// (http://www.iausofa.org). It does not use the "iau" or "sofa" prefix
// and is not endorsed by the IAU.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Astrometry.SOFA
{
    /// <summary>
    /// Star-independent astrometry parameters.
    /// Zero heap allocation — all vectors/matrices use fixed-size buffers.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct Astrom
    {
        /// <summary>PM time interval (SSB, Julian years)</summary>
        public double Pmt;

        /// <summary>SSB to observer (vector, au) [3]</summary>
        public fixed double Eb[3];

        /// <summary>Sun to observer (unit vector) [3]</summary>
        public fixed double Eh[3];

        /// <summary>Distance from Sun to observer (au)</summary>
        public double Em;

        /// <summary>Barycentric observer velocity (vector, c) [3]</summary>
        public fixed double V[3];

        /// <summary>sqrt(1-|v|^2): reciprocal of Lorentz factor</summary>
        public double Bm1;

        /// <summary>Bias-precession-nutation matrix (3x3, row-major) [9]</summary>
        public fixed double Bpn[9];

        /// <summary>Longitude + s' + dERA(DUT) (radians)</summary>
        public double Along;

        /// <summary>Geodetic latitude (radians)</summary>
        public double Phi;

        /// <summary>Polar motion xp wrt local meridian (radians)</summary>
        public double Xpl;

        /// <summary>Polar motion yp wrt local meridian (radians)</summary>
        public double Ypl;

        /// <summary>Sine of geodetic latitude</summary>
        public double Sphi;

        /// <summary>Cosine of geodetic latitude</summary>
        public double Cphi;

        /// <summary>Magnitude of diurnal aberration vector</summary>
        public double Diurab;

        /// <summary>"Local" Earth rotation angle (radians)</summary>
        public double Eral;

        /// <summary>Refraction constant A (radians)</summary>
        public double Refa;

        /// <summary>Refraction constant B (radians)</summary>
        public double Refb;

        /// <summary>Get a Span view of the Eb fixed buffer.</summary>
        public Span<double> EbSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Eb) { return new Span<double>(p, 3); } }
        }

        /// <summary>Get a read-only Span view of the Eb fixed buffer.</summary>
        public readonly ReadOnlySpan<double> EbReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Eb) { return new ReadOnlySpan<double>(p, 3); } }
        }

        /// <summary>Get a Span view of the Eh fixed buffer.</summary>
        public Span<double> EhSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Eh) { return new Span<double>(p, 3); } }
        }

        /// <summary>Get a read-only Span view of the Eh fixed buffer.</summary>
        public readonly ReadOnlySpan<double> EhReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Eh) { return new ReadOnlySpan<double>(p, 3); } }
        }

        /// <summary>Get a Span view of the V fixed buffer.</summary>
        public Span<double> VSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = V) { return new Span<double>(p, 3); } }
        }

        /// <summary>Get a read-only Span view of the V fixed buffer.</summary>
        public readonly ReadOnlySpan<double> VReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = V) { return new ReadOnlySpan<double>(p, 3); } }
        }

        /// <summary>Get a Span view of the Bpn fixed buffer (9 elements, row-major).</summary>
        public Span<double> BpnSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Bpn) { return new Span<double>(p, 9); } }
        }

        /// <summary>Get a read-only Span view of the Bpn fixed buffer (9 elements, row-major).</summary>
        public readonly ReadOnlySpan<double> BpnReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { fixed (double* p = Bpn) { return new ReadOnlySpan<double>(p, 9); } }
        }
    }
}
