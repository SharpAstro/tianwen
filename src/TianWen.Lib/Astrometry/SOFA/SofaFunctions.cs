// This C# code is derived from routines published by the International
// Astronomical Union's Standards of Fundamental Astronomy (SOFA) service
// (http://www.iausofa.org). It does not use the "iau" or "sofa" prefix
// and is not endorsed by the IAU.

using System;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using static TianWen.Lib.Astrometry.SOFA.SofaConstants;

namespace TianWen.Lib.Astrometry.SOFA
{
    /// <summary>
    /// Zero-allocation port of SOFA astrometry functions.
    /// All vectors are Span&lt;double&gt; of length 3, matrices are Span&lt;double&gt; of length 9 (row-major).
    /// </summary>
    public static class SofaFunctions
    {
        // =====================================================================
        // Phase 1: Foundation — vector/matrix primitives
        // =====================================================================

        /// <summary>Normalize angle into the range [0, 2pi).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Anp(double a)
        {
            double w = a % D2PI;
            if (w < 0.0) w += D2PI;
            return w;
        }

        /// <summary>Normalize angle into the range [-pi, +pi).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Anpm(double a)
        {
            double w = a % D2PI;
            if (Math.Abs(w) >= DPI) w -= Math.CopySign(D2PI, a);
            return w;
        }

        /// <summary>Spherical coordinates to Cartesian direction vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void S2c(double theta, double phi, Span<double> c)
        {
            double cp = Math.Cos(phi);
            c[0] = Math.Cos(theta) * cp;
            c[1] = Math.Sin(theta) * cp;
            c[2] = Math.Sin(phi);
        }

        /// <summary>Cartesian direction vector to spherical coordinates.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void C2s(ReadOnlySpan<double> p, out double theta, out double phi)
        {
            double x = p[0], y = p[1], z = p[2];
            double d2 = x * x + y * y;
            theta = (d2 == 0.0) ? 0.0 : Math.Atan2(y, x);
            phi = (z == 0.0) ? 0.0 : Math.Atan2(z, Math.Sqrt(d2));
        }

        /// <summary>Dot product of two 3-vectors.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Pdp(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
        {
            return TensorPrimitives.Dot(a[..3], b[..3]);
        }

        /// <summary>Modulus of p-vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Pm(ReadOnlySpan<double> p)
        {
            return Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
        }

        /// <summary>Convert a p-vector into modulus and unit vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pn(ReadOnlySpan<double> p, out double r, Span<double> u)
        {
            double w = Pm(p);
            if (w == 0.0)
            {
                u[0] = 0.0; u[1] = 0.0; u[2] = 0.0;
            }
            else
            {
                double s = 1.0 / w;
                u[0] = s * p[0]; u[1] = s * p[1]; u[2] = s * p[2];
            }
            r = w;
        }

        /// <summary>Multiply a p-vector by a scalar.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Sxp(double s, ReadOnlySpan<double> p, Span<double> sp)
        {
            sp[0] = s * p[0]; sp[1] = s * p[1]; sp[2] = s * p[2];
        }

        /// <summary>Copy a p-vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cp(ReadOnlySpan<double> p, Span<double> c)
        {
            c[0] = p[0]; c[1] = p[1]; c[2] = p[2];
        }

        /// <summary>Zero a p-vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Zp(Span<double> p)
        {
            p[0] = 0.0; p[1] = 0.0; p[2] = 0.0;
        }

        /// <summary>Cross product of two 3-vectors.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pxp(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> axb)
        {
            double xa = a[0], ya = a[1], za = a[2];
            double xb = b[0], yb = b[1], zb = b[2];
            axb[0] = ya * zb - za * yb;
            axb[1] = za * xb - xa * zb;
            axb[2] = xa * yb - ya * xb;
        }

        /// <summary>r-matrix times p-vector (3x3 row-major matrix * 3-vector).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rxp(ReadOnlySpan<double> r, ReadOnlySpan<double> p, Span<double> rp)
        {
            // Use temp to allow in-place (p == rp)
            double w0 = r[0] * p[0] + r[1] * p[1] + r[2] * p[2];
            double w1 = r[3] * p[0] + r[4] * p[1] + r[5] * p[2];
            double w2 = r[6] * p[0] + r[7] * p[1] + r[8] * p[2];
            rp[0] = w0; rp[1] = w1; rp[2] = w2;
        }

        /// <summary>r-matrix times pv-vector. pv is [2][3] stored as 6 doubles.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rxpv(ReadOnlySpan<double> r, ReadOnlySpan<double> pv, Span<double> rpv)
        {
            Rxp(r, pv[..3], rpv[..3]);
            Rxp(r, pv[3..6], rpv[3..6]);
        }

        /// <summary>Transpose r-matrix times p-vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Trxp(ReadOnlySpan<double> r, ReadOnlySpan<double> p, Span<double> trp)
        {
            Span<double> tr = stackalloc double[9];
            Tr(r, tr);
            Rxp(tr, p, trp);
        }

        /// <summary>Transpose r-matrix times pv-vector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Trxpv(ReadOnlySpan<double> r, ReadOnlySpan<double> pv, Span<double> trpv)
        {
            Span<double> tr = stackalloc double[9];
            Tr(r, tr);
            Rxpv(tr, pv, trpv);
        }

        /// <summary>Copy 3x3 rotation matrix (9 doubles, row-major).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cr(ReadOnlySpan<double> r, Span<double> c)
        {
            r[..9].CopyTo(c);
        }

        /// <summary>Transpose 3x3 rotation matrix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Tr(ReadOnlySpan<double> r, Span<double> rt)
        {
            // Use temp for safe in-place
            double w00 = r[0], w01 = r[1], w02 = r[2];
            double w10 = r[3], w11 = r[4], w12 = r[5];
            double w20 = r[6], w21 = r[7], w22 = r[8];
            rt[0] = w00; rt[1] = w10; rt[2] = w20;
            rt[3] = w01; rt[4] = w11; rt[5] = w21;
            rt[6] = w02; rt[7] = w12; rt[8] = w22;
        }

        /// <summary>Initialize 3x3 identity matrix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Ir(Span<double> r)
        {
            r[0] = 1.0; r[1] = 0.0; r[2] = 0.0;
            r[3] = 0.0; r[4] = 1.0; r[5] = 0.0;
            r[6] = 0.0; r[7] = 0.0; r[8] = 1.0;
        }

        /// <summary>Rotate 3x3 matrix about x-axis (in-place).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rx(double phi, Span<double> r)
        {
            double s = Math.Sin(phi), c = Math.Cos(phi);
            // Row 1 and Row 2 (indices [3..5] and [6..8])
            double a10 = c * r[3] + s * r[6];
            double a11 = c * r[4] + s * r[7];
            double a12 = c * r[5] + s * r[8];
            double a20 = -s * r[3] + c * r[6];
            double a21 = -s * r[4] + c * r[7];
            double a22 = -s * r[5] + c * r[8];
            r[3] = a10; r[4] = a11; r[5] = a12;
            r[6] = a20; r[7] = a21; r[8] = a22;
        }

        /// <summary>Rotate 3x3 matrix about y-axis (in-place).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Ry(double theta, Span<double> r)
        {
            double s = Math.Sin(theta), c = Math.Cos(theta);
            // Row 0 and Row 2 (indices [0..2] and [6..8])
            double a00 = c * r[0] - s * r[6];
            double a01 = c * r[1] - s * r[7];
            double a02 = c * r[2] - s * r[8];
            double a20 = s * r[0] + c * r[6];
            double a21 = s * r[1] + c * r[7];
            double a22 = s * r[2] + c * r[8];
            r[0] = a00; r[1] = a01; r[2] = a02;
            r[6] = a20; r[7] = a21; r[8] = a22;
        }

        /// <summary>Rotate 3x3 matrix about z-axis (in-place).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Rz(double psi, Span<double> r)
        {
            double s = Math.Sin(psi), c = Math.Cos(psi);
            // Row 0 and Row 1 (indices [0..2] and [3..5])
            double a00 = c * r[0] + s * r[3];
            double a01 = c * r[1] + s * r[4];
            double a02 = c * r[2] + s * r[5];
            double a10 = -s * r[0] + c * r[3];
            double a11 = -s * r[1] + c * r[4];
            double a12 = -s * r[2] + c * r[5];
            r[0] = a00; r[1] = a01; r[2] = a02;
            r[3] = a10; r[4] = a11; r[5] = a12;
        }

        // =====================================================================
        // Phase 2: Time conversions
        // =====================================================================

