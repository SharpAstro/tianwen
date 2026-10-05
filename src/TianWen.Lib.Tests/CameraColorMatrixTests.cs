using System.Collections.Immutable;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="CameraColorMatrix"/> (spectral cam_xyz derivation +
/// dcraw-style row-normalise/invert) and
/// <see cref="FilterCurveDatabase.TryComputeCameraToSrgbMatrix"/> (the
/// per-camera dispatch entrypoint). The cross-check against FC.SDK.Raw's
/// <c>CanonCameraProfile.ComputeRgbCam</c> lands with Phase 2 when the
/// import wiring adds an FC.SDK.Raw reference to this project.
/// </summary>
[Collection("Scheduling")]
public class CameraColorMatrixTests
{
    [Fact]
    public void CamXyzToRgbCam_NeutralCameraInput_RoundTripsToNeutralSrgb()
    {
        // Same invariant CanonCameraProfilesTests already enforces for the
        // dcraw-side ComputeRgbCam: row sums must each be 1.0 so neutral
        // camera RGB (1, 1, 1) maps to neutral sRGB (1, 1, 1) after the
        // matrix. Feed an arbitrary plausible cam_xyz (the EOS 6D entry,
        // scaled back to floats from dcraw's int×10000 storage convention).
        double[] camXyz =
        [
            0.7034, -0.0804, -0.1014,
           -0.4420,  1.2564,  0.2058,
           -0.0851,  0.1994,  0.5758,
        ];

        var rgbCam = CameraColorMatrix.CamXyzToRgbCam(camXyz);

        var r = rgbCam[0] + rgbCam[1] + rgbCam[2];
        var g = rgbCam[3] + rgbCam[4] + rgbCam[5];
        var b = rgbCam[6] + rgbCam[7] + rgbCam[8];
        r.ShouldBe(1.0f, tolerance: 1e-4f, "row 0 sum");
        g.ShouldBe(1.0f, tolerance: 1e-4f, "row 1 sum");
        b.ShouldBe(1.0f, tolerance: 1e-4f, "row 2 sum");
    }

    [Fact]
    public void ComputeCamXyz_GaussianCfa_ProducesTheExpectedColourOrdering()
    {
        // Three Gaussian-ish CFA curves (peak at 600 / 540 / 460 nm respectively)
        // should produce a 9-element matrix where the diagonal is the
        // dominant cell per channel (R picks up X most, G picks up Y most,
        // B picks up Z most under D65). Sanity check that the fit
        // produces the expected colour ordering.
        var cfaR = MakeGaussianCfa("cfa_R", peakNm: 600, sigmaNm: 60);
        var cfaG = MakeGaussianCfa("cfa_G", peakNm: 540, sigmaNm: 60);
        var cfaB = MakeGaussianCfa("cfa_B", peakNm: 460, sigmaNm: 60);

        var camXyz = CameraColorMatrix.ComputeCamXyz(cfaR, cfaG, cfaB);

        // 9 elements, all finite. Not all non-negative: the matrix is a least-squares fit onto the overlapping
        // matching functions, whose weights can be negative though every curve is positive (#1279, rule E).
        camXyz.Length.ShouldBe(9);
        for (var i = 0; i < 9; i++)
        {
            double.IsFinite(camXyz[i]).ShouldBeTrue($"entry {i} not finite");
        }

        // Cross-chromatic ordering: a CFA centred in the red has near-zero
        // overlap with the Z (blue) matching function; one centred in the
        // blue has near-zero overlap with X (red-ish; X also has a small
        // short-wavelength hump, hence the larger tolerance).
        camXyz[2].ShouldBeLessThan(camXyz[0]); // R-Z < R-X (red CFA is dim under Z)
        camXyz[6].ShouldBeLessThan(camXyz[8]); // B-X < B-Z (blue CFA is dim under X)
        // G channel (centre): Y response dominates because the G CFA
        // sits squarely under the Y matching function peak.
        camXyz[4].ShouldBeGreaterThan(camXyz[3]); // G-Y > G-X
        camXyz[4].ShouldBeGreaterThan(camXyz[5]); // G-Y > G-Z
    }

    [Fact]
    public void ComputeCamXyz_ALutherCamera_IsRecoveredExactly()
    {
        // A camera whose channels ARE combinations of the CIE matching functions (the Luther condition): its
        // cam_xyz is those combinations' weights, which a projection onto the functions alone does not return
        // (x-bar and y-bar overlap), and the least-squares fit through their Gram inverse does (#1279, rule E).
        double[] weights =
        [
            0.9, 0.3, 0.0,
            0.2, 1.0, 0.1,
            0.0, 0.1, 0.8,
        ];
        var cies = new[] { CieReferenceData.X1931, CieReferenceData.Y1931, CieReferenceData.Z1931 };
        var channels = new FilterCurve[3];
        for (var c = 0; c < 3; c++)
        {
            var values = new double[cies[0].Count];
            for (var i = 0; i < values.Length; i++)
            {
                for (var p = 0; p < 3; p++)
                {
                    values[i] += weights[c * 3 + p] * cies[p].ThroughputAt(i);
                }
            }
            channels[c] = new FilterCurve($"luther_{c}", "synthetic", cies[0].Wavelengths, ImmutableArray.Create(values));
        }

        var camXyz = CameraColorMatrix.ComputeCamXyz(channels[0], channels[1], channels[2]);

        for (var i = 0; i < 9; i++)
        {
            camXyz[i].ShouldBe(weights[i], 1e-6, $"entry {i}");
        }
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_EosFiveDMarkTwo_AgreesWithDcrawsMeasuredMatrix()
    {
        // The external truth for the spectral derivation: dcraw's measured matrix for the same body (FC.SDK.Raw's
        // CanonCameraProfile). Without the Gram inverse the spectral red row read 11.99 and -12.07 against dcraw's
        // 2.07 and -1.32; with it every element is within 0.28.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("Canon EOS 5D Mark II", out var spectral).ShouldBeTrue();
        var dcraw = FC.SDK.Raw.CanonCameraProfiles.ResolveProfile("Canon EOS 5D Mark II")?.ComputeRgbCam();
        dcraw.ShouldNotBeNull();
        for (var i = 0; i < 9; i++)
        {
            spectral[i].ShouldBe(dcraw[i], 0.35f, $"entry {i}");
        }
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_KnownCanonBody_ProducesPlausibleMatrix()
    {
        // EOS 5D Mark II ships in filter_curves.gs.gz as CANON_EOS_5D_MARK_II_R/G/B.
        // The derived matrix must be a 9-float row-major with row sums of 1.0
        // (by construction of CamXyzToRgbCam).
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        var ok = FilterCurveDatabase.TryComputeCameraToSrgbMatrix("Canon EOS 5D Mark II", out var matrix);
        ok.ShouldBeTrue();
        matrix.ShouldNotBeNull();
        matrix.Length.ShouldBe(9);

        var r = matrix[0] + matrix[1] + matrix[2];
        var g = matrix[3] + matrix[4] + matrix[5];
        var b = matrix[6] + matrix[7] + matrix[8];
        r.ShouldBe(1.0f, tolerance: 1e-4f, "row 0 sum");
        g.ShouldBe(1.0f, tolerance: 1e-4f, "row 1 sum");
        b.ShouldBe(1.0f, tolerance: 1e-4f, "row 2 sum");

        // Camera-RGB to sRGB matrices for real DSLRs have a strongly
        // dominant diagonal (R-channel weight on R-out is the largest in row 0,
        // etc.) and small but non-zero off-diagonals (the spectral crosstalk
        // correction). Sanity-check that shape.
        matrix[0].ShouldBeGreaterThan(0); // R->R weight is positive (channel kept)
        matrix[4].ShouldBeGreaterThan(0); // G->G
        matrix[8].ShouldBeGreaterThan(0); // B->B
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_NormalisesEosModelString()
    {
        // The dispatcher must accept the free-form EXIF model string
        // "Canon EOS 5D Mark II" and normalise to the SASP filter-name key
        // "CANON_EOS_5D_MARK_II": case + spacing + word ordering already
        // match the SASP convention.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("Canon EOS 5D Mark II", out _).ShouldBeTrue();
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("canon eos 5d mark ii", out _).ShouldBeTrue();
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix(" Canon  EOS  5D  Mark  II ", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_UnknownModel_ReturnsFalse()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("Nikon Z9", out var matrix).ShouldBeFalse();
        matrix.ShouldBeNull();
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_EmptyModel_ReturnsFalse()
    {
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("", out _).ShouldBeFalse();
        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("    ", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task TryComputeCameraToSrgbMatrix_KnownImxSensor_FallsBackToSonyCfa()
    {
        // OSC astro path: IMX571 has a sensor QE curve in sensor_qe.gs.gz, and
        // SONY_COLOR_SENSOR_R/G/B is in filter_curves.gs.gz. The dispatcher's
        // strategy 2 fires when the per-camera CFA triple isn't present.
        await FilterCurveDatabase.LoadAsync(TestContext.Current.CancellationToken);

        FilterCurveDatabase.TryComputeCameraToSrgbMatrix("IMX571", out var matrix).ShouldBeTrue();
        matrix.ShouldNotBeNull();
        matrix.Length.ShouldBe(9);

        var r = matrix[0] + matrix[1] + matrix[2];
        var g = matrix[3] + matrix[4] + matrix[5];
        var b = matrix[6] + matrix[7] + matrix[8];
        r.ShouldBe(1.0f, tolerance: 1e-4f);
        g.ShouldBe(1.0f, tolerance: 1e-4f);
        b.ShouldBe(1.0f, tolerance: 1e-4f);
    }

    /// <summary>Build a Gaussian-shaped CFA transmission curve for synthetic
    /// test purposes. Sampled at 5 nm intervals 380-780 nm to match the CIE
    /// reference grid.</summary>
    private static FilterCurve MakeGaussianCfa(string name, double peakNm, double sigmaNm)
    {
        var wavelengths = new double[81];
        var values = new double[81];
        for (var i = 0; i < 81; i++)
        {
            var nm = 380.0 + i * 5.0;
            wavelengths[i] = nm * 10.0; // nm -> Angstroms
            var x = (nm - peakNm) / sigmaNm;
            values[i] = System.Math.Exp(-0.5 * x * x);
        }
        return new FilterCurve(name, "synthetic",
            ImmutableArray.Create(wavelengths),
            ImmutableArray.Create(values));
    }
}
