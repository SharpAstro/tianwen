using System;

namespace TianWen.Lib.Devices.Fake;

/// <summary>
/// Renders a synthetic planet disk (Jupiter-like: limb-darkened body + latitude bands + an oval spot)
/// for the fake camera's video / planetary lucky-imaging path -- the live counterpart of
/// <see cref="SyntheticStarFieldRenderer"/>. The disk translates frame-to-frame (caller supplies the
/// centre), and a per-frame <paramref name="blurSigma"/> models atmospheric seeing: crisp frames (small
/// sigma) grade higher than soft ones, so the lucky-imaging stacker has something to rank. Surface
/// features sit at fixed disk-local positions, so they translate WITH the disk (no rotation -- planetary
/// de-rotation is out of scope for the live path), giving phase-correlation alignment stable structure
/// to lock onto.
/// </summary>
internal static class SyntheticPlanetRenderer
{
    /// <summary>
    /// Renders the disk into a <c>float[height, width]</c> array in ADU (<c>[0, maxAdu]</c>).
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="centerX">Disk centre X in frame pixels (may sit partly off-frame).</param>
    /// <param name="centerY">Disk centre Y in frame pixels.</param>
    /// <param name="radius">Disk radius in pixels.</param>
    /// <param name="blurSigma">Seeing blur sigma in pixels. &lt;= 0.35 px renders sharp (no convolution).</param>
    /// <param name="maxAdu">Full-scale ADU (sets the value range; the body peaks at <paramref name="bodyLevel"/> x this).</param>
    /// <param name="bodyLevel">Disk-centre brightness as a fraction of <paramref name="maxAdu"/>.</param>
    /// <param name="skyBackground">Sky-background ADU added everywhere.</param>
    /// <param name="readNoise">Read-noise sigma in ADU (added in quadrature with shot noise).</param>
    /// <param name="noiseSeed">Seed for the (deterministic) per-pixel noise draw.</param>
    /// <param name="dest">Optional reusable output buffer of the exact dimensions; allocated when null/mismatched.</param>
    public static float[,] Render(
        int width,
        int height,
        double centerX,
        double centerY,
        double radius,
        double blurSigma = 0.6,
        double maxAdu = 65535.0,
        double bodyLevel = 0.55,
        double skyBackground = 300.0,
        double readNoise = 8.0,
        int noiseSeed = 0,
        float[,]? dest = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);

        // 1) Sharp planet body: limb-darkened disk * latitude bands * an oval spot, all in disk-local coords.
        var sharp = new float[height, width];
        var peak = maxAdu * bodyLevel;
        var r2 = radius * radius;
        for (var y = 0; y < height; y++)
        {
            var dy = y - centerY;
            for (var x = 0; x < width; x++)
            {
                var dx = x - centerX;
                var rr = (dx * dx) + (dy * dy);
                if (rr > r2)
                {
                    continue; // sky -- left at 0; background + noise added in the compose pass
                }

                // Limb darkening: brighter at disk centre (mu = cos of the emission angle), never black at the limb.
                var mu = Math.Sqrt(Math.Max(0.0, 1.0 - (rr / r2)));
                var limb = 0.35 + (0.65 * mu);

                // Latitude belts (Jupiter-like): light/dark bands in disk-local Y.
                var lat = dy / radius; // -1..1 across the disk
                var bands = 1.0 + (0.18 * Math.Sin(lat * Math.PI * 3.0)) + (0.10 * Math.Sin((lat * Math.PI * 7.0) + 0.6));

                // Great-Red-Spot-like oval, fixed in disk-local coords (off-centre, southern band).
                var sxn = (dx - (radius * 0.28)) / (radius * 0.22);
                var syn = (dy - (radius * 0.32)) / (radius * 0.13);
                var spot = 1.0 - (0.45 * Math.Exp(-((sxn * sxn) + (syn * syn))));

                sharp[y, x] = (float)Math.Max(0.0, peak * limb * bands * spot);
            }
        }

        // 2) Seeing blur (separable Gaussian). Small sigma -> sharp ("lucky") frame, skipped below the floor.
        var body = blurSigma > 0.35 ? GaussianBlur(sharp, width, height, blurSigma) : sharp;

