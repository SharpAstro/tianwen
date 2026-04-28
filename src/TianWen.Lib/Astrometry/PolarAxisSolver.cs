using System;
using TianWen.Lib.Astrometry.SOFA;
using static TianWen.Lib.Astrometry.Constants;
using static TianWen.Lib.Astrometry.SOFA.SOFAHelpers;

namespace TianWen.Lib.Astrometry
{
    /// <summary>
    /// Pure math for SharpCap-style polar alignment: given two plate-solved
    /// frame centres separated by a known pure RA-axis rotation, recovers
    /// the mount's RA-axis orientation in J2000 unit-vector form, and
    /// decomposes the offset against the site's apparent (refraction-
    /// corrected) celestial pole into altitude and azimuth errors.
    ///
    /// Stateless and thread-safe; all operations are static.
    /// </summary>
    public static class PolarAxisSolver
    {
        /// <summary>
        /// Recover the mount's rotation axis in J2000 unit-vector form from
        /// two J2000 unit-vectors related by a pure rotation of
        /// <paramref name="deltaRad"/> radians around the (unknown) axis.
        /// </summary>
        /// <param name="v1">First plate-solve direction (J2000 unit vector).</param>
        /// <param name="v2">Second plate-solve direction (J2000 unit vector).</param>
        /// <param name="deltaRad">Commanded rotation angle in radians; positive
        /// in the right-hand sense around the axis (i.e. v2 = R(axis, deltaRad) * v1).</param>
        /// <param name="axis">Output: recovered rotation axis (J2000 unit vector).</param>
        /// <param name="coneHalfAngleRad">Output: angular distance from axis to v1
        /// (== distance from axis to v2). Used for the chord-angle sanity check.</param>
        /// <returns>True on success; false if v1 and v2 are too close, antipodal,
        /// or if the chord arc exceeds what the rotation could produce (impossible
        /// geometry, indicating non-pure-axis motion or wrong delta).</returns>
        public static bool TryRecoverAxis(
            in Vec3 v1,
            in Vec3 v2,
            double deltaRad,
            out Vec3 axis,
            out double coneHalfAngleRad)
        {
            // Chord angle alpha = arc(v1, v2) on the unit sphere.
            double cosAlpha = Math.Clamp(Vec3.Dot(v1, v2), -1.0, 1.0);
            double alpha = Math.Acos(cosAlpha);

            if (alpha < 1e-9 || deltaRad <= 1e-9)
            {
                axis = default;
                coneHalfAngleRad = 0;
                return false;
            }

            double halfDelta = 0.5 * deltaRad;
            double halfAlpha = 0.5 * alpha;

            // Chord-half-length on a sphere: sin(alpha/2) = sin(theta) * sin(delta/2),
            // where theta is the angular distance from the axis to v1 (cone half-angle).
            // Inverted: sin(theta) = sin(alpha/2) / sin(delta/2).
            double sinTheta = Math.Sin(halfAlpha) / Math.Sin(halfDelta);
            if (sinTheta > 1.0 + 1e-9)
            {
                // Chord arc exceeds the rotation could produce -> impossible geometry.
                axis = default;
                coneHalfAngleRad = 0;
                return false;
            }
            sinTheta = Math.Min(sinTheta, 1.0);
            double cosTheta = Math.Sqrt(Math.Max(0.0, 1.0 - sinTheta * sinTheta));

            // m_hat = (v1 + v2) / |v1 + v2|. |v1 + v2| = 2 cos(alpha/2).
            double cosHalfAlpha = Math.Cos(halfAlpha);
            if (cosHalfAlpha < 1e-9)
            {
                // v1, v2 nearly antipodal -> ill-conditioned.
                axis = default;
                coneHalfAngleRad = 0;
                return false;
            }
            var sum = Vec3.Add(v1, v2);
            var mHat = Vec3.Scale(sum, 1.0 / (2.0 * cosHalfAlpha));

            // n_hat = (v1 x v2) / sin(alpha). Perpendicular to plane(v1, v2),
            // pointing in the direction of positive rotation (right-hand rule).
            var cross = Vec3.Cross(v1, v2);
            double sinAlpha = Math.Sin(alpha);
            if (sinAlpha < 1e-9)
            {
                axis = default;
                coneHalfAngleRad = 0;
                return false;
            }
            var nHat = Vec3.Scale(cross, 1.0 / sinAlpha);

            // The axis lies in the plane perpendicular to (v2 - v1), passing through m_hat.
            // Within that plane, parametrise A = m_hat * cos(gamma) + n_hat * sin(gamma).
            // Constraint A . v1 = cos(theta) gives cos(gamma) = cos(theta) / cos(alpha/2).
            // Sign of sin(gamma) is positive (right-hand rule: A . (v1 x v2) > 0 for positive delta).
            double cosGamma = cosTheta / cosHalfAlpha;
            if (cosGamma > 1.0)
            {
                // Numerical drift; clamp.
                cosGamma = 1.0;
            }
            else if (cosGamma < -1.0)
            {
                cosGamma = -1.0;
            }
            double sinGamma = Math.Sqrt(Math.Max(0.0, 1.0 - cosGamma * cosGamma));

            var a1 = Vec3.Scale(mHat, cosGamma);
            var a2 = Vec3.Scale(nHat, sinGamma);
            axis = Vec3.Normalize(Vec3.Add(a1, a2));
            coneHalfAngleRad = Math.Acos(cosTheta);
            return true;
        }

