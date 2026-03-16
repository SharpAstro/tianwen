using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry.Catalogs;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// A star projected from sky coordinates (RA/Dec) to sensor pixel coordinates.
/// </summary>
/// <param name="PixelX">X position on the sensor in pixels.</param>
/// <param name="PixelY">Y position on the sensor in pixels.</param>
/// <param name="Magnitude">Visual magnitude (Johnson V).</param>
internal readonly record struct ProjectedStar(double PixelX, double PixelY, double Magnitude, double RA = 0, double Dec = 0);

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
        int seed = 42,
        int? noiseSeed = null)
    {
        return Render(width, height, defocusSteps, offsetX: 0, offsetY: 0,
            hyperbolaA, hyperbolaB, exposureSeconds, skyBackground, readNoise, starCount, seed,
            noiseSeed: noiseSeed);
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
    /// <param name="seeingJitterRng">Optional RNG for per-frame centroid jitter due to atmospheric turbulence.
    /// Each star's centroid is randomly shifted by a Gaussian offset whose sigma = seeing_FWHM / (2.35 * sqrt(exposure)).
    /// Pass a persistent Random instance (not seeded per frame) so jitter varies between frames.
    /// If null, no centroid jitter is applied (only PSF broadening).</param>
    /// <param name="noiseSeed">Optional separate seed for background and shot noise RNGs.
    /// When provided, star positions remain determined by <paramref name="seed"/> but noise varies
    /// per frame. When null, noise RNGs are derived from <paramref name="seed"/> (legacy behavior).</param>
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
        double pixelScaleArcsec = 1.5,
        Random? seeingJitterRng = null,
        int? noiseSeed = null)
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
        var bgRng = new Random(noiseSeed.HasValue ? noiseSeed.Value : seed + 1);
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

        // Apply per-frame seeing centroid jitter (atmospheric turbulence)
        // Short-exposure centroid wander: sigma_jitter = seeing_sigma / sqrt(exposure)
        // This models the residual tip-tilt after exposure averaging
        if (seeingJitterRng is not null && seeingFwhmPixels > 0 && exposureSeconds > 0)
        {
            var seeingSigmaPixels = seeingFwhmPixels / 2.3548;
            var jitterSigma = seeingSigmaPixels / Math.Sqrt(exposureSeconds);

            for (var s = 0; s < starCount; s++)
            {
                var (sx, sy, sf) = stars[s];
                // Box-Muller for Gaussian jitter
                var u1 = seeingJitterRng.NextDouble();
                var u2 = seeingJitterRng.NextDouble();
                var r = Math.Sqrt(-2.0 * Math.Log(Math.Max(u1, 1e-300)));
                var jitterX = r * Math.Cos(2.0 * Math.PI * u2) * jitterSigma;
                var jitterY = r * Math.Sin(2.0 * Math.PI * u2) * jitterSigma;
                stars[s] = (sx + jitterX, sy + jitterY, sf);
            }
        }

        // Render stars with shot noise (separate RNG — clipping differences don't affect positions)
        var shotRng = new Random(noiseSeed.HasValue ? noiseSeed.Value + 1 : seed + 2);
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

    /// <summary>
    /// Renders a synthetic star field using catalog star positions projected onto the sensor.
    /// When <paramref name="stars"/> is empty, falls back to random star generation.
    /// </summary>
    public static float[,] Render(
        int width,
        int height,
        double defocusSteps,
        ReadOnlySpan<ProjectedStar> stars,
        double offsetX = 0,
        double offsetY = 0,
        double hyperbolaA = 2.0,
        double hyperbolaB = 50.0,
        double exposureSeconds = 1.0,
        double skyBackground = 100.0,
        double readNoise = 5.0,
        int seed = 42,
        int hotPixelCount = 0,
        double maxADU = 4096.0,
        double seeingArcsec = 0.0,
        double pixelScaleArcsec = 1.5,
        Random? seeingJitterRng = null,
        int? noiseSeed = null)
    {
        if (stars.IsEmpty)
        {
            return Render(width, height, defocusSteps, offsetX, offsetY,
                hyperbolaA, hyperbolaB, exposureSeconds, skyBackground, readNoise,
                starCount: 50, seed, hotPixelCount, maxADU, seeingArcsec, pixelScaleArcsec,
                seeingJitterRng, noiseSeed);
        }

        var data = new float[height, width];

        // FWHM from defocus via hyperbola
        var opticalFwhm = hyperbolaA * Math.Cosh(Asinh(defocusSteps / hyperbolaB));
        var seeingFwhmPixels = seeingArcsec > 0 && pixelScaleArcsec > 0
            ? seeingArcsec / pixelScaleArcsec
            : 0.0;
        var fwhm = Math.Sqrt(opticalFwhm * opticalFwhm + seeingFwhmPixels * seeingFwhmPixels);
        var sigma = fwhm / 2.3548;

        // Sky background
        var bgRng = new Random(noiseSeed.HasValue ? noiseSeed.Value : seed + 1);
        var skyLevel = skyBackground * exposureSeconds;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                data[y, x] = (float)(skyLevel + bgRng.NextDouble() * readNoise);
            }
        }

        // Build star array from catalog projections with offset applied
        var psfRadius = (int)Math.Ceiling(sigma * 4);
        var starArray = new (double X, double Y, double Flux)[stars.Length];
        for (var s = 0; s < stars.Length; s++)
        {
            var star = stars[s];
            var flux = 10000.0 * Math.Pow(10, -0.4 * (star.Magnitude - 5.0)) * exposureSeconds;
            starArray[s] = (star.PixelX + offsetX, star.PixelY + offsetY, flux);
        }

        // Apply seeing jitter
        if (seeingJitterRng is not null && seeingFwhmPixels > 0 && exposureSeconds > 0)
        {
            var seeingSigmaPixels = seeingFwhmPixels / 2.3548;
            var jitterSigma = seeingSigmaPixels / Math.Sqrt(exposureSeconds);

            for (var s = 0; s < starArray.Length; s++)
            {
                var (sx, sy, sf) = starArray[s];
                var u1 = seeingJitterRng.NextDouble();
                var u2 = seeingJitterRng.NextDouble();
                var r = Math.Sqrt(-2.0 * Math.Log(Math.Max(u1, 1e-300)));
                var jitterX = r * Math.Cos(2.0 * Math.PI * u2) * jitterSigma;
                var jitterY = r * Math.Sin(2.0 * Math.PI * u2) * jitterSigma;
                starArray[s] = (sx + jitterX, sy + jitterY, sf);
            }
        }

        // Render stars with shot noise
        var shotRng = new Random(noiseSeed.HasValue ? noiseSeed.Value + 1 : seed + 2);
        var sigma2x2 = 2.0 * sigma * sigma;

        for (var s = 0; s < starArray.Length; s++)
        {
            var (starX, starY, flux) = starArray[s];
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
                    var noisy = value + Math.Sqrt(Math.Max(0, value)) * shotRng.NextDouble() * 0.5;
                    data[y, x] += (float)noisy;
                }
            }
        }

        // Inject hot pixels
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

    /// <summary>
    /// Projects catalog stars from the celestial object database onto sensor pixel coordinates
    /// using gnomonic (TAN) projection centered on the target.
    /// </summary>
    /// <param name="targetRA">Target center RA in hours (J2000).</param>
    /// <param name="targetDec">Target center Dec in degrees (J2000).</param>
    /// <param name="focalLengthMm">Telescope focal length in mm.</param>
    /// <param name="pixelSizeUm">Camera pixel size in micrometers.</param>
    /// <param name="width">Sensor width in pixels.</param>
    /// <param name="height">Sensor height in pixels.</param>
    /// <param name="db">Celestial object database with Tycho-2 star data.</param>
    /// <param name="magnitudeCutoff">Faintest magnitude to include. Stars fainter than this are skipped.
    /// Default 12.0 matches Tycho-2 completeness. For short exposures (&lt;5s), use ~8–10.</param>
    /// <returns>List of stars projected to pixel coordinates within sensor bounds.</returns>
    public static List<ProjectedStar> ProjectCatalogStars(
        double targetRA,
        double targetDec,
        double focalLengthMm,
        double pixelSizeUm,
        int width,
        int height,
        ICelestialObjectDB db,
        double magnitudeCutoff = 12.0)
    {
        const double Deg2Rad = Math.PI / 180.0;
        const double Rad2Arcsec = 206264.806;

        // Pixel scale in arcsec/pixel
        var pixelScaleArcsec = Rad2Arcsec * (pixelSizeUm * 1e-3) / focalLengthMm;

        // FOV in degrees (with margin for stars whose PSF extends into frame)
        var fovWidthDeg = width * pixelScaleArcsec / 3600.0;
        var fovHeightDeg = height * pixelScaleArcsec / 3600.0;
        var searchRadiusDeg = Math.Sqrt(fovWidthDeg * fovWidthDeg + fovHeightDeg * fovHeightDeg) / 2.0 + 0.1;

        // Center in radians
        var ra0Rad = targetRA * 15.0 * Deg2Rad; // hours → degrees → radians
        var dec0Rad = targetDec * Deg2Rad;
        var sinDec0 = Math.Sin(dec0Rad);
        var cosDec0 = Math.Cos(dec0Rad);

        // Query cells that overlap the FOV
        // IRaDecIndex cells are ~1° in Dec, ~(1/15)h in RA
        var decMin = targetDec - searchRadiusDeg;
        var decMax = targetDec + searchRadiusDeg;
        var raRadiusHours = searchRadiusDeg / (15.0 * Math.Max(Math.Cos(dec0Rad), 0.01));
        var raMinH = targetRA - raRadiusHours;
        var raMaxH = targetRA + raRadiusHours;

        var grid = db.CoordinateGrid;
        var queriedIndices = new HashSet<CatalogIndex>();

        // Step through RA/Dec cells (1/15 h RA steps, 1° Dec steps)
        var raStepH = 1.0 / 15.0;
        var decStep = 1.0;
        for (var dec = decMin; dec <= decMax + decStep; dec += decStep)
        {
            for (var ra = raMinH; ra <= raMaxH + raStepH; ra += raStepH)
            {
                var queryRA = ((ra % 24.0) + 24.0) % 24.0;
                var queryDec = Math.Clamp(dec, -90, 90);
                foreach (var index in grid[queryRA, queryDec])
                {
                    queriedIndices.Add(index);
                }
            }
        }

        // Project each star onto the sensor
        var result = new List<ProjectedStar>();
        var halfW = width / 2.0;
        var halfH = height / 2.0;

        foreach (var index in queriedIndices)
        {
            if (!db.TryLookupByIndex(index, out var obj))
            {
                continue;
            }

            if (obj.ObjectType is not ObjectType.Star || Half.IsNaN(obj.V_Mag))
            {
                continue;
            }

            var mag = (double)obj.V_Mag;
            if (mag > magnitudeCutoff)
            {
                continue;
            }
            var raRad = obj.RA * 15.0 * Deg2Rad;
            var decRad = obj.Dec * Deg2Rad;

            // Gnomonic (TAN) projection
            var sinDec = Math.Sin(decRad);
            var cosDec = Math.Cos(decRad);
            var deltaRA = raRad - ra0Rad;
            var cosC = sinDec0 * sinDec + cosDec0 * cosDec * Math.Cos(deltaRA);

            if (cosC <= 0)
            {
                continue; // Behind the tangent plane
            }

            // Standard coordinates in radians
            var xi = cosDec * Math.Sin(deltaRA) / cosC;
            var eta = (cosDec0 * sinDec - sinDec0 * cosDec * Math.Cos(deltaRA)) / cosC;

            // Convert to pixels (xi positive = East = -X in standard image orientation)
            var pixelX = halfW - xi * Rad2Arcsec / pixelScaleArcsec;
            var pixelY = halfH - eta * Rad2Arcsec / pixelScaleArcsec;

            // Keep stars within sensor bounds (with small margin for PSF)
            if (pixelX >= -20 && pixelX < width + 20 && pixelY >= -20 && pixelY < height + 20)
            {
                result.Add(new ProjectedStar(pixelX, pixelY, mag, obj.RA, (double)obj.Dec));
            }
        }

        return result;
    }

    private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));
}
