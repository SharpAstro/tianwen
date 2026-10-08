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
    /// Computes the camera-to-XYZ 3x3 matrix (dcraw's <c>cam_xyz</c>, a camera channel's response to each CIE
    /// primary): the least-squares fit of each channel's curve onto the CIE 1931 matching functions, weighted by
    /// D65 (the Luther approximation). Each channel is projected onto the three functions, <c>A[c, p] =
    /// integral(CFA_c x D65 x cie_p)</c>, and the projections are multiplied by the inverse of the functions' own
    /// Gram matrix, <c>G[p, q] = integral(cie_p x cie_q x D65)</c>. The projections alone are not the response:
    /// x-bar and y-bar overlap so much that, without the Gram inverse, red read nearly as green and the derived
    /// matrix came out ill-conditioned (11.99 and -12.07 on its red row for the EOS 5D Mark II, against dcraw's
    /// measured 2.07 and -1.32; #1279, rule E).
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

        Span<double> projections = stackalloc double[9];
        for (var c = 0; c < 3; c++)
        {
            // Pre-combine CFA_c x D65 so the inner loop only adds a CIE primary.
            // FilterCurve.Combine resamples to a common 1A grid over the curves'
            // overlap; we get back ~3000 samples covering the visible spectrum.
            var cfaXd65 = FilterCurve.Combine($"cfa{c}_d65", new[] { channels[c], d65 });
            for (var p = 0; p < 3; p++)
            {
                var tsys = FilterCurve.Combine($"cam{c}_xyz{p}", new[] { cfaXd65, cies[p] });
                projections[c * 3 + p] = Integrate(tsys);
            }
        }

        // The matching functions' Gram matrix under the same weight, and the fit through its inverse.
        Span<double> gram = stackalloc double[9];
        for (var p = 0; p < 3; p++)
        {
            for (var q = 0; q < 3; q++)
            {
                gram[p * 3 + q] = Integrate(FilterCurve.Combine($"cie{p}_cie{q}_d65", new[] { cies[p], cies[q], d65 }));
            }
        }
        Span<double> gramInverse = stackalloc double[9];
        if (!TryInvert3(gram, gramInverse))
        {
            throw new InvalidOperationException("the CIE functions' Gram matrix is singular; spectral input is degenerate.");
        }

        var camXyz = new double[9];
        for (var c = 0; c < 3; c++)
        {
            for (var p = 0; p < 3; p++)
            {
                var s = 0.0;
                for (var q = 0; q < 3; q++)
                    s += projections[c * 3 + q] * gramInverse[q * 3 + p];
                camXyz[c * 3 + p] = s;
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
    /// The canonical sRGB primaries in CIE XYZ-D65, row-major, linear sRGB to XYZ: identical to dcraw's <c>xyz_rgb[3][3]</c>
    /// (Bruce Lindbloom's reference values are the same).
    /// </summary>
    internal static ReadOnlySpan<double> SrgbToXyz =>
    [
        0.412453, 0.357580, 0.180423,
        0.212671, 0.715160, 0.072169,
        0.019334, 0.119193, 0.950227,
    ];

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
    /// worth forcing one. The whole spectral derivation is checked against dcraw's measured matrix for one body
    /// in <c>CameraColorMatrixTests.TryComputeCameraToSrgbMatrix_EosFiveDMarkTwo_AgreesWithDcrawsMeasuredMatrix</c>.</para>
    /// </summary>
    public static float[] CamXyzToRgbCam(ReadOnlySpan<double> camXyz)
    {
        if (camXyz.Length != 9)
            throw new ArgumentException($"Expected 9 elements (row-major 3x3), got {camXyz.Length}.", nameof(camXyz));

        ReadOnlySpan<double> xyzRgb = SrgbToXyz;

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

        // cam_rgb takes neutral camera -> neutral sRGB; we want the reverse
        // direction (apply to a WB-corrected camera-RGB pixel to get sRGB) =
        // matrix inverse.
        Span<double> rgbCam = stackalloc double[9];
        if (!TryInvert3(camRgb, rgbCam))
        {
            throw new InvalidOperationException("the camera-to-sRGB matrix is singular; spectral input is degenerate.");
        }
        var result = new float[9];
        for (var i = 0; i < 9; i++)
            result[i] = (float)rgbCam[i];
        return result;
    }

    /// <summary>A row-major 3x3 inverse by cofactor expansion; throws on a singular matrix (degenerate spectral input).</summary>
    /// <summary>
    /// The inverse of the row-major 3x3 <paramref name="m"/> into <paramref name="inverse"/>, by its adjugate; false, writing nothing, where
    /// <paramref name="m"/> is singular (a determinant under 1e-12). The one 3x3 inverse the colour code takes (the audit on #1343 found a
    /// second, by Cramer's rule, in <see cref="Planetary.PlanetaryColourBalance.GainsThrough"/>).
    /// </summary>
    internal static bool TryInvert3(ReadOnlySpan<double> m, Span<double> inverse)
    {
        var m00 = m[0]; var m01 = m[1]; var m02 = m[2];
        var m10 = m[3]; var m11 = m[4]; var m12 = m[5];
        var m20 = m[6]; var m21 = m[7]; var m22 = m[8];
        var det = m00 * (m11 * m22 - m12 * m21)
                - m01 * (m10 * m22 - m12 * m20)
                + m02 * (m10 * m21 - m11 * m20);
        if (Math.Abs(det) < 1e-12)
        {
            return false;
        }

        var invDet = 1.0 / det;
        inverse[0] = (m11 * m22 - m12 * m21) * invDet;
        inverse[1] = (m02 * m21 - m01 * m22) * invDet;
        inverse[2] = (m01 * m12 - m02 * m11) * invDet;
        inverse[3] = (m12 * m20 - m10 * m22) * invDet;
        inverse[4] = (m00 * m22 - m02 * m20) * invDet;
        inverse[5] = (m02 * m10 - m00 * m12) * invDet;
        inverse[6] = (m10 * m21 - m11 * m20) * invDet;
        inverse[7] = (m01 * m20 - m00 * m21) * invDet;
        inverse[8] = (m00 * m11 - m01 * m10) * invDet;
        return true;
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
