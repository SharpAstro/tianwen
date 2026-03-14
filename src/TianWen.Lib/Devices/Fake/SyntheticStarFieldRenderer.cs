using System;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Generates synthetic star field images with defocus-dependent PSF.
/// Stars are rendered as 2D Gaussians whose FWHM follows a hyperbolic
/// relationship with distance from best focus, matching the real optical model.
/// </summary>
internal static class SyntheticStarFieldRenderer
{
    /// <summary>
    /// Renders a synthetic star field into a float[height, width] array.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="defocusSteps">Absolute distance from true best focus in focuser steps.</param>
    /// <param name="hyperbolaA">Minimum FWHM in pixels at perfect focus (~2.0).</param>
    /// <param name="hyperbolaB">Asymptote scaling in steps (~50).</param>
    /// <param name="exposureSeconds">Exposure duration in seconds.</param>
    /// <param name="skyBackground">Base sky background per second.</param>
    /// <param name="readNoise">Read noise sigma in ADU.</param>
    /// <param name="starCount">Number of stars to generate.</param>
    /// <param name="seed">Random seed (0 = random).</param>
    /// <returns>Image data array with synthetic stars.</returns>
    public static float[,] Render(
        int width,
        int height,
        double defocusSteps,
        double hyperbolaA = 2.0,
        double hyperbolaB = 50.0,
        double exposureSeconds = 1.0,
        double skyBackground = 100.0,
        double readNoise = 5.0,
        int starCount = 50,
        int seed = 42)
    {
        return Render(width, height, defocusSteps, offsetX: 0, offsetY: 0,
            hyperbolaA, hyperbolaB, exposureSeconds, skyBackground, readNoise, starCount, seed);
    }

    /// <summary>
    /// Renders a synthetic star field with a sub-pixel offset applied to all star positions.
    /// Used by guider simulation to model tracking error and guide corrections:
    /// as the mount drifts, the star field shifts on the sensor.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="defocusSteps">Absolute distance from true best focus in focuser steps.</param>
    /// <param name="offsetX">Horizontal offset in pixels (positive = stars shift right).</param>
    /// <param name="offsetY">Vertical offset in pixels (positive = stars shift down).</param>
    /// <param name="hyperbolaA">Minimum FWHM in pixels at perfect focus (~2.0).</param>
    /// <param name="hyperbolaB">Asymptote scaling in steps (~50).</param>
    /// <param name="exposureSeconds">Exposure duration in seconds.</param>
    /// <param name="skyBackground">Base sky background per second.</param>
    /// <param name="readNoise">Read noise sigma in ADU.</param>
    /// <param name="starCount">Number of stars to generate.</param>
    /// <param name="seed">Random seed (0 = random).</param>
    /// <param name="hotPixelCount">Number of hot pixels to inject (value = maxADU).</param>
    /// <param name="maxADU">Maximum ADU value for hot pixels.</param>
    /// <param name="seeingArcsec">Atmospheric seeing FWHM in arcsec (added in quadrature with optical PSF).</param>
    /// <param name="pixelScaleArcsec">Pixel scale in arcsec/pixel (for converting seeing to pixels).</param>
    /// <returns>Image data array with synthetic stars at offset positions.</returns>
    public static float[,] Render(
        int width,
        int height,
        double defocusSteps,
        double offsetX,
        double offsetY,
        double hyperbolaA = 2.0,
        double hyperbolaB = 50.0,
        double exposureSeconds = 1.0,
        double skyBackground = 100.0,
        double readNoise = 5.0,
        int starCount = 50,
        int seed = 42,
        int hotPixelCount = 0,
        double maxADU = 4096.0,
        double seeingArcsec = 0.0,
        double pixelScaleArcsec = 1.5)
    {
        var rng = new Random(seed);
        var data = new float[height, width];

        // FWHM from defocus via hyperbola: fwhm = a * cosh(asinh(defocus / b))
        var opticalFwhm = hyperbolaA * Math.Cosh(Asinh(defocusSteps / hyperbolaB));

        // Add atmospheric seeing in quadrature (both are Gaussian, so sigma adds in quadrature)
        var seeingFwhmPixels = seeingArcsec > 0 && pixelScaleArcsec > 0
            ? seeingArcsec / pixelScaleArcsec
            : 0.0;
        var fwhm = Math.Sqrt(opticalFwhm * opticalFwhm + seeingFwhmPixels * seeingFwhmPixels);

        var sigma = fwhm / 2.3548; // FWHM = 2 * sqrt(2 * ln(2)) * sigma

        // Sky background — use separate RNG so star positions don't depend on image size
        var bgRng = new Random(seed + 1);
        var skyLevel = skyBackground * exposureSeconds;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(skyLevel + bgRng.NextDouble() * readNoise);
            }
        }

        // Generate all star positions and magnitudes first (deterministic regardless of offset/clipping)
        var psfRadius = (int)Math.Ceiling(sigma * 4);
        var stars = new (double X, double Y, double Flux)[starCount];

        for (var s = 0; s < starCount; s++)
        {
            var baseX = rng.NextDouble() * (width - 2 * psfRadius) + psfRadius;
            var baseY = rng.NextDouble() * (height - 2 * psfRadius) + psfRadius;
            var magnitude = 5.0 + rng.NextDouble() * 7.0;
            stars[s] = (baseX + offsetX, baseY + offsetY,
                10000.0 * Math.Pow(10, -0.4 * (magnitude - 5.0)) * exposureSeconds);
        }

        // Render stars with shot noise (separate RNG — clipping differences don't affect positions)
        var shotRng = new Random(seed + 2);
        var sigma2x2 = 2.0 * sigma * sigma;

        for (var s = 0; s < starCount; s++)
        {
            var (starX, starY, flux) = stars[s];
            var normalization = flux / (Math.PI * sigma2x2);

            var xMin = Math.Max(0, (int)(starX - psfRadius));
            var xMax = Math.Min(width - 1, (int)(starX + psfRadius));
            var yMin = Math.Max(0, (int)(starY - psfRadius));
            var yMax = Math.Min(height - 1, (int)(starY + psfRadius));

            for (var y = yMin; y <= yMax; y++)
            {
                var dy = y - starY;
                for (var x = xMin; x <= xMax; x++)
                {
                    var dx = x - starX;
                    var r2 = dx * dx + dy * dy;
                    var value = normalization * Math.Exp(-r2 / sigma2x2);

                    // Add Poisson-like shot noise
                    var noisy = value + Math.Sqrt(Math.Max(0, value)) * shotRng.NextDouble() * 0.5;
                    data[y, x] += (float)noisy;
                }
            }
        }

        // Inject hot pixels at random positions
        if (hotPixelCount > 0)
        {
            var hotRng = new Random(seed + 9999);
            for (var i = 0; i < hotPixelCount; i++)
            {
                var hx = hotRng.Next(width);
                var hy = hotRng.Next(height);
                data[hy, hx] = (float)maxADU;
            }
        }

        return data;
    }

    private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));
}