        /// <summary>
        /// Chord arc length (radians) between two unit vectors on the celestial sphere.
        /// Equivalent to the angular separation between their corresponding sky positions.
        /// </summary>
        public static double ChordAngle(in Vec3 v1, in Vec3 v2) =>
            Math.Acos(Math.Clamp(Vec3.Dot(v1, v2), -1.0, 1.0));

        /// <summary>
        /// Predicted chord arc for a given rotation angle and cone half-angle:
        /// theta_predicted = 2 * asin( sin(delta/2) * sin(coneHalfAngle) ).
        /// Used in the Phase-A sanity check: compare against ChordAngle(v1, v2).
        /// Mismatches > ~5 arcsec indicate mid-rotation slew error, mechanical
        /// wobble, or a miscommanded delta.
        /// </summary>
        public static double PredictedChordAngle(double deltaRad, double coneHalfAngleRad) =>
            2.0 * Math.Asin(Math.Min(1.0, Math.Sin(0.5 * deltaRad) * Math.Sin(coneHalfAngleRad)));

        /// <summary>
        /// Signed rotation angle about <paramref name="axis"/> that takes
        /// <paramref name="v1"/> to <paramref name="v2"/>. Both inputs are
        /// projected onto the plane perpendicular to the axis, then the
        /// angle between the projections is read using the right-hand rule
        /// (sign comes from cross(v1_perp, v2_perp) · axis).
        ///
        /// This is the *measured* mount rotation between the two plate-solves,
        /// independent of the commanded (rate * elapsed) value the orchestrator
        /// uses for the geometric solve. Comparing the two flags rate-error
        /// or sidereal contamination of the rotation duration estimate. Returns
        /// 0 when either projection is degenerate (axis pointing through v1 or v2).
        /// </summary>
        public static double MeasuredRotationAroundAxis(in Vec3 v1, in Vec3 v2, in Vec3 axis)
        {
            var v1DotA = Vec3.Dot(v1, axis);
            var v2DotA = Vec3.Dot(v2, axis);
            var v1PerpX = v1.X - v1DotA * axis.X;
            var v1PerpY = v1.Y - v1DotA * axis.Y;
            var v1PerpZ = v1.Z - v1DotA * axis.Z;
            var v2PerpX = v2.X - v2DotA * axis.X;
            var v2PerpY = v2.Y - v2DotA * axis.Y;
            var v2PerpZ = v2.Z - v2DotA * axis.Z;

            var v1PerpLenSq = v1PerpX * v1PerpX + v1PerpY * v1PerpY + v1PerpZ * v1PerpZ;
            var v2PerpLenSq = v2PerpX * v2PerpX + v2PerpY * v2PerpY + v2PerpZ * v2PerpZ;
            if (v1PerpLenSq < 1e-18 || v2PerpLenSq < 1e-18) return 0.0;

            var dot = v1PerpX * v2PerpX + v1PerpY * v2PerpY + v1PerpZ * v2PerpZ;

            // Right-hand sense: sign(cross(v1_perp, v2_perp) . axis) gives the
            // sign of the rotation that takes v1 -> v2 around `axis`.
            var crossX = v1PerpY * v2PerpZ - v1PerpZ * v2PerpY;
            var crossY = v1PerpZ * v2PerpX - v1PerpX * v2PerpZ;
            var crossZ = v1PerpX * v2PerpY - v1PerpY * v2PerpX;
            var crossDotAxis = crossX * axis.X + crossY * axis.Y + crossZ * axis.Z;

            return Math.Atan2(crossDotAxis, dot);
        }