        /// <summary>
        /// Encode date and time fields into 2-part Julian Date.
        /// scale should be "UTC", "TAI", "TT" etc.
        /// </summary>
        public static int Dtf2d(string scale, int iy, int im, int id, int ihr, int imn, double sec, out double d1, out double d2)
        {
            int js = Cal2jd(iy, im, id, out double dj, out double w);
            if (js != 0) { d1 = 0; d2 = 0; return js; }
            dj += w;

            double day = DAYSEC;
            double seclim = 60.0;

            if (scale == "UTC")
            {
                js = Dat(iy, im, id, 0.0, out double dat0);
                if (js < 0) { d1 = 0; d2 = 0; return js; }
                js = Dat(iy, im, id, 0.5, out double dat12);
                if (js < 0) { d1 = 0; d2 = 0; return js; }
                Jd2cal(dj, 1.5, out int iy2, out int im2, out int id2, out _);
                js = Dat(iy2, im2, id2, 0.0, out double dat24);
                if (js < 0) { d1 = 0; d2 = 0; return js; }
                double dleap = dat24 - (2.0 * dat12 - dat0);
                day += dleap;
                if (ihr == 23 && imn == 59) seclim += dleap;
            }

            if (ihr < 0 || ihr > 23) { d1 = 0; d2 = 0; return -4; }
            if (imn < 0 || imn > 59) { d1 = 0; d2 = 0; return -5; }
            if (sec < 0.0) { d1 = 0; d2 = 0; return -6; }
            if (sec >= seclim) js += 2;

            double time = (60.0 * (60 * ihr + imn) + sec) / day;
            d1 = dj;
            d2 = time;
            return js;
        }

        /// <summary>UTC to TAI.</summary>
        public static int Utctai(double utc1, double utc2, out double tai1, out double tai2)
        {
            bool big1 = Math.Abs(utc1) >= Math.Abs(utc2);
            double u1 = big1 ? utc1 : utc2;
            double u2 = big1 ? utc2 : utc1;

            int j = Jd2cal(u1, u2, out int iy, out int im, out int id, out double fd);
            if (j != 0) { tai1 = 0; tai2 = 0; return j; }

            j = Dat(iy, im, id, 0.0, out double dat0);
            if (j < 0) { tai1 = 0; tai2 = 0; return j; }
            j = Dat(iy, im, id, 0.5, out double dat12);
            if (j < 0) { tai1 = 0; tai2 = 0; return j; }

            Jd2cal(u1 + 1.5, u2 - fd, out int iyt, out int imt, out int idt, out _);
            j = Dat(iyt, imt, idt, 0.0, out double dat24);
            if (j < 0) { tai1 = 0; tai2 = 0; return j; }

            double dlod = 2.0 * (dat12 - dat0);
            double dleap = dat24 - (dat0 + dlod);
            fd *= (DAYSEC + dleap) / DAYSEC;
            fd *= (DAYSEC + dlod) / DAYSEC;

            Cal2jd(iy, im, id, out double z1, out double z2);
            double a2 = z1 - u1 + z2 + fd + dat0 / DAYSEC;

            if (big1) { tai1 = u1; tai2 = a2; }
            else { tai1 = a2; tai2 = u1; }
            return j;
        }

        /// <summary>TAI to TT.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Taitt(double tai1, double tai2, out double tt1, out double tt2)
        {
            const double dtat = TTMTAI / DAYSEC;
            if (Math.Abs(tai1) > Math.Abs(tai2))
            { tt1 = tai1; tt2 = tai2 + dtat; }
            else
            { tt1 = tai1 + dtat; tt2 = tai2; }
            return 0;
        }

        /// <summary>TT to TAI.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Tttai(double tt1, double tt2, out double tai1, out double tai2)
        {
            const double dtat = TTMTAI / DAYSEC;
            if (Math.Abs(tt1) > Math.Abs(tt2))
            { tai1 = tt1; tai2 = tt2 - dtat; }
            else
            { tai1 = tt1 - dtat; tai2 = tt2; }
            return 0;
        }

        /// <summary>TAI to UTC.</summary>
        public static int Taiutc(double tai1, double tai2, out double utc1, out double utc2)
        {
            bool big1 = Math.Abs(tai1) >= Math.Abs(tai2);
            double a1 = big1 ? tai1 : tai2;
            double a2 = big1 ? tai2 : tai1;

            double u1 = a1, u2 = a2;
            int j = 0;

            for (int i = 0; i < 3; i++)
            {
                j = Utctai(u1, u2, out double g1, out double g2);
                if (j < 0) { utc1 = 0; utc2 = 0; return j; }
                u2 += a1 - g1;
                u2 += a2 - g2;
            }

            if (big1) { utc1 = u1; utc2 = u2; }
            else { utc1 = u2; utc2 = u1; }
            return j;
        }

        /// <summary>TAI to UT1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Taiut1(double tai1, double tai2, double dta, out double ut11, out double ut12)
        {
            double dtad = dta / DAYSEC;
            if (Math.Abs(tai1) > Math.Abs(tai2))
            { ut11 = tai1; ut12 = tai2 + dtad; }
            else
            { ut11 = tai1 + dtad; ut12 = tai2; }
            return 0;
        }

        /// <summary>UTC to UT1.</summary>
        public static int Utcut1(double utc1, double utc2, double dut1, out double ut11, out double ut12)
        {
            if (Jd2cal(utc1, utc2, out int iy, out int im, out int id, out _) != 0)
            { ut11 = 0; ut12 = 0; return -1; }

            int js = Dat(iy, im, id, 0.0, out double dat);
            if (js < 0) { ut11 = 0; ut12 = 0; return -1; }

            double dta = dut1 - dat;

            int jw = Utctai(utc1, utc2, out double tai1, out double tai2);
            if (jw < 0) { ut11 = 0; ut12 = 0; return -1; }
            if (jw > 0) js = jw;

            if (Taiut1(tai1, tai2, dta, out ut11, out ut12) != 0)
            { ut11 = 0; ut12 = 0; return -1; }

            return js;
        }

        /// <summary>Earth Rotation Angle (IAU 2000 model).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Era00(double dj1, double dj2)
        {
            double d1, d2;
            if (dj1 < dj2) { d1 = dj1; d2 = dj2; }
            else { d1 = dj2; d2 = dj1; }
            double t = d1 + (d2 - DJ00);
            double f = (d1 % 1.0) + (d2 % 1.0);
            return Anp(D2PI * (f + 0.7790572732640 + 0.00273781191135448 * t));
        }

        /// <summary>Greenwich mean sidereal time (IAU 2006).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Gmst06(double uta, double utb, double tta, double ttb)
        {
            double t = ((tta - DJ00) + ttb) / DJC;
            return Anp(Era00(uta, utb) +
                (0.014506 +
                (4612.156534 +
                (1.3915817 +
                (-0.00000044 +
                (-0.000029956 +
                (-0.0000000368)
                * t) * t) * t) * t) * t) * DAS2R);
        }

        // =====================================================================
        // Calendar / JD helpers
        // =====================================================================

        /// <summary>Gregorian Calendar to Julian Date.</summary>
        public static int Cal2jd(int iy, int im, int id, out double djm0, out double djm)
        {
            const int IYMIN = -4799;
            ReadOnlySpan<int> mtab = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

            djm0 = DJM0;
            djm = 0;
            int j = 0;
            if (iy < IYMIN) return -1;
            if (im < 1 || im > 12) return -2;

            int ly = (im == 2 && (iy % 4 == 0) && (iy % 100 != 0 || iy % 400 == 0)) ? 1 : 0;
            if (id < 1 || id > mtab[im - 1] + ly) j = -3;

            int my = (im - 14) / 12;
            long iypmy = (long)iy + my;
            djm = (double)((1461L * (iypmy + 4800L)) / 4L
                          + (367L * (long)(im - 2 - 12 * my)) / 12L
                          - (3L * ((iypmy + 4900L) / 100L)) / 4L
                          + (long)id - 2432076L);
            return j;
        }

        /// <summary>Julian Date to Gregorian year, month, day, and fraction of a day.</summary>
        public static int Jd2cal(double dj1, double dj2, out int iy, out int im, out int id, out double fd)
        {
            const double DJMIN = -68569.5;
            const double DJMAX = 1e9;

            iy = 0; im = 0; id = 0; fd = 0;

            double dj = dj1 + dj2;
            if (dj < DJMIN || dj > DJMAX) return -1;

            double d = Math.Round(dj1);
            double f1 = dj1 - d;
            long jd = (long)d;
            d = Math.Round(dj2);
            double f2 = dj2 - d;
            jd += (long)d;

            double s = 0.5;
            double cs = 0.0;
            double x, t;

            x = f1;
            t = s + x;
            cs += Math.Abs(s) >= Math.Abs(x) ? (s - t) + x : (x - t) + s;
            s = t;
            if (s >= 1.0) { jd++; s -= 1.0; }

            x = f2;
            t = s + x;
            cs += Math.Abs(s) >= Math.Abs(x) ? (s - t) + x : (x - t) + s;
            s = t;
            if (s >= 1.0) { jd++; s -= 1.0; }

            double f = s + cs;
            cs = f - s;

            if (f < 0.0)
            {
                f = s + 1.0;
                cs += (1.0 - f) + s;
                s = f;
                f = s + cs;
                cs = f - s;
                jd--;
            }

            if ((f - 1.0) >= -double.Epsilon / 4.0)
            {
                t = s - 1.0;
                cs += (s - t) - 1.0;
                s = t;
                f = s + cs;
                if (-double.Epsilon / 2.0 < f)
                {
                    jd++;
                    f = Math.Max(f, 0.0);
                }
            }

            long l = jd + 68569L;
            long n = (4L * l) / 146097L;
            l -= (146097L * n + 3L) / 4L;
            long ii = (4000L * (l + 1L)) / 1461001L;
            l -= (1461L * ii) / 4L - 31L;
            long k = (80L * l) / 2447L;
            id = (int)(l - (2447L * k) / 80L);
            l = k / 11L;
            im = (int)(k + 2L - 12L * l);
            iy = (int)(100L * (n - 49L) + ii + l);
            fd = f;

            return 0;
        }

