using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Derives a camera-RGB to sRGB 3x3 colour matrix from spectral response data
/// (sensor QE + per-channel CFA transmission curves), using the canonical
/// CIE 1931 + D65 reference data from <see cref="CieReferenceData"/>.
///
/// <para>This is the "first-principles" companion to FC.SDK.Raw's
/// <see cref="FC.SDK.Raw.CanonCameraProfile.ComputeRgbCam"/>: the dcraw entry
/// gives you the matrix as a hand-curated 9-int table per body, whereas this
/// helper derives the same matrix from the per-camera spectral curves we ship
/// in <c>filter_curves.gs.gz</c>. The maths is identical from step 2 onwards
/// (cam_xyz × xyz_rgb → row-normalise → invert); what differs is step 1:
/// dcraw bakes <c>cam_xyz</c> into a constant, we compute it.</para>
///
/// <para>Use <see cref="FilterCurveDatabase.TryComputeCameraToSrgbMatrix"/> as
/// the top-level entry; this class is the bare math layer for callers that
/// already have <see cref="FilterCurve"/> instances to feed in.</para>
/// </summary>
public static class CameraColorMatrix
{
    /// <summary>
    /// Computes the camera-to-XYZ 3x3 matrix by integrating each
    /// <c>(CFA channel × CIE primary × D65)</c> product over wavelength.
    /// <paramref name="cfaR"/> / <paramref name="cfaG"/> / <paramref name="cfaB"/>
    /// are the per-channel CFA transmission curves; QE is treated as a
    /// flat unit response (use the QE-aware overload to incorporate sensor
    /// QE separately). Returns 9 doubles, row-major: index <c>c*3 + p</c>
    /// holds the response of camera channel c to CIE primary p.
    /// </summary>
    public static double[] ComputeCamXyz(FilterCurve cfaR, FilterCurve cfaG, FilterCurve cfaB)
    {
        // FilterCurve is a managed record struct (carries string + ImmutableArray
        // fields), so we can't stackalloc it. Heap arrays of three are cheap and
        // happen once per matrix derivation.
        var channels = new[] { cfaR, cfaG, cfaB };
        var cies = new[] { CieReferenceData.X1931, CieReferenceData.Y1931, CieReferenceData.Z1931 };
        var d65 = CieReferenceData.D65;

        var camXyz = new double[9];
        for (var c = 0; c < 3; c++)
        {
            // Pre-combine CFA_c x D65 so the inner loop only adds a CIE primary.
            // FilterCurve.Combine resamples to a common 1A grid over the curves'
            // overlap; we get back ~3000 samples covering the visible spectrum.
            var cfaXd65 = FilterCurve.Combine($"cfa{c}_d65", new[] { channels[c], d65 });
            for (var p = 0; p < 3; p++)
            {
                var tsys = FilterCurve.Combine($"cam{c}_xyz{p}", new[] { cfaXd65, cies[p] });
                camXyz[c * 3 + p] = Integrate(tsys);
            }
        }
        return camXyz;
    }

    /// <summary>
    /// Computes the camera-to-XYZ 3x3 matrix with an explicit sensor QE curve
    /// folded into every channel. Use this overload when both per-camera CFA
    /// curves AND a sensor QE curve are available (e.g. OSC astro cameras
    /// with known IMX sensor + Sony CFA); the no-QE overload is correct when
    /// the CFA curves are themselves end-to-end measurements that already
    /// include the sensor's QE response (as is the case for SASP's Canon
    /// per-model CFA triples, which were derived from full-system DSLR
    /// measurements rather than CFA-glass-only tabulations).
    /// </summary>
    public static double[] ComputeCamXyz(FilterCurve qe, FilterCurve cfaR, FilterCurve cfaG, FilterCurve cfaB)
    {
        return ComputeCamXyz(
            FilterCurve.Combine("qe_cfaR", new[] { qe, cfaR }),
            FilterCurve.Combine("qe_cfaG", new[] { qe, cfaG }),
            FilterCurve.Combine("qe_cfaB", new[] { qe, cfaB }));
    }

