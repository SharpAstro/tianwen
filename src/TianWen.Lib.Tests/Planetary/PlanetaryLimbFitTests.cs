using System;
using System.IO;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The limb fit (docs/plans/planetary-restoration.md, R1 part 2, #1049) against disks rendered here by code of its own: 8x
/// supersampled, blurred at the pixel level by a kernel the model may not share. Set before the first run: the centre within
/// 0.05 px, the equatorial radius within 0.1 px and the axis within 0.5 degree when the model's PSF is the truth's; the centre
/// within 0.05 px and the radius within the plan's 0.5 % under a Moffat PSF the model does not have; the geometric centre
/// within 0.05 px at a 10 degree phase.
/// </summary>
public class PlanetaryLimbFitTests : IDisposable
{
    /// <summary>The temporary folders this test made, deleted after it (#1197).</summary>
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private const int Size = 200;
    private const double TrueX = 100.37;
    private const double TrueY = 98.81;
    private const double TrueRadius = 60;
    private const double TrueAxisDeg = 30;
    private const double AxisRatio = 0.935;

    [Fact]
    public void ADiskBlurredAsTheModelBlursComesBackWhereItWas()
    {
        var image = Render(k: 0.9, phaseDeg: 0, psf: Gaussian(1.8), noise: 0.005, seed: 1);

        var fit = FitFromRoughStart(image, new LimbFitOptions(AxisRatio));

        TestContext.Current.TestOutputHelper?.WriteLine($"iterations {fit.Iterations}");
        fit.Converged.ShouldBeTrue();
        fit.CenterX.ShouldBe(TrueX, 0.05);
        fit.CenterY.ShouldBe(TrueY, 0.05);
        fit.EquatorialRadius.ShouldBe(TrueRadius, 0.1);
        fit.AxisAngleDeg.ShouldBe(TrueAxisDeg, 0.5);
        fit.LimbDarkening.ShouldBe(0.9, 0.1);
        fit.PsfSigma.ShouldBe(1.8, 0.1);
    }

    [Fact]
    public void AMoffatBlurTheModelDoesNotHaveKeepsTheCentreAndMovesTheRadiusLittle()
    {
        // Seeing's wings are Moffat's, not a Gaussian's: beta 2.5 at the same 4.24 px FWHM as a sigma of 1.8.
        var image = Render(k: 0.9, phaseDeg: 0, psf: Moffat(fwhm: 4.24, beta: 2.5), noise: 0.005, seed: 2);

        var fit = FitFromRoughStart(image, new LimbFitOptions(AxisRatio));

        TestContext.Current.TestOutputHelper?.WriteLine($"Moffat: centre {fit.CenterX - TrueX:+0.000;-0.000}, {fit.CenterY - TrueY:+0.000;-0.000} px, radius {fit.EquatorialRadius - TrueRadius:+0.000;-0.000} px, axis {fit.AxisAngleDeg - TrueAxisDeg:+0.00;-0.00} deg, sigma {fit.PsfSigma:0.00}");
        fit.CenterX.ShouldBe(TrueX, 0.05, "a symmetric blur cannot move the centre");
        fit.CenterY.ShouldBe(TrueY, 0.05);
        fit.EquatorialRadius.ShouldBe(TrueRadius, 0.005 * TrueRadius, "the plan's 0.5 %");
    }

    [Fact]
    public void AtATenDegreePhaseTheGeometricCentreIsFoundOnlyWhenThePhaseIsModelled()
    {
        // The sun at +u: the terminator eats a (1 - cos 10) = 0.9 px off the -u limb.
        var image = Render(k: 0.9, phaseDeg: 10, psf: Gaussian(1.8), noise: 0.005, seed: 3);

        var modelled = FitFromRoughStart(image, new LimbFitOptions(AxisRatio, PhaseAngleDeg: 10));
        var ignored = FitFromRoughStart(image, new LimbFitOptions(AxisRatio));

        TestContext.Current.TestOutputHelper?.WriteLine($"phase modelled: centre off by {Offset(modelled):0.000} px (side {modelled.SunSide}); ignored: {Offset(ignored):0.000} px");
        Offset(modelled).ShouldBeLessThan(0.05, "the geometric centre, with the terminator modelled");
        Offset(ignored).ShouldBeGreaterThan(0.2, "a fit that ignores the phase centres on the lit disk instead");
    }

    [Fact]
    public void AFitIsTheSameFitEveryTimeThoughItsRowsRunAtOnce()
    {
        // #1106: each model evaluation renders and blurs its rows in parallel bands (and a phased disk's searches run at once).
        // Every output gathers into its own cell in a fixed order, so the fit must not depend on how the work was shared.
        var image = Render(k: 0.9, phaseDeg: 0, psf: Gaussian(1.8), noise: 0.005, seed: 1);
        var options = new LimbFitOptions(AxisRatio);

        var first = FitFromRoughStart(image, options);
        var second = FitFromRoughStart(image, options);

        second.CenterX.ShouldBe(first.CenterX);
        second.CenterY.ShouldBe(first.CenterY);
        second.EquatorialRadius.ShouldBe(first.EquatorialRadius);
        second.AxisAngleDeg.ShouldBe(first.AxisAngleDeg);
        second.PsfSigma.ShouldBe(first.PsfSigma);
        second.RmsResidual.ShouldBe(first.RmsResidual);
        second.SunSide.ShouldBe(first.SunSide);
        second.Iterations.ShouldBe(first.Iterations);
    }

    [Fact]
    public void TheStartFindsADiskThatFillsAFifthOfTheFrame()
    {
        // A planetary stack's disk: the case PlanetaryDisk.BoundingBox's mean-plus-three-sigma threshold gets wrong (it
        // started the real 2022 stacks at 40 px for 74).
        var image = Render(k: 0.9, phaseDeg: 0, psf: Gaussian(1.8), noise: 0.005, seed: 4);

        var start = PlanetaryLimbFit.Start(image, Size, Size, AxisRatio).ShouldNotBeNull();

        start.X.ShouldBe(TrueX, 1.0);
        start.Y.ShouldBe(TrueY, 1.0);
        start.Radius.ShouldBe(TrueRadius, 3.0);
    }

    [Fact]
    public void AWinJuposMeasurementIsReadWithItsOutlineTimeAndMeridians()
    {
        var folder = _folders.Create("twwinjupos");
        var xml = Path.Combine(folder.FullName, "2022-09-03-1209.0.ims.xml");
        File.WriteAllText(xml, """
            <?xml version="1.0" encoding="UTF-8"?>
            <WinJUPOS Version="12.1.2">
              <ImageMeasurementSettings Version="1">
                <FileName>C:\Temp\Astro\SharpCap Captures\Jupiter\Light\stack.tif</FileName>
                <Body>Jupiter</Body>
                <JulianDateUT>2459826.00625</JulianDateUT>
                <GeoLongDeg>145.10000</GeoLongDeg>
                <GeoLatDeg>-37.88333</GeoLatDeg>
                <CenterXPixel>148.6</CenterXPixel>
                <CenterYPixel>151.5</CenterYPixel>
                <EquatorialRadiusPixel>74.0</EquatorialRadiusPixel>
                <RotationAngleDeg>69.000</RotationAngleDeg>
                <MirroredInvertedImage>False</MirroredInvertedImage>
                <CM1Deg>274.76</CM1Deg>
                <CM2Deg>67.06</CM2Deg>
                <CM3Deg>351.45</CM3Deg>
                <q>0.9351256575</q>
                <DeclinationOfEarthOnPlanetDeg>2.64</DeclinationOfEarthOnPlanetDeg>
              </ImageMeasurementSettings>
            </WinJUPOS>
            """);
        File.WriteAllBytes(Path.Combine(folder.FullName, "stack.tif"), [0]);

        var m = WinJuposMeasurement.TryRead(xml).ShouldNotBeNull();

        (m.Body, m.CenterX, m.CenterY, m.EquatorialRadius, m.RotationAngleDeg, m.Mirrored).ShouldBe(("Jupiter", 148.6, 151.5, 74.0, 69.0, false));
        (m.CentralMeridianI, m.CentralMeridianII, m.CentralMeridianIII, m.EarthDeclinationDeg).ShouldBe((274.76, 67.06, 351.45, 2.64));
        m.Utc.ShouldBe(new DateTimeOffset(2022, 9, 3, 12, 9, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        m.LocalImage(xml).ShouldBe(Path.Combine(folder.FullName, "stack.tif"), "the image beside the measurement, by the file name WinJUPOS recorded");
        WinJuposMeasurement.TryRead(Path.Combine(folder.FullName, "stack.tif")).ShouldBeNull("not WinJUPOS's XML");
    }

    private static double Offset(LimbFit fit) => Math.Sqrt(((fit.CenterX - TrueX) * (fit.CenterX - TrueX)) + ((fit.CenterY - TrueY) * (fit.CenterY - TrueY)));

    // Started a couple of pixels off, as a bounding box would start it.
    private static LimbFit FitFromRoughStart(float[] image, LimbFitOptions options)
        => PlanetaryLimbFit.Fit(image, Size, Size, TrueX + 1.6, TrueY - 1.2, TrueRadius + 2.5, options) ?? throw new InvalidOperationException("no fit");

    private static double[,] Gaussian(double sigma)
    {
        var r = (int)Math.Ceiling(4 * sigma);
        var k = new double[(2 * r) + 1, (2 * r) + 1];
        for (var y = -r; y <= r; y++)
        {
            for (var x = -r; x <= r; x++)
            {
                k[y + r, x + r] = Math.Exp(-((x * x) + (y * y)) / (2 * sigma * sigma));
            }
        }
        return k;
    }

    private static double[,] Moffat(double fwhm, double beta)
    {
        var alpha = fwhm / (2 * Math.Sqrt(Math.Pow(2, 1 / beta) - 1));
        var r = (int)Math.Ceiling(6 * fwhm);
        var k = new double[(2 * r) + 1, (2 * r) + 1];
        for (var y = -r; y <= r; y++)
        {
            for (var x = -r; x <= r; x++)
            {
                k[y + r, x + r] = Math.Pow(1 + (((x * x) + (y * y)) / (alpha * alpha)), -beta);
            }
        }
        return k;
    }

    /// <summary>
    /// An oblate disk (axis at <see cref="TrueAxisDeg"/>), Minnaert-darkened with exponent <paramref name="k"/> and lit at
    /// <paramref name="phaseDeg"/> from +u, 8x supersampled into pixels, convolved with <paramref name="psf"/> (normalised),
    /// scaled to 1 at the centre, with Gaussian noise of <paramref name="noise"/>.
    /// </summary>
    private static float[] Render(double k, double phaseDeg, double[,] psf, double noise, int seed)
    {
        const int super = 8;
        var sharp = new double[Size * Size];
        var theta = TrueAxisDeg * Math.PI / 180;
        var (cos, sin) = (Math.Cos(theta), Math.Sin(theta));
        var phase = phaseDeg * Math.PI / 180;
        for (var py = 0; py < Size; py++)
        {
            for (var px = 0; px < Size; px++)
            {
                double sum = 0;
                for (var sy = 0; sy < super; sy++)
                {
                    for (var sx = 0; sx < super; sx++)
                    {
                        var x = px - 0.5 + ((sx + 0.5) / super);
                        var y = py - 0.5 + ((sy + 0.5) / super);
                        var (dx, dy) = (x - TrueX, y - TrueY);
                        var v = ((dx * cos) + (dy * sin)) / (TrueRadius * AxisRatio);
                        var u = ((-dx * sin) + (dy * cos)) / TrueRadius;
                        var rho2 = (u * u) + (v * v);
                        if (rho2 >= 1)
                        {
                            continue;
                        }
                        var mu = Math.Sqrt(1 - rho2);
                        var mu0 = (u * Math.Sin(phase)) + (mu * Math.Cos(phase));
                        if (mu0 > 0)
                        {
                            sum += Math.Pow(mu0, k) * Math.Pow(Math.Max(mu, 1e-3), k - 1);
                        }
                    }
                }
                sharp[(py * Size) + px] = sum / (super * super);
            }
        }

        var kr = psf.GetLength(0) / 2;
        double total = 0;
        foreach (var w in psf)
        {
            total += w;
        }
        var rng = new Random(seed);
        var image = new float[Size * Size];
        for (var py = 0; py < Size; py++)
        {
            for (var px = 0; px < Size; px++)
            {
                double s = 0;
                for (var ky = -kr; ky <= kr; ky++)
                {
                    var yy = py + ky;
                    if (yy < 0 || yy >= Size)
                    {
                        continue;
                    }
                    for (var kx = -kr; kx <= kr; kx++)
                    {
                        var xx = px + kx;
                        if (xx >= 0 && xx < Size)
                        {
                            s += psf[ky + kr, kx + kr] * sharp[(yy * Size) + xx];
                        }
                    }
                }
                var u1 = 1 - rng.NextDouble();
                var u2 = rng.NextDouble();
                image[(py * Size) + px] = (float)((s / total) + (noise * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)));
            }
        }
        return image;
    }
}