        /// <summary>
        /// For a given UTC date, calculate Delta(AT) = TAI - UTC.
        /// Includes full leap second table and pre-1972 drift terms.
        /// </summary>
        public static int Dat(int iy, int im, int id, double fd, out double deltat)
        {
            // Release year for the table
            const int IYV = 2023;

            // Pre-1972 drift terms
            ReadOnlySpan<double> driftMjd0 =
            [
                37300.0, 37300.0, 37300.0, 37665.0, 37665.0,
                38761.0, 38761.0, 38761.0, 38761.0, 38761.0,
                38761.0, 38761.0, 39126.0, 39126.0
            ];
            ReadOnlySpan<double> driftRate =
            [
                0.0012960, 0.0012960, 0.0012960, 0.0011232, 0.0011232,
                0.0012960, 0.0012960, 0.0012960, 0.0012960, 0.0012960,
                0.0012960, 0.0012960, 0.0025920, 0.0025920
            ];

            const int NERA1 = 14;

            // Leap second table (year, month, delat)
            ReadOnlySpan<int> changeYear =
            [
                1960, 1961, 1961, 1962, 1963, 1964, 1964, 1964,
                1965, 1965, 1965, 1965, 1966, 1968, 1972, 1972,
                1973, 1974, 1975, 1976, 1977, 1978, 1979, 1980,
                1981, 1982, 1983, 1985, 1988, 1990, 1991, 1992,
                1993, 1994, 1996, 1997, 1999, 2006, 2009, 2012,
                2015, 2017
            ];
            ReadOnlySpan<int> changeMonth =
            [
                1, 1, 8, 1, 11, 1, 4, 9,
                1, 3, 7, 9, 1, 2, 1, 7,
                1, 1, 1, 1, 1, 1, 1, 1,
                7, 7, 7, 7, 1, 1, 1, 7,
                7, 7, 1, 7, 1, 1, 1, 7,
                7, 1
            ];
            ReadOnlySpan<double> changeDelat =
            [
                1.4178180, 1.4228180, 1.3728180, 1.8458580, 1.9458580,
                3.2401300, 3.3401300, 3.4401300, 3.5401300, 3.6401300,
                3.7401300, 3.8401300, 4.3131700, 4.2131700,
                10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0,
                18.0, 19.0, 20.0, 21.0, 22.0, 23.0, 24.0, 25.0,
                26.0, 27.0, 28.0, 29.0, 30.0, 31.0, 32.0, 33.0,
                34.0, 35.0, 36.0, 37.0
            ];

            int NDAT = changeYear.Length;

            deltat = 0.0;
            if (fd < 0.0 || fd > 1.0) return -4;

            int j = Cal2jd(iy, im, id, out _, out double djm);
            if (j < 0) return j;

            if (iy < changeYear[0]) return 1;
            if (iy > IYV + 5) j = 1;

            int m = 12 * iy + im;
            int i;
            for (i = NDAT - 1; i >= 0; i--)
            {
                if (m >= 12 * changeYear[i] + changeMonth[i]) break;
            }

            if (i < 0) return -5;

            double da = changeDelat[i];
            if (i < NERA1) da += (djm + fd - driftMjd0[i]) * driftRate[i];

            deltat = da;
            return j;
        }

        // =====================================================================
        // Phase 3: Coordinate transform helpers
        // =====================================================================

        /// <summary>Proper motion and parallax.</summary>
        public static void Pmpx(double rc, double dc, double pr, double pd,
                                double px, double rv, double pmt,
                                ReadOnlySpan<double> pob, Span<double> pco)
        {
            const double VF = DAYSEC * DJM / DAU;
            const double AULTY = AULT / DAYSEC / DJY;

            double sr = Math.Sin(rc), cr = Math.Cos(rc);
            double sd = Math.Sin(dc), cd = Math.Cos(dc);
            double x = cr * cd, y = sr * cd, z = sd;

            Span<double> p = stackalloc double[3];
            p[0] = x; p[1] = y; p[2] = z;

            double dt = pmt + Pdp(p, pob) * AULTY;
            double pxr = px * DAS2R;
            double w = VF * rv * pxr;
            double pdz = pd * z;

            Span<double> pm = stackalloc double[3];
            pm[0] = -pr * y - pdz * cr + w * x;
            pm[1] = pr * x - pdz * sr + w * y;
            pm[2] = pd * cd + w * z;

            for (int i = 0; i < 3; i++)
                p[i] += dt * pm[i] - pxr * pob[i];

            Pn(p, out _, pco);
        }

        /// <summary>Light deflection by a single solar-system body (general).</summary>
        public static void Ld(double bm, ReadOnlySpan<double> p, ReadOnlySpan<double> q,
                              ReadOnlySpan<double> e, double em, double dlim, Span<double> p1)
        {
            Span<double> qpe = stackalloc double[3];
            for (int i = 0; i < 3; i++) qpe[i] = q[i] + e[i];
            double qdqpe = Pdp(q, qpe);
            double w = bm * SRS / em / Math.Max(qdqpe, dlim);

            Span<double> eq = stackalloc double[3];
            Span<double> peq = stackalloc double[3];
            Pxp(e, q, eq);
            Pxp(p, eq, peq);

            for (int i = 0; i < 3; i++)
                p1[i] = p[i] + w * peq[i];
        }

        /// <summary>Light deflection by the Sun.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Ldsun(ReadOnlySpan<double> p, ReadOnlySpan<double> e, double em, Span<double> p1)
        {
            double em2 = em * em;
            if (em2 < 1.0) em2 = 1.0;
            double dlim = 1e-6 / (em2 > 1.0 ? em2 : 1.0);
            Ld(1.0, p, p, e, em, dlim, p1);
        }

        /// <summary>Stellar aberration.</summary>
        public static void Ab(ReadOnlySpan<double> pnat, ReadOnlySpan<double> v,
                              double s, double bm1, Span<double> ppr)
        {
            double pdv = Pdp(pnat, v);
            double w1 = 1.0 + pdv / (1.0 + bm1);
            double w2 = SRS / s;
            double r2 = 0.0;
            Span<double> p = stackalloc double[3];
            for (int i = 0; i < 3; i++)
            {
                double w = pnat[i] * bm1 + w1 * v[i] + w2 * (v[i] - pdv * pnat[i]);
                p[i] = w;
                r2 += w * w;
            }
            double r = Math.Sqrt(r2);
            for (int i = 0; i < 3; i++)
                ppr[i] = p[i] / r;
        }

        /// <summary>Reference ellipsoid parameters.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Eform(int n, out double a, out double f)
        {
            switch (n)
            {
                case WGS84: a = 6378137.0; f = 1.0 / 298.257223563; return 0;
                case GRS80: a = 6378137.0; f = 1.0 / 298.257222101; return 0;
                case WGS72: a = 6378135.0; f = 1.0 / 298.26; return 0;
                default: a = 0; f = 0; return -1;
            }
        }

        /// <summary>Geodetic to geocentric (general ellipsoid).</summary>
        public static int Gd2gce(double a, double f, double elong, double phi, double height, Span<double> xyz)
        {
            double sp = Math.Sin(phi), cp = Math.Cos(phi);
            double w = 1.0 - f;
            w *= w;
            double d = cp * cp + w * sp * sp;
            if (d <= 0.0) return -1;
            double ac = a / Math.Sqrt(d);
            double acs = w * ac;
            double r = (ac + height) * cp;
            xyz[0] = r * Math.Cos(elong);
            xyz[1] = r * Math.Sin(elong);
            xyz[2] = (acs + height) * sp;
            return 0;
        }

        /// <summary>Geodetic to geocentric (WGS84, GRS80, or WGS72).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Gd2gc(int n, double elong, double phi, double height, Span<double> xyz)
        {
            int j = Eform(n, out double a, out double f);
            if (j == 0)
            {
                j = Gd2gce(a, f, elong, phi, height, xyz);
                if (j != 0) j = -2;
            }
            if (j != 0) Zp(xyz);
            return j;
        }

        /// <summary>Polar-motion matrix (TIRS to ITRS).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pom00(double xp, double yp, double sp, Span<double> rpom)
        {
            Ir(rpom);
            Rz(sp, rpom);
            Ry(-xp, rpom);
            Rx(-yp, rpom);
        }

        /// <summary>Position and velocity of a terrestrial observing station.</summary>
        public static void Pvtob(double elong, double phi, double hm,
                                 double xp, double yp, double sp, double theta,
                                 Span<double> pv)
        {
            const double OM = 1.00273781191135448 * D2PI / DAYSEC;

            Span<double> xyzm = stackalloc double[3];
            Span<double> rpm = stackalloc double[9];
            Span<double> xyz = stackalloc double[3];

            Gd2gc(WGS84, elong, phi, hm, xyzm);
            Pom00(xp, yp, sp, rpm);
            Trxp(rpm, xyzm, xyz);

            double x = xyz[0], y = xyz[1], z = xyz[2];
            double sc = Math.Sin(theta), cc = Math.Cos(theta);

            // Position
            pv[0] = cc * x - sc * y;
            pv[1] = sc * x + cc * y;
            pv[2] = z;
            // Velocity
            pv[3] = OM * (-sc * x - cc * y);
            pv[4] = OM * (cc * x - sc * y);
            pv[5] = 0.0;
        }