    /// <summary>
    /// Closes the loop on the dcraw cam_xyz_coeff pipeline: multiply
    /// <paramref name="camXyz"/> by <c>xyz_rgb</c> (the sRGB primaries in
    /// XYZ-D65, the canonical Rec. 709 3x3), row-normalise so neutral
    /// D65 -> camera-neutral (1, 1, 1), then 3x3 invert so a WB-corrected
    /// camera pixel maps to neutral sRGB.
    ///
    /// <para>This duplicates the math in FC.SDK.Raw's
    /// <c>CanonCameraProfile.ComputeRgbCam</c> on purpose: TianWen.Lib has no
    /// FC.SDK.Raw dependency today, and a 30-line pure-math routine isn't
    /// worth forcing one. The duplication is guarded by a cross-check test
    /// in <c>CameraColorMatrixTests.CamXyzToRgbCam_MatchesCanonCameraProfile_ForKnownEntry</c>.</para>
    /// </summary>
    public static float[] CamXyzToRgbCam(ReadOnlySpan<double> camXyz)
    {
        if (camXyz.Length != 9)
            throw new ArgumentException($"Expected 9 elements (row-major 3x3), got {camXyz.Length}.", nameof(camXyz));

        // Canonical sRGB primaries in CIE XYZ-D65, identical to dcraw's
        // const xyz_rgb[3][3]. Bruce Lindbloom's reference values are the same.
        ReadOnlySpan<double> xyzRgb = stackalloc double[]
        {
            0.412453, 0.357580, 0.180423,
            0.212671, 0.715160, 0.072169,
            0.019334, 0.119193, 0.950227,
        };

        // cam_rgb = cam_xyz * xyz_rgb (3x3 matrix product).
        Span<double> camRgb = stackalloc double[9];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var s = 0.0;
            for (var k = 0; k < 3; k++)
                s += camXyz[i * 3 + k] * xyzRgb[k * 3 + j];
            camRgb[i * 3 + j] = s;
        }

        // Row-normalise: encode "neutral camera RGB (1, 1, 1) maps to neutral
        // sRGB output (1, 1, 1)" into the matrix itself.
        for (var i = 0; i < 3; i++)
        {
            var rowSum = camRgb[i * 3] + camRgb[i * 3 + 1] + camRgb[i * 3 + 2];
            if (rowSum == 0) continue;
            for (var j = 0; j < 3; j++) camRgb[i * 3 + j] /= rowSum;
        }

        // 3x3 inverse via cofactor expansion. cam_rgb takes neutral camera
        // -> neutral sRGB; we want the reverse direction (apply to a
        // WB-corrected camera-RGB pixel to get sRGB) = matrix inverse.
        var m00 = camRgb[0]; var m01 = camRgb[1]; var m02 = camRgb[2];
        var m10 = camRgb[3]; var m11 = camRgb[4]; var m12 = camRgb[5];
        var m20 = camRgb[6]; var m21 = camRgb[7]; var m22 = camRgb[8];
        var det = m00 * (m11 * m22 - m12 * m21)
                - m01 * (m10 * m22 - m12 * m20)
                + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-12)
            throw new InvalidOperationException(
                $"cam_rgb matrix is singular (det={det:E3}); spectral input is degenerate.");

        var invDet = 1.0 / det;
        return
        [
            (float)((m11 * m22 - m12 * m21) * invDet),
            (float)((m02 * m21 - m01 * m22) * invDet),
            (float)((m01 * m12 - m02 * m11) * invDet),
            (float)((m12 * m20 - m10 * m22) * invDet),
            (float)((m00 * m22 - m02 * m20) * invDet),
            (float)((m02 * m10 - m00 * m12) * invDet),
            (float)((m10 * m21 - m11 * m20) * invDet),
            (float)((m01 * m20 - m00 * m21) * invDet),
            (float)((m00 * m11 - m01 * m10) * invDet),
        ];
    }

    /// <summary>Trapezoidal integration of a curve's throughput against its
    /// wavelength axis. Same algorithm as <see cref="FilterCurve.IntegrateSedThroughput"/>
    /// but takes a single curve since the upstream <see cref="FilterCurve.Combine"/>
    /// has already done the product step.</summary>
    private static double Integrate(FilterCurve curve)
    {
        if (curve.Count < 2) return 0;
        var wl = curve.Wavelengths;
        var tp = curve.Throughputs;
        var sum = 0.0;
        for (var i = 0; i < curve.Count - 1; i++)
        {
            var dx = wl[i + 1] - wl[i];
            sum += (tp[i] + tp[i + 1]) * 0.5 * dx;
        }
        return sum;
    }
}