        /// <summary>
        /// Live-refining axis tracker. Phase A produces (axis A0, v1, v2, delta);
        /// during refining the user adjusts polar knobs which moves the physical
        /// RA axis but leaves the encoder fixed. The (v1, v_now, delta) pair
        /// passed to <see cref="TryRecoverAxis"/> is then a non-rotation pair
        /// and the recovered "axis" is fictitious. This refiner solves the
        /// problem with a Jacobian-linearised one-frame method:
        /// <list type="number">
        ///   <item>recover total encoder angle theta from (v1, A0, hemisphere
        ///     pole) by projecting both onto the plane perpendicular to A0 --
        ///     no need to know absolute mount encoder steps.</item>
        ///   <item>pre-compute J = dv/dA at (A0, theta + delta) where v(A, t) =
        ///     R(A, t) * pole. Acting on h = (0, 0, +/-1) the Jacobian's third
        ///     row is zero so the relevant 3x2 reduces to a 2x2 normal-equation
        ///     system (always invertible for axis off the pole).</item>
        ///   <item>per refine: dv = v_now - v2_baseline, dA = J^+ * dv,
        ///     A_current = normalise(A0 + dA + axis-tangent correction).</item>
        /// </list>
        /// Tracks knob adjustments inside one Phase A without re-rotating the
        /// mount. Tested against the FakeSkywatcher pipeline at sim Az
        /// 60->45->30->15->0 arcmin: recovered Az matches sim to &lt; 0.01'
        /// across the range, Alt stays under 0.01'.
        /// </summary>
        public readonly struct LiveAxisRefiner
        {
            private readonly Vec3 _axisAtPhaseAEnd;
            private readonly Vec3 _v2Baseline;
            private readonly Hemisphere _hemisphere;
            private readonly double _j00, _j01, _j10, _j11;
            private readonly double _m00, _m01, _m11, _det;

            /// <summary>
            /// Initialise the refiner from the data SolveAsync already has at
            /// the end of Phase A. The hemisphere pole is the v(A, t) input
            /// vector h: (0, 0, +1) for north, (0, 0, -1) for south.
            /// </summary>
            public LiveAxisRefiner(
                in Vec3 v1,
                in Vec3 v2Baseline,
                in Vec3 axisAtPhaseAEnd,
                Hemisphere hemisphere,
                double phaseADeltaRad)
            {
                _axisAtPhaseAEnd = axisAtPhaseAEnd;
                _v2Baseline = v2Baseline;
                _hemisphere = hemisphere;

                var hz = hemisphere == Hemisphere.North ? 1.0 : -1.0;

                // Recover theta_1 (encoder angle when v1 was captured) by projecting
                // v1 and h onto the plane perpendicular to A0 and reading the signed
                // angle between them around A0.
                var h = new Vec3(0, 0, hz);
                var theta1 = SolveTheta(v1, axisAtPhaseAEnd, h);
                var thetaTotal = theta1 + phaseADeltaRad;

                // Pre-compute Jacobian J = dv/dA at (A0, thetaTotal). Third row of
                // the full 3x3 is zero for h on the z-axis, so the reduced 3x2
                // (axis dx, dy components) lives in the (x, y) plane of the image.
                var sinT = Math.Sin(thetaTotal);
                var cosT = Math.Cos(thetaTotal);
                var oneMinusCos = 1.0 - cosT;
                var az = axisAtPhaseAEnd.Z;

                _j00 = hz * az * oneMinusCos;
                _j01 = hz * sinT;
                _j10 = -hz * sinT;
                _j11 = hz * az * oneMinusCos;

                // Normal equations J^T J (2x2). Always invertible for axis not
                // exactly on a pole (degenerate row at az=0 is irrelevant since the
                // axis lives near the celestial pole by the routine's premise).
                _m00 = _j00 * _j00 + _j10 * _j10;
                _m01 = _j00 * _j01 + _j10 * _j11;
                _m11 = _j01 * _j01 + _j11 * _j11;
                _det = _m00 * _m11 - _m01 * _m01;
            }

            /// <summary>
            /// Returns the current J2000 axis given the live frame's WCS centre.
            /// Constant work per call (one matrix-vector multiply + Cramer 2x2).
            /// </summary>
            public Vec3 RefineAxis(in Vec3 vNow)
            {
                var dvX = vNow.X - _v2Baseline.X;
                var dvY = vNow.Y - _v2Baseline.Y;
                // dvZ contributes nothing because j20 = j21 = 0 for h on the z-axis.

                var rhs0 = _j00 * dvX + _j10 * dvY;
                var rhs1 = _j01 * dvX + _j11 * dvY;
                var dax = (_m11 * rhs0 - _m01 * rhs1) / _det;
                var day = (-_m01 * rhs0 + _m00 * rhs1) / _det;

                // dA . A0 = 0 constraint (tangent to unit sphere) gives daz.
                var ax = _axisAtPhaseAEnd.X;
                var ay = _axisAtPhaseAEnd.Y;
                var az = _axisAtPhaseAEnd.Z;
                var daz = -(ax * dax + ay * day) / az;

                var nx = ax + dax;
                var ny = ay + day;
                var nz = az + daz;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                return new Vec3(nx / len, ny / len, nz / len);
            }

            // Project v and h onto the plane perpendicular to axis; the signed
            // angle between them around axis is theta. Sign comes from
            // cross(h_perp, v_perp) . axis (right-hand rule).
            private static double SolveTheta(in Vec3 v, in Vec3 axis, in Vec3 h)
            {
                var vDotA = v.X * axis.X + v.Y * axis.Y + v.Z * axis.Z;
                var hDotA = h.X * axis.X + h.Y * axis.Y + h.Z * axis.Z;
                var vPerpX = v.X - vDotA * axis.X;
                var vPerpY = v.Y - vDotA * axis.Y;
                var vPerpZ = v.Z - vDotA * axis.Z;
                var hPerpX = h.X - hDotA * axis.X;
                var hPerpY = h.Y - hDotA * axis.Y;
                var hPerpZ = h.Z - hDotA * axis.Z;
                var dot = vPerpX * hPerpX + vPerpY * hPerpY + vPerpZ * hPerpZ;
                var crossX = hPerpY * vPerpZ - hPerpZ * vPerpY;
                var crossY = hPerpZ * vPerpX - hPerpX * vPerpZ;
                var crossZ = hPerpX * vPerpY - hPerpY * vPerpX;
                var crossDotAxis = crossX * axis.X + crossY * axis.Y + crossZ * axis.Z;
                return Math.Atan2(crossDotAxis, dot);
            }
        }