        /// <summary>Determine the constants A and B in the atmospheric refraction model.</summary>
        public static void Refco(double phpa, double tc, double rh, double wl,
                                 out double refa, out double refb)
        {
            bool optic = wl <= 100.0;
            double t = Math.Clamp(tc, -150.0, 200.0);
            double p = Math.Clamp(phpa, 0.0, 10000.0);
            double r = Math.Clamp(rh, 0.0, 1.0);
            double w = Math.Clamp(wl, 0.1, 1e6);

            double pw;
            if (p > 0.0)
            {
                double ps = Math.Pow(10.0, (0.7859 + 0.03477 * t) / (1.0 + 0.00412 * t))
                            * (1.0 + p * (4.5e-6 + 6e-10 * t * t));
                pw = r * ps / (1.0 - (1.0 - r) * ps / p);
            }
            else
            {
                pw = 0.0;
            }

            double tk = t + 273.15;
            double gamma, beta;
            if (optic)
            {
                double wlsq = w * w;
                gamma = ((77.53484e-6 + (4.39108e-7 + 3.666e-9 / wlsq) / wlsq) * p
                         - 11.2684e-6 * pw) / tk;
            }
            else
            {
                gamma = (77.6890e-6 * p - (6.3938e-6 - 0.375463 / tk) * pw) / tk;
            }

            beta = 4.4474e-6 * tk;
            if (!optic) beta -= 0.0074 * pw * beta;

            refa = gamma * (1.0 - beta);
            refb = -gamma * (beta - gamma / 2.0);
        }

        /// <summary>TIO locator s', positioning the Terrestrial Intermediate Origin on the equator of the CIP.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Sp00(double date1, double date2)
        {
            double t = ((date1 - DJ00) + date2) / DJC;
            return -47e-6 * t * DAS2R;
        }

        /// <summary>Equation of the origins, given the classical NPB matrix and the quantity s.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Eors(ReadOnlySpan<double> rnpb, double s)
        {
            double x = rnpb[6]; // [2][0]
            double ax = x / (1.0 + rnpb[8]); // [2][2]
            double xs = 1.0 - ax * x;
            double ys = -ax * rnpb[7]; // [2][1]
            double zs = -x;
            double pp = rnpb[0] * xs + rnpb[1] * ys + rnpb[2] * zs; // [0][0..2]
            double q = rnpb[3] * xs + rnpb[4] * ys + rnpb[5] * zs; // [1][0..2]
            return (pp != 0.0 || q != 0.0) ? s - Math.Atan2(q, pp) : s;
        }

        /// <summary>Accumulate Epv00 Poisson series for one component.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Epv00Accumulate(ReadOnlySpan<double> data, int count, double t, int power, Span<double> target, int comp)
        {
            double xyz = 0.0;
            double xyzd = 0.0;
            for (int j = count - 1; j >= 0; j--)
            {
                double a = data[j * 3];
                double b = data[j * 3 + 1];
                double c = data[j * 3 + 2];
                double angle = b + c * t;
                double cosA = Math.Cos(angle);
                double sinA = Math.Sin(angle);
                xyz += a * cosA;
                xyzd -= a * c * sinA;
            }

            switch (power)
            {
                case 1:
                    xyzd = xyzd * t + xyz;
                    xyz *= t;
                    break;
                case 2:
                    xyzd = (xyzd * t + 2.0 * xyz) * t;
                    xyz *= t * t;
                    break;
            }

            target[comp] += xyz;
            target[comp + 3] += xyzd / DJM;
        }

        /// <summary>Earth position and velocity, heliocentric and barycentric, with respect to the Barycentric Celestial Reference System.</summary>
        public static int Epv00(double date1, double date2, Span<double> pvh, Span<double> pvb)
        {
            // pvh[0..2] = heliocentric position, pvh[3..5] = heliocentric velocity
            // pvb[0..2] = barycentric position, pvb[3..5] = barycentric velocity
            double t = ((date1 - DJ00) + date2) / DJM;

            // Rotation matrix: ecliptic to BCRS (SOFA epv00.c)
            const double am12 = 0.000000211284;
            const double am13 = -0.000000091603;
            const double am21 = -0.000000230286;
            const double am22 = 0.917482137087;
            const double am23 = -0.397776982902;
            const double am32 = 0.397776982902;
            const double am33 = 0.917482137087;

            // Accumulate: 6 heliocentric values (pos xyz, vel xyz), 6 SSB values
            Span<double> eh = stackalloc double[6]; // heliocentric ecliptic
            Span<double> es = stackalloc double[6]; // SSB

            // Heliocentric T^0
            Epv00Accumulate(Epv00Coefficients.E0x, Epv00Coefficients.E0xCount, t, 0, eh, 0);
            Epv00Accumulate(Epv00Coefficients.E0y, Epv00Coefficients.E0yCount, t, 0, eh, 1);
            Epv00Accumulate(Epv00Coefficients.E0z, Epv00Coefficients.E0zCount, t, 0, eh, 2);
            // Heliocentric T^1
            Epv00Accumulate(Epv00Coefficients.E1x, Epv00Coefficients.E1xCount, t, 1, eh, 0);
            Epv00Accumulate(Epv00Coefficients.E1y, Epv00Coefficients.E1yCount, t, 1, eh, 1);
            Epv00Accumulate(Epv00Coefficients.E1z, Epv00Coefficients.E1zCount, t, 1, eh, 2);
            // Heliocentric T^2
            Epv00Accumulate(Epv00Coefficients.E2x, Epv00Coefficients.E2xCount, t, 2, eh, 0);
            Epv00Accumulate(Epv00Coefficients.E2y, Epv00Coefficients.E2yCount, t, 2, eh, 1);
            Epv00Accumulate(Epv00Coefficients.E2z, Epv00Coefficients.E2zCount, t, 2, eh, 2);
            // SSB T^0
            Epv00Accumulate(Epv00Coefficients.S0x, Epv00Coefficients.S0xCount, t, 0, es, 0);
            Epv00Accumulate(Epv00Coefficients.S0y, Epv00Coefficients.S0yCount, t, 0, es, 1);
            Epv00Accumulate(Epv00Coefficients.S0z, Epv00Coefficients.S0zCount, t, 0, es, 2);
            // SSB T^1
            Epv00Accumulate(Epv00Coefficients.S1x, Epv00Coefficients.S1xCount, t, 1, es, 0);
            Epv00Accumulate(Epv00Coefficients.S1y, Epv00Coefficients.S1yCount, t, 1, es, 1);
            Epv00Accumulate(Epv00Coefficients.S1z, Epv00Coefficients.S1zCount, t, 1, es, 2);
            // SSB T^2
            Epv00Accumulate(Epv00Coefficients.S2x, Epv00Coefficients.S2xCount, t, 2, es, 0);
            Epv00Accumulate(Epv00Coefficients.S2y, Epv00Coefficients.S2yCount, t, 2, es, 1);
            Epv00Accumulate(Epv00Coefficients.S2z, Epv00Coefficients.S2zCount, t, 2, es, 2);

            // Rotate from ecliptic to BCRS
            // Heliocentric position
            pvh[0] = eh[0] + am12 * eh[1] + am13 * eh[2];
            pvh[1] = am21 * eh[0] + am22 * eh[1] + am23 * eh[2];
            pvh[2] = am32 * eh[1] + am33 * eh[2];

            // Heliocentric velocity
            pvh[3] = eh[3] + am12 * eh[4] + am13 * eh[5];
            pvh[4] = am21 * eh[3] + am22 * eh[4] + am23 * eh[5];
            pvh[5] = am32 * eh[4] + am33 * eh[5];

            // Barycentric = heliocentric + Sun-to-SSB
            double sx = es[0] + am12 * es[1] + am13 * es[2];
            double sy = am21 * es[0] + am22 * es[1] + am23 * es[2];
            double sz = am32 * es[1] + am33 * es[2];
            pvb[0] = pvh[0] + sx;
            pvb[1] = pvh[1] + sy;
            pvb[2] = pvh[2] + sz;

            double svx = es[3] + am12 * es[4] + am13 * es[5];
            double svy = am21 * es[3] + am22 * es[4] + am23 * es[5];
            double svz = am32 * es[4] + am33 * es[5];
            pvb[3] = pvh[3] + svx;
            pvb[4] = pvh[4] + svy;
            pvb[5] = pvh[5] + svz;

            // Return status: 0 if inside valid range, 1 if outside
            return (Math.Abs(t) <= 100.0) ? 0 : 1;
        }

        // =====================================================================
        // Fundamental arguments (lunisolar and planetary)
        // =====================================================================