        // 3) Compose: sky background + body + (shot (+) read) noise, deterministic per noiseSeed. The
        // procedural disk keeps the legacy unity-gain noise (fullWell == maxAdu => 1 e-/ADU, read in ADU) so
        // the renderer-grading unit tests stay stable; the image-based JupiterTextureRenderer drives the
        // realistic low-electron planetary noise instead.
        return ComposeWithNoise(body, width, height, maxAdu, skyBackground,
            fullWellElectrons: maxAdu, readNoiseElectrons: readNoise, noiseSeed, dest);
    }

    /// <summary>
    /// Composes a (blurred) body buffer into the final frame: adds sky background + shot (+) read noise,
    /// deterministic per <paramref name="noiseSeed"/>, clamped to [0, <paramref name="maxAdu"/>]. Shared by
    /// the procedural disk and the image-based <see cref="JupiterTextureRenderer"/> so both noise identically.
    /// <para>
    /// Noise is modelled in the <b>electron</b> domain, not ADU: shot noise is Poisson in collected electrons
    /// (variance = electron count), and a short, high-gain planetary frame collects only a few hundred
    /// electrons even when the disk sits near mid-ADU -- so a single frame is genuinely grainy (the
    /// lucky-imaging premise; the stack averages it out). <paramref name="fullWellElectrons"/> is the electron
    /// count at <paramref name="maxAdu"/> (the system gain = <c>maxAdu / fullWellElectrons</c> ADU per
    /// electron); a low value = high gain = noisier. Modelling noise directly in ADU (the old
    /// <c>sqrt(signalAdu)</c> form) implies ~1 electron per ADU -> unrealistically clean frames. Calibrated
    /// against a real 8-bit planetary SER: disk at ~40% scale showed ~8% per-frame grain (~170 e-).
    /// </para>
    /// </summary>
    internal static float[,] ComposeWithNoise(
        float[,] body, int width, int height, double maxAdu, double skyBackground,
        double fullWellElectrons, double readNoiseElectrons, int noiseSeed, float[,]? dest = null)
    {
        var outArr = dest is not null && dest.GetLength(0) == height && dest.GetLength(1) == width
            ? dest
            : new float[height, width];
        var rng = new Random(noiseSeed);
        var aduPerElectron = maxAdu / Math.Max(1.0, fullWellElectrons);
        var readVarE = readNoiseElectrons * readNoiseElectrons;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var signalAdu = Math.Max(0.0, body[y, x] + skyBackground);
                var signalE = signalAdu / aduPerElectron;                  // collected electrons
                var noiseE = Math.Sqrt(signalE + readVarE);                // shot (Poisson) (+) read, in e-
                var v = signalAdu + (NextGaussian(rng) * noiseE * aduPerElectron);
                outArr[y, x] = (float)Math.Clamp(v, 0.0, maxAdu);
            }
        }

        return outArr;
    }

    // Separable Gaussian blur with edge clamping. Kernel radius = ceil(3*sigma).
    internal static float[,] GaussianBlur(float[,] src, int width, int height, double sigma)
    {
        var radius = (int)Math.Ceiling(3.0 * sigma);
        var kernel = new double[(2 * radius) + 1];
        var twoSigma2 = 2.0 * sigma * sigma;
        var sum = 0.0;
        for (var i = -radius; i <= radius; i++)
        {
            var w = Math.Exp(-(i * i) / twoSigma2);
            kernel[i + radius] = w;
            sum += w;
        }
        for (var i = 0; i < kernel.Length; i++)
        {
            kernel[i] /= sum;
        }

        // Horizontal pass src -> tmp.
        var tmp = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var acc = 0.0;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = Math.Clamp(x + k, 0, width - 1);
                    acc += src[y, xx] * kernel[k + radius];
                }
                tmp[y, x] = (float)acc;
            }
        }

        // Vertical pass tmp -> dst.
        var dst = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var acc = 0.0;
                for (var k = -radius; k <= radius; k++)
                {
                    var yy = Math.Clamp(y + k, 0, height - 1);
                    acc += tmp[yy, x] * kernel[k + radius];
                }
                dst[y, x] = (float)acc;
            }
        }

        return dst;
    }

    // Standard Box-Muller normal draw; deterministic given the supplied Random.
    private static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