        /// <summary>
        /// Decompose a J2000 axis direction into (azError, altError) against the
        /// apparent (refraction-corrected) celestial pole at the configured site.
        /// </summary>
        /// <param name="axisJ2000">Mount RA-axis direction in J2000 unit-vector form.</param>
        /// <param name="hemisphere">North or south pole target.</param>
        /// <param name="siteLatDeg">Site latitude in degrees (+north).</param>
        /// <param name="siteLonDeg">Site longitude in degrees (+east).</param>
        /// <param name="siteElevM">Site elevation in metres.</param>
        /// <param name="sitePressureHPa">Atmospheric pressure at the site in hPa.</param>
        /// <param name="siteTempC">Air temperature at the site in degrees Celsius.</param>
        /// <param name="utc">UTC instant for the calculation.</param>
        /// <returns>(azError, altError) in radians. Sign convention: positive azError
        /// means the axis is east of the apparent pole; positive altError means
        /// the axis is above the apparent pole.</returns>
        /// <remarks>
        /// Bypasses <see cref="Transform"/> and calls <see cref="SOFAHelpers.J2000ToTopo"/>
        /// directly, because <see cref="Transform.Refraction"/> only gates refraction
        /// in the topo-to-J2000 direction. We need explicit per-call control: refraction
        /// applied for the apparent pole, omitted for the geometric mount-axis projection.
        /// </remarks>
        public static (double AzErrorRad, double AltErrorRad) DecomposeAxisError(
            in Vec3 axisJ2000,
            Hemisphere hemisphere,
            double siteLatDeg,
            double siteLonDeg,
            double siteElevM,
            double sitePressureHPa,
            double siteTempC,
            DateTimeOffset utc)
        {
            utc.ToSOFAUtcJd(out double utc1, out double utc2);

            // Apparent pole: where the true celestial pole appears to a refracted
            // observer at this site, in topocentric az/alt. Pass pressure + temp so
            // SOFAHelpers includes refraction.
            double poleDecDeg = hemisphere == Hemisphere.North ? 90.0 : -90.0;
            var (_, _, poleAzDeg, poleAltDeg) = J2000ToTopo(
                ra: 0.0, dec: poleDecDeg,
                utc1: utc1, utc2: utc2,
                siteLat: siteLatDeg, siteLong: siteLonDeg, siteElevation: siteElevM,
                sitePressure: sitePressureHPa, siteTemp: siteTempC);

            // Mount axis: a mechanical orientation, no refraction applies. Pass NaN for
            // pressure + temp to skip the refraction step in SOFAHelpers.
            var (axisRaHours, axisDecDeg) = UnitVecToRaDec(axisJ2000);
            var (_, _, axisAzDeg, axisAltDeg) = J2000ToTopo(
                ra: axisRaHours, dec: axisDecDeg,
                utc1: utc1, utc2: utc2,
                siteLat: siteLatDeg, siteLong: siteLonDeg, siteElevation: siteElevM,
                sitePressure: double.NaN, siteTemp: double.NaN);

            double dAzDeg = axisAzDeg - poleAzDeg;
            // Wrap to [-180, 180].
            while (dAzDeg > 180.0) dAzDeg -= 360.0;
            while (dAzDeg < -180.0) dAzDeg += 360.0;

            return (dAzDeg * DEGREES2RADIANS, (axisAltDeg - poleAltDeg) * DEGREES2RADIANS);
        }