        /// <summary>Mean anomaly of the Moon (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fal03(double t)
        {
            return ((485868.249036 + t * (1717915923.2178 + t * (31.8792 + t * (0.051635 + t * (-0.00024470))))) % TURNAS) * DAS2R;
        }

        /// <summary>Mean anomaly of the Sun (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Falp03(double t)
        {
            return ((1287104.793048 + t * (129596581.0481 + t * (-0.5532 + t * (0.000136 + t * (-0.00001149))))) % TURNAS) * DAS2R;
        }

        /// <summary>Mean argument of the latitude of the Moon (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Faf03(double t)
        {
            return ((335779.526232 + t * (1739527262.8478 + t * (-12.7512 + t * (-0.001037 + t * 0.00000417)))) % TURNAS) * DAS2R;
        }

        /// <summary>Mean elongation of the Moon from the Sun (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fad03(double t)
        {
            return ((1072260.703692 + t * (1602961601.2090 + t * (-6.3706 + t * (0.006593 + t * (-0.00003169))))) % TURNAS) * DAS2R;
        }

        /// <summary>Mean longitude of the Moon's ascending node (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Faom03(double t)
        {
            return ((450160.398036 + t * (-6962890.5431 + t * (7.4722 + t * (0.007702 + t * (-0.00005939))))) % TURNAS) * DAS2R;
        }

        /// <summary>Mean longitude of Venus (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fave03(double t) => (3.176146697 + 1021.3285546211 * t) % D2PI;

        /// <summary>Mean longitude of Earth (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fae03(double t) => (1.753470314 + 628.3075849991 * t) % D2PI;

        /// <summary>General accumulated precession in longitude (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fapa03(double t) => (0.024381750 + 0.00000538691 * t) * t;

        /// <summary>Mean longitude of Mercury (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fame03(double t) => (4.402608842 + 2608.7903141574 * t) % D2PI;

        /// <summary>Mean longitude of Mars (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fama03(double t) => (6.203480913 + 334.0612426700 * t) % D2PI;

        /// <summary>Mean longitude of Jupiter (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Faju03(double t) => (0.599546497 + 52.9690962641 * t) % D2PI;

        /// <summary>Mean longitude of Saturn (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fasa03(double t) => (0.874016757 + 21.3299104960 * t) % D2PI;

        /// <summary>Mean longitude of Uranus (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Faur03(double t) => (5.481293872 + 7.4781598567 * t) % D2PI;

        /// <summary>Mean longitude of Neptune (IERS 2003).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Fane03(double t) => (5.311886287 + 3.8133035638 * t) % D2PI;

        // =====================================================================
        // Phase 4: Precession-Nutation
        // =====================================================================

        /// <summary>Nutation, IAU 2000A model (MHB2000 luni-solar and planetary nutation with IERS 2003 corrections).</summary>
        public static void Nut00a(double date1, double date2, out double dpsi, out double deps)
        {
            const double U2R = DAS2R / 1e7;

            double t = ((date1 - DJ00) + date2) / DJC;

            // Fundamental (Delaunay) arguments
            double el = Fal03(t);
            double elp = Falp03(t);
            double f = Faf03(t);
            double d = Fad03(t);
            double om = Faom03(t);

            // Luni-solar nutation
            double dp = 0.0, de = 0.0;
            var lsNfa = NutationCoefficients.LuniSolarNfa;
            var lsCoeffs = NutationCoefficients.LuniSolarCoeffs;

            for (int i = NutationCoefficients.LuniSolarCount - 1; i >= 0; i--)
            {
                int ni = i * 5;
                double arg = (double)lsNfa[ni] * el
                           + (double)lsNfa[ni + 1] * elp
                           + (double)lsNfa[ni + 2] * f
                           + (double)lsNfa[ni + 3] * d
                           + (double)lsNfa[ni + 4] * om;

                int ci = i * 6;
                double sinArg = Math.Sin(arg);
                double cosArg = Math.Cos(arg);
                dp += (lsCoeffs[ci] + lsCoeffs[ci + 1] * t) * sinArg + lsCoeffs[ci + 2] * cosArg;
                de += (lsCoeffs[ci + 3] + lsCoeffs[ci + 4] * t) * cosArg + lsCoeffs[ci + 5] * sinArg;
            }

            // Planetary arguments
            double alme = Fame03(t);
            double alve = Fave03(t);
            double alea = Fae03(t);
            double alma = Fama03(t);
            double alju = Faju03(t);
            double alsa = Fasa03(t);
            double alur = Faur03(t);
            double alne = Fane03(t);
            double apa = Fapa03(t);

            var plNfa = NutationCoefficients.PlanetaryNfa;
            var plCoeffs = NutationCoefficients.PlanetaryCoeffs;

            for (int i = NutationCoefficients.PlanetaryCount - 1; i >= 0; i--)
            {
                int ni = i * NutationCoefficients.PlanetaryNfaStride;
                double arg = (double)plNfa[ni] * el
                           + (double)plNfa[ni + 1] * f
                           + (double)plNfa[ni + 2] * d
                           + (double)plNfa[ni + 3] * om
                           + (double)plNfa[ni + 4] * alme
                           + (double)plNfa[ni + 5] * alve
                           + (double)plNfa[ni + 6] * alea
                           + (double)plNfa[ni + 7] * alma
                           + (double)plNfa[ni + 8] * alju
                           + (double)plNfa[ni + 9] * alsa
                           + (double)plNfa[ni + 10] * alur
                           + (double)plNfa[ni + 11] * alne
                           + (double)plNfa[ni + 12] * apa;

                int ci = i * NutationCoefficients.PlanetaryCoeffsStride;
                double sinArg = Math.Sin(arg);
                double cosArg = Math.Cos(arg);
                dp += plCoeffs[ci] * sinArg + plCoeffs[ci + 1] * cosArg;
                de += plCoeffs[ci + 2] * sinArg + plCoeffs[ci + 3] * cosArg;
            }

            dpsi = dp * U2R;
            deps = de * U2R;
        }

        /// <summary>IAU 2006/2000A nutation (calls Nut00a with small corrections).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Nut06a(double date1, double date2, out double dpsi, out double deps)
        {
            double t = ((date1 - DJ00) + date2) / DJC;
            double fj2 = -2.7774e-6 * t;
            Nut00a(date1, date2, out double dp, out double de);
            dpsi = dp + dp * (0.4697e-6 + fj2);
            deps = de + de * fj2;
        }

        /// <summary>Mean obliquity of the ecliptic, IAU 2006 precession model.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Obl06(double date1, double date2)
        {
            double t = ((date1 - DJ00) + date2) / DJC;
            return (84381.406 +
                   (-46.836769 +
                   (-0.0001831 +
                   (0.00200340 +
                   (-0.000000576 +
                   (-0.0000000434) * t) * t) * t) * t) * t) * DAS2R;
        }

        /// <summary>Precession angles, IAU 2006 (Fukushima-Williams 4-angle formulation).</summary>
        public static void Pfw06(double date1, double date2,
                                 out double gamb, out double phib, out double psib, out double epsa)
        {
            double t = ((date1 - DJ00) + date2) / DJC;

            gamb = (-0.052928 +
                   (10.556378 +
                   (0.4932044 +
                   (-0.00031238 +
                   (-0.000002788 +
                   (0.0000000260)
                   * t) * t) * t) * t) * t) * DAS2R;

            phib = (84381.412819 +
                   (-46.811016 +
                   (0.0511268 +
                   (0.00053289 +
                   (-0.000000440 +
                   (-0.0000000176)
                   * t) * t) * t) * t) * t) * DAS2R;

            psib = (-0.041775 +
                   (5038.481484 +
                   (1.5584175 +
                   (-0.00018522 +
                   (-0.000026452 +
                   (-0.0000000148)
                   * t) * t) * t) * t) * t) * DAS2R;

            epsa = Obl06(date1, date2);
        }

        /// <summary>Form rotation matrix given the Fukushima-Williams angles.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Fw2m(double gamb, double phib, double psi, double eps, Span<double> r)
        {
            Ir(r);
            Rz(gamb, r);
            Rx(phib, r);
            Rz(-psi, r);
            Rx(-eps, r);
        }

        /// <summary>Bias-precession-nutation matrix, IAU 2006/2000A.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Pnm06a(double date1, double date2, Span<double> rbpn)
        {
            Pfw06(date1, date2, out double gamb, out double phib, out double psib, out double epsa);
            Nut06a(date1, date2, out double dp, out double de);
            Fw2m(gamb, phib, psib + dp, epsa + de, rbpn);
        }

        /// <summary>Extract CIP X,Y coordinates from the bias-precession-nutation matrix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bpn2xy(ReadOnlySpan<double> rbpn, out double x, out double y)
        {
            x = rbpn[6]; // [2][0]
            y = rbpn[7]; // [2][1]
        }

        /// <summary>CIO locator s, given CIP X,Y, IAU 2006.</summary>
        public static double S06(double date1, double date2, double x, double y)
        {
            double t = ((date1 - DJ00) + date2) / DJC;

            // Fundamental arguments
            Span<double> fa = stackalloc double[8];
            fa[0] = Fal03(t);
            fa[1] = Falp03(t);
            fa[2] = Faf03(t);
            fa[3] = Fad03(t);
            fa[4] = Faom03(t);
            fa[5] = Fave03(t);
            fa[6] = Fae03(t);
            fa[7] = Fapa03(t);

            // Polynomial coefficients
            double w0 = 94.00e-6;
            double w1 = 3808.65e-6;
            double w2 = -122.68e-6;
            double w3 = -72574.11e-6;
            double w4 = 27.98e-6;
            double w5 = 15.62e-6;

            // s0 terms (33)
            ReadOnlySpan<int> s0nfa =
            [
                0,0,0,0,1,0,0,0, 0,0,0,0,2,0,0,0, 0,0,2,-2,3,0,0,0,
                0,0,2,-2,1,0,0,0, 0,0,2,-2,2,0,0,0, 0,0,2,0,3,0,0,0,
                0,0,2,0,1,0,0,0, 0,0,0,0,3,0,0,0, 0,1,0,0,1,0,0,0,
                0,1,0,0,-1,0,0,0, 1,0,0,0,-1,0,0,0, 1,0,0,0,1,0,0,0,
                0,1,2,-2,3,0,0,0, 0,1,2,-2,1,0,0,0, 0,0,4,-4,4,0,0,0,
                0,0,1,-1,1,-8,12,0, 0,0,2,0,0,0,0,0, 0,0,2,0,2,0,0,0,
                1,0,2,0,3,0,0,0, 1,0,2,0,1,0,0,0, 0,0,2,-2,0,0,0,0,
                0,1,-2,2,-3,0,0,0, 0,1,-2,2,-1,0,0,0, 0,0,0,0,0,8,-13,-1,
                0,0,0,2,0,0,0,0, 2,0,-2,0,-1,0,0,0, 0,1,2,-2,2,0,0,0,
                1,0,0,-2,1,0,0,0, 1,0,0,-2,-1,0,0,0, 0,0,4,-2,4,0,0,0,
                0,0,2,-2,4,0,0,0, 1,0,-2,0,-3,0,0,0, 1,0,-2,0,-1,0,0,0
            ];
            ReadOnlySpan<double> s0sc =
            [
                -2640.73e-6, 0.39e-6, -63.53e-6, 0.02e-6,
                -11.75e-6, -0.01e-6, -11.21e-6, -0.01e-6,
                4.57e-6, 0.00e-6, -2.02e-6, 0.00e-6,
                -1.98e-6, 0.00e-6, 1.72e-6, 0.00e-6,
                1.41e-6, 0.01e-6, 1.26e-6, 0.01e-6,
                0.63e-6, 0.00e-6, 0.63e-6, 0.00e-6,
                -0.46e-6, 0.00e-6, -0.45e-6, 0.00e-6,
                -0.36e-6, 0.00e-6, 0.24e-6, 0.12e-6,
                -0.32e-6, 0.00e-6, -0.28e-6, 0.00e-6,
                -0.27e-6, 0.00e-6, -0.26e-6, 0.00e-6,
                0.21e-6, 0.00e-6, -0.19e-6, 0.00e-6,
                -0.18e-6, 0.00e-6, 0.10e-6, -0.05e-6,
                -0.15e-6, 0.00e-6, 0.14e-6, 0.00e-6,
                0.14e-6, 0.00e-6, -0.14e-6, 0.00e-6,
                -0.14e-6, 0.00e-6, -0.13e-6, 0.00e-6,
                0.11e-6, 0.00e-6, -0.11e-6, 0.00e-6,
                -0.11e-6, 0.00e-6
            ];
            for (int i = 32; i >= 0; i--)
            {
                double a = 0.0;
                for (int j = 0; j < 8; j++) a += s0nfa[i * 8 + j] * fa[j];
                w0 += s0sc[i * 2] * Math.Sin(a) + s0sc[i * 2 + 1] * Math.Cos(a);
            }

            // s1 terms (3)
            ReadOnlySpan<int> s1nfa = [0,0,0,0,2,0,0,0, 0,0,0,0,1,0,0,0, 0,0,2,-2,3,0,0,0];
            ReadOnlySpan<double> s1sc = [-0.07e-6, 3.57e-6, 1.73e-6, -0.03e-6, 0.00e-6, 0.48e-6];
            for (int i = 2; i >= 0; i--)
            {
                double a = 0.0;
                for (int j = 0; j < 8; j++) a += s1nfa[i * 8 + j] * fa[j];
                w1 += s1sc[i * 2] * Math.Sin(a) + s1sc[i * 2 + 1] * Math.Cos(a);
            }

            // s2 terms (25)
            ReadOnlySpan<int> s2nfa =
            [
                0,0,0,0,1,0,0,0, 0,0,2,-2,2,0,0,0, 0,0,2,0,2,0,0,0,
                0,0,0,0,2,0,0,0, 0,1,0,0,0,0,0,0, 1,0,0,0,0,0,0,0,
                0,1,2,-2,2,0,0,0, 0,0,2,0,1,0,0,0, 1,0,2,0,2,0,0,0,
                0,1,-2,2,-2,0,0,0, 1,0,0,-2,0,0,0,0, 0,0,2,-2,1,0,0,0,
                1,0,-2,0,-2,0,0,0, 0,0,0,2,0,0,0,0, 1,0,0,0,1,0,0,0,
                1,0,-2,-2,-2,0,0,0, 1,0,0,0,-1,0,0,0, 1,0,2,0,1,0,0,0,
                2,0,0,-2,0,0,0,0, 2,0,-2,0,-1,0,0,0, 0,0,2,2,2,0,0,0,
                2,0,2,0,2,0,0,0, 2,0,0,0,0,0,0,0, 1,0,2,-2,2,0,0,0,
                0,0,2,0,0,0,0,0
            ];
            ReadOnlySpan<double> s2sc =
            [
                743.52e-6, -0.17e-6, 56.91e-6, 0.06e-6, 9.84e-6, -0.01e-6,
                -8.85e-6, 0.01e-6, -6.38e-6, -0.05e-6, -3.07e-6, 0.00e-6,
                2.23e-6, 0.00e-6, 1.67e-6, 0.00e-6, 1.30e-6, 0.00e-6,
                0.93e-6, 0.00e-6, 0.68e-6, 0.00e-6, -0.55e-6, 0.00e-6,
                0.53e-6, 0.00e-6, -0.27e-6, 0.00e-6, -0.27e-6, 0.00e-6,
                -0.26e-6, 0.00e-6, -0.25e-6, 0.00e-6, 0.22e-6, 0.00e-6,
                -0.21e-6, 0.00e-6, 0.20e-6, 0.00e-6, 0.17e-6, 0.00e-6,
                0.13e-6, 0.00e-6, -0.13e-6, 0.00e-6, -0.12e-6, 0.00e-6,
                -0.11e-6, 0.00e-6
            ];
            for (int i = 24; i >= 0; i--)
            {
                double a = 0.0;
                for (int j = 0; j < 8; j++) a += s2nfa[i * 8 + j] * fa[j];
                w2 += s2sc[i * 2] * Math.Sin(a) + s2sc[i * 2 + 1] * Math.Cos(a);
            }

            // s3 terms (4)
            ReadOnlySpan<int> s3nfa = [0,0,0,0,1,0,0,0, 0,0,2,-2,2,0,0,0, 0,0,2,0,2,0,0,0, 0,0,0,0,2,0,0,0];
            ReadOnlySpan<double> s3sc = [0.30e-6, -23.42e-6, -0.03e-6, -1.46e-6, -0.01e-6, -0.25e-6, 0.00e-6, 0.23e-6];
            for (int i = 3; i >= 0; i--)
            {
                double a = 0.0;
                for (int j = 0; j < 8; j++) a += s3nfa[i * 8 + j] * fa[j];
                w3 += s3sc[i * 2] * Math.Sin(a) + s3sc[i * 2 + 1] * Math.Cos(a);
            }

            // s4 terms (1)
            {
                double a = fa[4]; // om
                w4 += -0.26e-6 * Math.Sin(a) + -0.01e-6 * Math.Cos(a);
            }

            return (w0 + (w1 + (w2 + (w3 + (w4 + w5 * t) * t) * t) * t) * t) * DAS2R - x * y / 2.0;
        }

        /// <summary>Form the celestial to intermediate-frame-of-date matrix given CIP X,Y and the CIO locator s.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void C2ixys(double x, double y, double s, Span<double> rc2i)
        {
            double r2 = x * x + y * y;
            double e = (r2 > 0.0) ? Math.Atan2(y, x) : 0.0;
            double dd = Math.Atan(Math.Sqrt(r2 / (1.0 - r2)));
            Ir(rc2i);
            Rz(e, rc2i);
            Ry(dd, rc2i);
            Rz(-(e + s), rc2i);
        }

        // =====================================================================
        // Phase 5: Top-level functions
        // =====================================================================