        /// <summary>
        /// Convert (RA hours, Dec degrees) to a J2000 unit vector.
        /// X-axis points to RA = 0h on the equator, Z-axis to the north pole.
        /// </summary>
        public static Vec3 RaDecToUnitVec(double raHours, double decDegrees)
        {
            double ra = raHours * HOURS2RADIANS;
            double dec = decDegrees * DEGREES2RADIANS;
            double cosDec = Math.Cos(dec);
            return new Vec3(cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
        }

        /// <summary>
        /// Convert a J2000 unit vector back to (RA hours in [0, 24), Dec degrees in [-90, 90]).
        /// </summary>
        public static (double RaHours, double DecDegrees) UnitVecToRaDec(in Vec3 v)
        {
            double dec = Math.Asin(Math.Clamp(v.Z, -1.0, 1.0));
            double ra = Math.Atan2(v.Y, v.X);
            if (ra < 0) ra += 2.0 * Math.PI;
            return (ra * RADIANS2HOURS, dec * RADIANS2DEGREES);
        }

        /// <summary>
        /// Apply a Rodrigues rotation: rotate <paramref name="v"/> by
        /// <paramref name="angleRad"/> around the unit-vector <paramref name="axis"/>
        /// using the right-hand rule. Used by tests to synthesise (v1, v2) pairs.
        /// </summary>
        public static Vec3 Rotate(in Vec3 v, in Vec3 axis, double angleRad)
        {
            double c = Math.Cos(angleRad);
            double s = Math.Sin(angleRad);
            double dot = Vec3.Dot(axis, v);
            var cross = Vec3.Cross(axis, v);
            return new Vec3(
                v.X * c + cross.X * s + axis.X * dot * (1 - c),
                v.Y * c + cross.Y * s + axis.Y * dot * (1 - c),
                v.Z * c + cross.Z * s + axis.Z * dot * (1 - c));
        }
    }

    /// <summary>
    /// Double-precision 3D vector for celestial-sphere unit-vector math.
    /// We don't reuse <see cref="System.Numerics.Vector3"/> because polar
    /// alignment near the pole is a tight numerical regime where single-
    /// precision sin/cos lose enough accuracy to matter at the arcminute level.
    /// </summary>
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static double Dot(in Vec3 a, in Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(in Vec3 a, in Vec3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static Vec3 Add(in Vec3 a, in Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vec3 Sub(in Vec3 a, in Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vec3 Scale(in Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public static Vec3 Normalize(in Vec3 a)
        {
            var len = a.Length;
            return len > 0 ? new Vec3(a.X / len, a.Y / len, a.Z / len) : default;
        }
    }

    /// <summary>Celestial pole hemisphere (north or south).</summary>
    public enum Hemisphere
    {
        North,
        South
    }
}