        /// <summary>
        /// For a geocentric observer, prepare star-independent astrometry
        /// parameters for transformations between ICRS and GCRS coordinates.
        /// </summary>
        public static void Apcg(double date1, double date2,
                                ReadOnlySpan<double> ebpv, ReadOnlySpan<double> ehp,
                                ref Astrom astrom)
        {
            const double CR = AULT / DAYSEC;

            astrom.Pmt = ((date1 - DJ00) + date2) / DJY;

            var eb = astrom.EbSpan;
            var eh = astrom.EhSpan;
            var v = astrom.VSpan;
            var bpn = astrom.BpnSpan;

            Cp(ebpv[..3], eb);
            Pn(ehp, out astrom.Em, eh);

            double v2 = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double w = ebpv[3 + i] * CR;
                v[i] = w;
                v2 += w * w;
            }
            astrom.Bm1 = Math.Sqrt(1.0 - v2);

            Ir(bpn);
        }

        /// <summary>
        /// For a geocentric observer, prepare star-independent astrometry parameters
        /// for transformations between ICRS and GCRS. Use with Atciq, Aticq.
        /// </summary>
        public static void Apci(double date1, double date2,
                                ReadOnlySpan<double> ebpv, ReadOnlySpan<double> ehp,
                                double x, double y, double s,
                                ref Astrom astrom)
        {
            Apcg(date1, date2, ebpv, ehp, ref astrom);
            C2ixys(x, y, s, astrom.BpnSpan);
        }

        /// <summary>
        /// For a geocentric observer, prepare star-independent astrometry parameters
        /// for transformations between ICRS and GCRS. Calls Epv00, Pnm06a, etc.
        /// </summary>
        public static void Apci13(double date1, double date2, ref Astrom astrom, out double eo)
        {
            Span<double> ehpv = stackalloc double[6];
            Span<double> ebpv = stackalloc double[6];
            Epv00(date1, date2, ehpv, ebpv);

            Span<double> r = stackalloc double[9];
            Pnm06a(date1, date2, r);
            Bpn2xy(r, out double x, out double y);
            double s = S06(date1, date2, x, y);

            Apci(date1, date2, ebpv, ehpv[..3], x, y, s, ref astrom);
            eo = Eors(r, s);
        }

        /// <summary>
        /// For an observer whose geocentric position and velocity are known,
        /// prepare star-independent astrometry parameters for transformations
        /// between ICRS and GCRS.
        /// </summary>
        public static void Apcs(double date1, double date2,
                                ReadOnlySpan<double> pv,
                                ReadOnlySpan<double> ebpv, ReadOnlySpan<double> ehp,
                                ref Astrom astrom)
        {
            const double AUDMS = DAU / DAYSEC;
            const double CR = AULT / DAYSEC;

            astrom.Pmt = ((date1 - DJ00) + date2) / DJY;

            var eb = astrom.EbSpan;
            var eh = astrom.EhSpan;
            var v = astrom.VSpan;
            var bpn = astrom.BpnSpan;

            Span<double> pb = stackalloc double[3];
            Span<double> vb = stackalloc double[3];
            Span<double> ph = stackalloc double[3];

            for (int i = 0; i < 3; i++)
            {
                double dp = pv[i] / DAU;
                double dv = pv[3 + i] / AUDMS;
                pb[i] = ebpv[i] + dp;
                vb[i] = ebpv[3 + i] + dv;
                ph[i] = ehp[i] + dp;
            }

            Cp(pb, eb);
            Pn(ph, out astrom.Em, eh);

            double v2 = 0.0;
            for (int i = 0; i < 3; i++)
            {
                double w = vb[i] * CR;
                v[i] = w;
                v2 += w * w;
            }
            astrom.Bm1 = Math.Sqrt(1.0 - v2);

            Ir(bpn);
        }

        /// <summary>
        /// For a terrestrial observer, prepare star-independent astrometry parameters
        /// for transformations between ICRS and observed coordinates.
        /// </summary>
        public static void Apco(double date1, double date2,
                                ReadOnlySpan<double> ebpv, ReadOnlySpan<double> ehp,
                                double x, double y, double s, double theta,
                                double elong, double phi, double hm,
                                double xp, double yp, double sp,
                                double refa, double refb,
                                ref Astrom astrom)
        {
            // Form the rotation matrix: CIRS to apparent [HA,Dec]
            Span<double> r = stackalloc double[9];
            Ir(r);
            Rz(theta + sp, r);
            Ry(-xp, r);
            Rx(-yp, r);
            Rz(elong, r);

            double a = r[0], b = r[1];
            double eral = (a != 0.0 || b != 0.0) ? Math.Atan2(b, a) : 0.0;
            astrom.Eral = eral;

            double c = r[2];
            astrom.Xpl = Math.Atan2(c, Math.Sqrt(a * a + b * b));
            a = r[5]; // [1][2]
            b = r[8]; // [2][2]
            astrom.Ypl = (a != 0.0 || b != 0.0) ? -Math.Atan2(a, b) : 0.0;

            astrom.Along = Anpm(eral - theta);
            astrom.Sphi = Math.Sin(phi);
            astrom.Cphi = Math.Cos(phi);
            astrom.Refa = refa;
            astrom.Refb = refb;
            astrom.Diurab = 0.0;

            // CIP to GCRS matrix
            C2ixys(x, y, s, r);

            // Observer position/velocity
            Span<double> pvc = stackalloc double[6];
            Pvtob(elong, phi, hm, xp, yp, sp, theta, pvc);

            // Rotate to GCRS
            Span<double> pv = stackalloc double[6];
            Trxpv(r, pvc, pv);

            // ICRS-GCRS params
            Apcs(date1, date2, pv, ebpv, ehp, ref astrom);

            // Store BPN matrix
            Cr(r, astrom.BpnSpan);
        }

        /// <summary>
        /// For a terrestrial observer, prepare star-independent astrometry parameters
        /// for transformations between ICRS and observed coordinates. UTC+site based.
        /// </summary>
        public static int Apco13(double utc1, double utc2, double dut1,
                                 double elong, double phi, double hm,
                                 double xp, double yp,
                                 double phpa, double tc, double rh, double wl,
                                 ref Astrom astrom, out double eo)
        {
            eo = 0;
            int j = Utctai(utc1, utc2, out double tai1, out double tai2);
            if (j < 0) return -1;
            Taitt(tai1, tai2, out double tt1, out double tt2);
            j = Utcut1(utc1, utc2, dut1, out double ut11, out double ut12);
            if (j < 0) return -1;

            Span<double> ehpv = stackalloc double[6];
            Span<double> ebpv = stackalloc double[6];
            Epv00(tt1, tt2, ehpv, ebpv);

            Span<double> r = stackalloc double[9];
            Pnm06a(tt1, tt2, r);
            Bpn2xy(r, out double x, out double y);
            double s = S06(tt1, tt2, x, y);
            double theta = Era00(ut11, ut12);
            double spp = Sp00(tt1, tt2);

            Refco(phpa, tc, rh, wl, out double refa, out double refb);

            Apco(tt1, tt2, ebpv, ehpv[..3], x, y, s, theta,
                 elong, phi, hm, xp, yp, spp, refa, refb, ref astrom);

            eo = Eors(r, s);
            return j;
        }

        /// <summary>ICRS RA,Dec to CIRS using the Astrom context (quick, no proper motion/parallax).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Atciq(double rc, double dc,
                                 double pr, double pd, double px, double rv,
                                 in Astrom astrom, out double ri, out double di)
        {
            Span<double> pco = stackalloc double[3];
            Span<double> pnat = stackalloc double[3];
            Span<double> ppr = stackalloc double[3];
            Span<double> pi = stackalloc double[3];

            Pmpx(rc, dc, pr, pd, px, rv, astrom.Pmt, astrom.EbReadOnly, pco);
            Ldsun(pco, astrom.EhReadOnly, astrom.Em, pnat);
            Ab(pnat, astrom.VReadOnly, astrom.Em, astrom.Bm1, ppr);
            Rxp(astrom.BpnReadOnly, ppr, pi);
            C2s(pi, out double w, out di);
            ri = Anp(w);
        }

        /// <summary>CIRS RA,Dec to observed Az,ZD,HA,Dec,RA using the Astrom context.</summary>
        public static void Atioq(double ri, double di, in Astrom astrom,
                                 out double aob, out double zob,
                                 out double hob, out double dob, out double rob)
        {
            const double CELMIN = 1e-6;
            const double SELMIN = 0.05;

            Span<double> v = stackalloc double[3];
            S2c(ri - astrom.Eral, di, v);
            double xx = v[0], yy = v[1], zz = v[2];

            double sx = Math.Sin(astrom.Xpl), cx = Math.Cos(astrom.Xpl);
            double sy = Math.Sin(astrom.Ypl), cy = Math.Cos(astrom.Ypl);
            double xhd = cx * xx + sx * zz;
            double yhd = sx * sy * xx + cy * yy - cx * sy * zz;
            double zhd = -sx * cy * xx + sy * yy + cx * cy * zz;

            double f = 1.0 - astrom.Diurab * yhd;
            double xhdt = f * xhd;
            double yhdt = f * (yhd + astrom.Diurab);
            double zhdt = f * zhd;

            double xaet = astrom.Sphi * xhdt - astrom.Cphi * zhdt;
            double yaet = yhdt;
            double zaet = astrom.Cphi * xhdt + astrom.Sphi * zhdt;

            double azobs = (xaet != 0.0 || yaet != 0.0) ? Math.Atan2(yaet, -xaet) : 0.0;

            double rr = Math.Sqrt(xaet * xaet + yaet * yaet);
            rr = rr > CELMIN ? rr : CELMIN;
            zz = zaet > SELMIN ? zaet : SELMIN;

            double tz = rr / zz;
            double w = astrom.Refb * tz * tz;
            double del = (astrom.Refa + w) * tz / (1.0 + (astrom.Refa + 3.0 * w) / (zz * zz));

            double cosdel = 1.0 - del * del / 2.0;
            f = cosdel - del * zz / rr;
            double xaeo = xaet * f;
            double yaeo = yaet * f;
            double zaeo = cosdel * zaet + del * rr;

            double zdobs = Math.Atan2(Math.Sqrt(xaeo * xaeo + yaeo * yaeo), zaeo);

            v[0] = astrom.Sphi * xaeo + astrom.Cphi * zaeo;
            v[1] = yaeo;
            v[2] = -astrom.Cphi * xaeo + astrom.Sphi * zaeo;

            C2s(v, out double hmobs, out double dcobs);
            double raobs = astrom.Eral + hmobs;

            aob = Anp(azobs);
            zob = zdobs;
            hob = -hmobs;
            dob = dcobs;
            rob = Anp(raobs);
        }

        /// <summary>Observed place to CIRS using the Astrom context.</summary>
        public static void Atoiq(char type, double ob1, double ob2,
                                 in Astrom astrom, out double ri, out double di)
        {
            const double SELMIN = 0.05;

            double sphi = astrom.Sphi, cphi = astrom.Cphi;
            double c1 = ob1, c2 = ob2;
            char cc = char.ToUpperInvariant(type);

            double xaeo, yaeo, zaeo;
            if (cc == 'A')
            {
                double ce = Math.Sin(c2);
                xaeo = -Math.Cos(c1) * ce;
                yaeo = Math.Sin(c1) * ce;
                zaeo = Math.Cos(c2);
            }
            else
            {
                if (cc == 'R') c1 = astrom.Eral - c1;
                Span<double> vv = stackalloc double[3];
                S2c(-c1, c2, vv);
                xaeo = sphi * vv[0] - cphi * vv[2];
                yaeo = vv[1];
                zaeo = cphi * vv[0] + sphi * vv[2];
            }

            double az = (xaeo != 0.0 || yaeo != 0.0) ? Math.Atan2(yaeo, xaeo) : 0.0;
            double sz = Math.Sqrt(xaeo * xaeo + yaeo * yaeo);
            double zdo = Math.Atan2(sz, zaeo);

            double refa = astrom.Refa, refb = astrom.Refb;
            double tz = sz / (zaeo > SELMIN ? zaeo : SELMIN);
            double dref = (refa + refb * tz * tz) * tz;
            double zdt = zdo + dref;

            double ce2 = Math.Sin(zdt);
            double xaet = Math.Cos(az) * ce2;
            double yaet = Math.Sin(az) * ce2;
            double zaet = Math.Cos(zdt);

            double xmhda = sphi * xaet + cphi * zaet;
            double ymhda = yaet;
            double zmhda = -cphi * xaet + sphi * zaet;

            double f = 1.0 + astrom.Diurab * ymhda;
            double xhd = f * xmhda;
            double yhd = f * (ymhda - astrom.Diurab);
            double zhd = f * zmhda;

            double sx = Math.Sin(astrom.Xpl), cx = Math.Cos(astrom.Xpl);
            double sy = Math.Sin(astrom.Ypl), cy = Math.Cos(astrom.Ypl);
            Span<double> v = stackalloc double[3];
            v[0] = cx * xhd + sx * sy * yhd - sx * cy * zhd;
            v[1] = cy * yhd + sy * zhd;
            v[2] = sx * xhd - cx * sy * yhd + cx * cy * zhd;

            C2s(v, out double hma, out di);
            ri = Anp(astrom.Eral + hma);
        }

        /// <summary>ICRS to observed (one-shot, calls Apco13 + Atciq + Atioq).</summary>
        public static int Atco13(double rc, double dc,
                                 double pr, double pd, double px, double rv,
                                 double utc1, double utc2, double dut1,
                                 double elong, double phi, double hm, double xp, double yp,
                                 double phpa, double tc, double rh, double wl,
                                 out double aob, out double zob, out double hob,
                                 out double dob, out double rob, out double eo)
        {
            Astrom astrom = default;
            int j = Apco13(utc1, utc2, dut1, elong, phi, hm, xp, yp,
                           phpa, tc, rh, wl, ref astrom, out eo);
            if (j < 0) { aob = 0; zob = 0; hob = 0; dob = 0; rob = 0; return j; }

            Atciq(rc, dc, pr, pd, px, rv, ref astrom, out double ri, out double di);
            Atioq(ri, di, ref astrom, out aob, out zob, out hob, out dob, out rob);
            return j;
        }

        /// <summary>Observed to ICRS (one-shot, calls Apco13 + Atoiq + Aticq).</summary>
        public static int Atoc13(char type, double ob1, double ob2,
                                 double utc1, double utc2, double dut1,
                                 double elong, double phi, double hm, double xp, double yp,
                                 double phpa, double tc, double rh, double wl,
                                 out double rc, out double dc)
        {
            Astrom astrom = default;
            int j = Apco13(utc1, utc2, dut1, elong, phi, hm, xp, yp,
                           phpa, tc, rh, wl, ref astrom, out _);
            if (j < 0) { rc = 0; dc = 0; return j; }

            Atoiq(type, ob1, ob2, in astrom, out double ri, out double di);
            Aticq(ri, di, in astrom, out rc, out dc);
            return j;
        }

        /// <summary>ICRS to CIRS (one-shot, using TT).</summary>
        public static void Atci13(double rc, double dc,
                                  double pr, double pd, double px, double rv,
                                  double date1, double date2,
                                  out double ri, out double di, out double eo)
        {
            Astrom astrom = default;
            Apci13(date1, date2, ref astrom, out eo);
            Atciq(rc, dc, pr, pd, px, rv, in astrom, out ri, out di);
        }

        /// <summary>CIRS to ICRS (one-shot, using TT).</summary>
        public static void Atic13(double ri, double di,
                                  double date1, double date2,
                                  out double rc, out double dc, out double eo)
        {
            Astrom astrom = default;
            Apci13(date1, date2, ref astrom, out eo);
            Aticq(ri, di, in astrom, out rc, out dc);
        }

        /// <summary>CIRS RA,Dec to ICRS astrometric RA,Dec using the Astrom context (iterative inversion).</summary>
        public static void Aticq(double ri, double di, in Astrom astrom, out double rc, out double dc)
        {
            Span<double> pi = stackalloc double[3];
            Span<double> ppr = stackalloc double[3];
            Span<double> d = stackalloc double[3];
            Span<double> before = stackalloc double[3];
            Span<double> after = stackalloc double[3];
            Span<double> pnat = stackalloc double[3];
            Span<double> pco = stackalloc double[3];

            S2c(ri, di, pi);
            Trxp(astrom.BpnReadOnly, pi, ppr);

            // Aberration: 2 iterations
            Zp(d);
            for (int j = 0; j < 2; j++)
            {
                double r2 = 0.0;
                for (int i = 0; i < 3; i++)
                {
                    double w = ppr[i] - d[i];
                    before[i] = w;
                    r2 += w * w;
                }
                double r = Math.Sqrt(r2);
                for (int i = 0; i < 3; i++) before[i] /= r;

                Ab(before, astrom.VReadOnly, astrom.Em, astrom.Bm1, after);

                r2 = 0.0;
                for (int i = 0; i < 3; i++)
                {
                    d[i] = after[i] - before[i];
                    double w = ppr[i] - d[i];
                    pnat[i] = w;
                    r2 += w * w;
                }
                r = Math.Sqrt(r2);
                for (int i = 0; i < 3; i++) pnat[i] /= r;
            }

            // Light deflection: 5 iterations
            Zp(d);
            for (int j = 0; j < 5; j++)
            {
                double r2 = 0.0;
                for (int i = 0; i < 3; i++)
                {
                    double w = pnat[i] - d[i];
                    before[i] = w;
                    r2 += w * w;
                }
                double r = Math.Sqrt(r2);
                for (int i = 0; i < 3; i++) before[i] /= r;

                Ldsun(before, astrom.EhReadOnly, astrom.Em, after);

                r2 = 0.0;
                for (int i = 0; i < 3; i++)
                {
                    d[i] = after[i] - before[i];
                    double w = pnat[i] - d[i];
                    pco[i] = w;
                    r2 += w * w;
                }
                r = Math.Sqrt(r2);
                for (int i = 0; i < 3; i++) pco[i] /= r;
            }

            C2s(pco, out double w2, out dc);
            rc = Anp(w2);
        }

        /// <summary>Equation of the origins, IAU 2006/2000A.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Eo06a(double date1, double date2)
        {
            Span<double> r = stackalloc double[9];
            Pnm06a(date1, date2, r);
            Bpn2xy(r, out double x, out double y);
            double s = S06(date1, date2, x, y);
            return Eors(r, s);
        }
    }
}
