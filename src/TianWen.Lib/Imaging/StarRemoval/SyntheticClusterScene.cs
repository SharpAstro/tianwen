using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>How a synthetic cluster's optics render a star's three channels.</summary>
public enum ClusterOptics
{
    /// <summary>No chromatic aberration: every channel takes green's Moffat, with the field's elongation.</summary>
    Plain = 0,

    /// <summary>The master's own per-channel profiles (the plate builder's Moffat and residual table per channel): its axial
    /// colour, red wider than green, blue's wings longer.</summary>
    Measured = 1,

    /// <summary><see cref="Measured"/> plus lateral colour: each channel's centre magnified about the frame's centre by its
    /// own radial scale (<see cref="SyntheticClusterSpec.LateralPpm"/>).</summary>
    Lateral = 2,
}

/// <summary>
/// One synthetic crowded-cluster scene (docs/plans/star-remover-training.md, the LMC field). The SKY (where every star is,
/// how bright, what colour, the nebula) depends on <see cref="Seed"/> alone, so scenes that differ only in
/// <see cref="RotationDeg"/> or <see cref="Optics"/> hold the same stars: a rotation turns the sky and re-renders it through
/// optics that stay where the frame has them, as a camera rotator would.
/// </summary>
/// <param name="Size">The scene's side, pixels.</param>
/// <param name="FrameX">The scene's left edge in the master's frame: the optics (elongation, lateral colour) are the frame's
/// there.</param>
/// <param name="FrameY">The scene's top edge in the master's frame.</param>
/// <param name="RotationDeg">The sky's rotation about the scene's centre, degrees.</param>
/// <param name="ClusterStars">Resolved stars in the cluster, placed by a King profile.</param>
/// <param name="CoreRadiusPx">The King core radius.</param>
/// <param name="TidalRadiusPx">The King tidal radius, where the cluster ends.</param>
/// <param name="FieldDensity">Resolved field stars per square pixel, uniform.</param>
/// <param name="SaturatedStars">Saturated stars, drawn from the master's own saturated pool, placed as the others are.</param>
/// <param name="GlowShare">The unresolved light's share of all starlight (resolved plus unresolved): stars too faint to
/// find, rendered into the STARLESS truth, since a galaxy's unresolved light is not a star a remover takes.</param>
/// <param name="GlowSlope">The unresolved stars' luminance function, N(&gt;a) proportional to a to the minus this.</param>
/// <param name="NebulaStrength">The nebula's scale over the sky: 0 for none, 1 for the plate's own at
/// (<paramref name="NebulaX"/>, <paramref name="NebulaY"/>).</param>
/// <param name="NebulaX">Where in the master's frame the nebula's texture is drawn from.</param>
/// <param name="NebulaY">Where in the master's frame the nebula's texture is drawn from.</param>
/// <param name="Optics">How the channels are rendered.</param>
/// <param name="LateralPpm">Per channel, the radial scale of <see cref="ClusterOptics.Lateral"/>, parts per million.</param>
/// <param name="Seed">The sky's seed.</param>
/// <param name="SubClusters">Compact groups inside the cluster (an OB association's knots), each a King profile of core
/// <paramref name="SubClusterCorePx"/> and tidal radius six times it, centred where a cluster star would be.</param>
/// <param name="SubClusterShare">The share of the cluster's stars that belong to the groups.</param>
/// <param name="SubClusterCorePx">A group's core radius, pixels.</param>
public sealed record SyntheticClusterSpec(
    int Size, int FrameX, int FrameY, double RotationDeg,
    int ClusterStars, double CoreRadiusPx, double TidalRadiusPx, double FieldDensity, int SaturatedStars,
    double GlowShare, double GlowSlope,
    double NebulaStrength, int NebulaX, int NebulaY,
    ClusterOptics Optics, ImmutableArray<double> LateralPpm, int Seed,
    int SubClusters = 0, double SubClusterShare = 0.0, double SubClusterCorePx = 8.0);

/// <summary>A resolved star of the scene's truth, at its green centre in scene pixels.</summary>
public readonly record struct ClusterTruthStar(double X, double Y, ImmutableArray<double> Amplitudes, bool Saturated, bool InCluster);

/// <summary>
/// A rendered scene: the resolved stars alone (noise-free, no sky), the starless truth (sky, unresolved glow and nebula,
/// noise-free) and the composite (the two plus the master's noise, in its shape and at its level for the light there).
/// </summary>
public sealed record SyntheticClusterRender(
    float[][] Stars, float[][] Starless, float[][] Composite, ImmutableArray<ClusterTruthStar> Truth, int GlowStars,
    double[] StarFlux, double[] GlowFlux);

/// <summary>
/// What a master gives every scene drawn from it: its brightness and colour pool and profiles
/// (<see cref="InjectionPopulation"/>), its sky, its noise (level and shape) and its nebula (<see cref="SyntheticBackground"/>).
/// </summary>
/// <param name="Population">The master's stars, from its starless plate's catalogue.</param>
/// <param name="Background">The plate's synthetic background, the nebula's source.</param>
/// <param name="Sky">Per channel, the plate's darkest sky.</param>
/// <param name="Noise">Per channel, the master's pixel noise at that sky: its finest band (<see cref="NoiseField.BandSigmasOf"/>'s
/// band 0) over the finest band of a unit field of its shape.</param>
/// <param name="ResampleSigma">The noise's shape: <see cref="NoiseField.Warped"/>'s resampling width that matches the master's
/// band ratio.</param>
/// <param name="FrameWidth">The master's width, for the optical centre.</param>
/// <param name="FrameHeight">The master's height.</param>
public sealed record SyntheticClusterSource(
    InjectionPopulation Population, SyntheticBackground? Background, double[] Sky, double[] Noise, double ResampleSigma,
    int FrameWidth, int FrameHeight)
{
    /// <summary>
    /// Reads the sky and the noise off a master's starless <paramref name="plate"/> (its own units): the darkest of its
    /// 128 px blocks by median, its median per channel the sky, its finest-band noise the level, and the
    /// <see cref="NoiseField.Warped"/> resampling width whose band ratio is nearest the block's (0 to 2 px in quarters).
    /// </summary>
    public static (double[] Sky, double[] Noise, double ResampleSigma, double BandRatio) MeasureSkyAndNoise(Image plate, BitMatrix? absent)
    {
        const int Block = 128;
        var (channels, width, height) = plate.Shape;
        var lum = new float[width * height];
        for (var c = 0; c < channels; c++)
        {
            var span = plate.GetChannelSpan(c);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] += span[i] / channels;
            }
        }
        var best = (X: 0, Y: 0, Median: double.PositiveInfinity);
        var scratch = new float[Block * Block];
        for (var by = Block; by + (2 * Block) <= height; by += Block)
        {
            for (var bx = Block; bx + (2 * Block) <= width; bx += Block)
            {
                var n = 0;
                for (var y = by; y < by + Block; y++)
                {
                    for (var x = bx; x < bx + Block; x++)
                    {
                        if (absent is { } a && a[y, x])
                        {
                            continue;
                        }
                        scratch[n++] = lum[(y * width) + x];
                    }
                }
                if (n < Block * Block / 2)
                {
                    continue;
                }
                var median = StatisticsHelper.MedianAndMad(scratch.AsSpan(0, n)).Median;
                if (median > 0 && median < best.Median)
                {
                    best = (bx, by, median);
                }
            }
        }
        var sky = new double[channels];
        var noise = new double[channels];
        var ratio = 0.0;
        var cut = new float[Block * Block];
        for (var c = 0; c < channels; c++)
        {
            var span = plate.GetChannelSpan(c);
            for (var y = 0; y < Block; y++)
            {
                span.Slice(((best.Y + y) * width) + best.X, Block).CopyTo(cut.AsSpan(y * Block, Block));
            }
            // The bands first: MedianAndMad leaves its buffer holding the deviations.
            var bands = NoiseField.BandSigmasOf(cut, Block, Block);
            sky[c] = StatisticsHelper.MedianAndMad(cut.AsSpan()).Median;
            noise[c] = bands[0];
            ratio += bands[1] / bands[0] / channels;
        }
        var resample = 0.0;
        var nearest = double.PositiveInfinity;
        var unitBand0 = 1.0;
        for (var q = 0; q <= 8; q++)
        {
            var s = q * 0.25;
            var field = NoiseField.Warped(256, 256, 8, new Random(17 + q), s);
            var bands = NoiseField.BandSigmasOf(field, 256, 256);
            var r = bands[1] / bands[0];
            if (Math.Abs(r - ratio) < nearest)
            {
                nearest = Math.Abs(r - ratio);
                resample = s;
                unitBand0 = bands[0];
            }
        }
        // The finest band of a unit-variance field of that shape is unitBand0, so the master's pixel noise is its finest band
        // over that: the level the floors and the unresolved stars are set in, and the scale the noise field is drawn at.
        for (var c = 0; c < channels; c++)
        {
            noise[c] /= unitBand0;
        }
        return (sky, noise, resample, ratio);
    }
}

/// <summary>
/// Composes a synthetic crowded-cluster scene from a real master's measurements (docs/plans/star-remover-training.md): a
/// King-profile cluster and a uniform field of resolved stars whose brightness and colour are drawn from the master's own
/// catalogue (<see cref="InjectionPopulation.DrawBrightness"/>), rendered as the injector renders (<see cref="StarInjection"/>)
/// through one of three optics (<see cref="ClusterOptics"/>); under them, the sky, an unresolved glow of stars too faint to
/// find (in the starless truth) and optionally the plate's nebula (<see cref="SyntheticBackground"/>); over all, the master's
/// noise in its shape. Every random draw of the sky comes from the spec's seed in a fixed order, so a rotation or another
/// optics re-renders the same stars.
/// </summary>
public static class SyntheticClusterScene
{
    /// <summary>A star is rendered out to where its light falls below this many noise sigma.</summary>
    public const double FloorSigma = 0.02;

    /// <summary>The faintest and brightest unresolved star's green peak, in green's noise sigma. With a slope near 1 the light
    /// is spread evenly over each factor of brightness, so stars under a tenth of the noise would add almost nothing to the
    /// glow but nearly all the count.</summary>
    public const double GlowMinSigma = 0.1, GlowMaxSigma = 1.0;

    /// <summary>Virtual subs averaged into the noise's shape (<see cref="NoiseField.Warped"/>).</summary>
    public const int NoiseRealisations = 8;

    private const int Margin = 32;

    /// <summary>Renders one scene.</summary>
    public static SyntheticClusterRender Render(SyntheticClusterSource source, SyntheticClusterSpec spec)
    {
        var population = source.Population;
        var channels = source.Sky.Length;
        var size = spec.Size;
        var n = size * size;
        var centre = (size - 1) / 2.0;
        var (sin, cos) = Math.SinCos(spec.RotationDeg * Math.PI / 180.0);
        (double X, double Y) ToScene(double sx, double sy) => (centre + (cos * sx) - (sin * sy), centre + (sin * sx) + (cos * sy));
        bool InScene((double X, double Y) p) => p.X >= -Margin && p.Y >= -Margin && p.X < size + Margin && p.Y < size + Margin;

        // The sky, in sky coordinates about the scene's centre: a disc wide enough that any rotation covers the scene.
        var rng = new Random(spec.Seed);
        var g = Math.Min(1, channels - 1);
        // A star's brightness is the pool's, its colour a BRIGHT pool star's: a faint star's fitted colour is mostly its own
        // noise, and the render adds the master's noise again, which painted the faint field in saturated reds and blues.
        var colours = ColourLibrary(population, g, spec.Seed);
        var colourRng = new Random(spec.Seed ^ 0x3c6ef372);
        ImmutableArray<double> Recolour(ImmutableArray<double> drawn)
        {
            var colour = colours[colourRng.Next(colours.Length)];
            return [.. colour.Select(c => drawn[g] * c / colour[g])];
        }
        var reach = (size * Math.Sqrt(0.5)) + Margin;
        var fieldCount = (int)Math.Round(spec.FieldDensity * Math.PI * reach * reach);
        var resolved = new List<(double X, double Y, ImmutableArray<double> A, ImmutableArray<double> Clips, bool Saturated, bool InCluster)>();
        var groups = Enumerable.Range(0, Math.Max(0, spec.SubClusters))
            .Select(_ => KingPosition(rng, spec.CoreRadiusPx, spec.TidalRadiusPx)).ToArray();
        (double X, double Y) ClusterPosition()
        {
            if (groups.Length > 0 && rng.NextDouble() < spec.SubClusterShare)
            {
                var (gx, gy) = groups[rng.Next(groups.Length)];
                var (dx, dy) = KingPosition(rng, spec.SubClusterCorePx, 6 * spec.SubClusterCorePx);
                return (gx + dx, gy + dy);
            }
            return KingPosition(rng, spec.CoreRadiusPx, spec.TidalRadiusPx);
        }
        for (var i = 0; i < spec.ClusterStars; i++)
        {
            var (x, y) = ClusterPosition();
            var (a, clips) = population.DrawBrightness(rng, saturated: false);
            resolved.Add((x, y, Recolour(a), clips, false, true));
        }
        for (var i = 0; i < fieldCount; i++)
        {
            var (x, y) = DiscPosition(rng, reach);
            var (a, clips) = population.DrawBrightness(rng, saturated: false);
            resolved.Add((x, y, Recolour(a), clips, false, false));
        }
        var clusterShare = spec.ClusterStars + fieldCount > 0 ? (double)spec.ClusterStars / (spec.ClusterStars + fieldCount) : 0.0;
        for (var i = 0; i < spec.SaturatedStars; i++)
        {
            var inCluster = rng.NextDouble() < clusterShare;
            var (x, y) = inCluster ? ClusterPosition() : DiscPosition(rng, reach);
            var (a, clips) = population.DrawBrightness(rng, saturated: true);
            resolved.Add((x, y, a, clips, true, inCluster));
        }

        // The unresolved stars: a truncated power law in green peak below detection, each a real star's colour, placed as
        // the resolved ones are, until their light is GlowShare of all starlight (the resolved stars' read off green's Moffat).
        // Both are counted inside the scene's inscribed circle, which every rotation shares, so the draw does not depend on it.
        var greenProfile = population.ProfilesAt(spec.FrameX + centre, spec.FrameY + centre, StarProfileFamily.Moffat)[g];
        var unitFlux = MoffatFlux(greenProfile);
        var inscribed = size / 2.0;
        bool InCircle(double x, double y) => (x * x) + (y * y) <= inscribed * inscribed;
        var resolvedFlux = resolved.Where(s => InCircle(s.X, s.Y)).Sum(s => s.A[g]) * unitFlux;
        var glowTarget = spec.GlowShare is > 0 and < 1 ? resolvedFlux * spec.GlowShare / (1 - spec.GlowShare) : 0.0;
        // Each unresolved star is its place, its green peak and the colour it was drawn with (a pool star's amplitudes, shared,
        // not copied): tens of millions can be needed.
        var glow = new List<(float X, float Y, float Peak, ImmutableArray<double> Colour)>();
        var glowInCircle = 0.0;
        var noiseG = source.Noise[g];
        var lo = GlowMinSigma * noiseG;
        var hi = GlowMaxSigma * noiseG;
        while (glowInCircle < glowTarget && glow.Count < 40_000_000)
        {
            var inCluster = rng.NextDouble() < clusterShare;
            var (x, y) = inCluster ? ClusterPosition() : DiscPosition(rng, reach);
            var peak = TruncatedPowerLaw(rng, lo, hi, spec.GlowSlope);
            glow.Add(((float)x, (float)y, (float)peak, colours[rng.Next(colours.Length)]));
            if (InCircle(x, y))
            {
                glowInCircle += peak * unitFlux;
            }
        }

        // The nebula's texture, drawn wider than the scene and turned with the sky.
        float[][]? nebula = null;
        if (spec.NebulaStrength > 0 && source.Background is { } background)
        {
            var wide = (int)Math.Ceiling(size * Math.Sqrt(2.0)) + 4;
            var drawn = background.Preview(spec.NebulaX, spec.NebulaY, wide, source.Noise, noisy: false, new Random(spec.Seed ^ 0x5bd1e995), withKnots: false);
            nebula = new float[channels][];
            var filled = FillPits(drawn, wide, NebulaPitSigmaPx);
            for (var c = 0; c < channels; c++)
            {
                var floor = Percentile(filled[c], 0.05);
                var turned = Rotate(filled[c], wide, size, spec.RotationDeg);
                for (var i = 0; i < n; i++)
                {
                    turned[i] = (float)(Math.Max(0.0, turned[i] - floor) * spec.NebulaStrength);
                }
                nebula[c] = turned;
            }
        }

        // The starless truth: sky, nebula, and the unresolved stars deposited at each channel's centre and blurred to that
        // channel's FWHM (they are unresolved: their wings are part of the glow they make, and a Gaussian of the core's width
        // carries it).
        var starless = new float[channels][];
        var glowFlux = new double[channels];
        var midX = spec.FrameX + centre;
        var midY = spec.FrameY + centre;
        var midProfiles = ProfilesFor(population, spec.Optics, midX, midY, channels);
        for (var c = 0; c < channels; c++)
        {
            var deposit = new float[n];
            var channelFlux = MoffatFlux(midProfiles[c]);
            foreach (var (x, y, peak, colour) in glow)
            {
                var (px, py) = ChannelCentre(spec, source, ToScene(x, y), c);
                var cg = colour[g] > 0 ? colour[g] : colour.Max();
                var flux = (cg > 0 ? peak * colour[c] / cg : peak) * channelFlux;
                if (DepositBilinear(deposit, size, px, py, flux))
                {
                    glowFlux[c] += flux;
                }
            }
            var sigma = (float)(midProfiles[c].FwhmPx / 2.3548);
            var blurred = Image.SeparableGaussianBlur(deposit, size, size, sigma);
            var plane = new float[n];
            for (var i = 0; i < n; i++)
            {
                plane[i] = (float)source.Sky[c] + blurred[i] + (nebula?[c][i] ?? 0f);
            }
            starless[c] = plane;
        }

        // The resolved stars, rendered onto the starless planes channel by channel (so a saturated star clips against the
        // light under it, as the sensor clips the sum), each channel at its own centre and profile.
        var composite = new float[channels][];
        var stars = new float[channels][];
        var starFlux = new double[channels];
        var truth = ImmutableArray.CreateBuilder<ClusterTruthStar>();
        var subs = new Random(spec.Seed ^ 0x27d4eb2f);
        var placed = resolved.Select(s => (Star: s, At: ToScene(s.X, s.Y))).Where(t => InScene(t.At)).ToArray();
        foreach (var (s, at) in placed)
        {
            if (at.X >= 0 && at.Y >= 0 && at.X < size && at.Y < size)
            {
                var (gx, gy) = ChannelCentre(spec, source, at, g);
                truth.Add(new ClusterTruthStar(gx, gy, s.A, s.Saturated, s.InCluster));
            }
        }
        // The optics vary slowly over the frame (the elongation is the nearest measured stars' mean), so each star takes the
        // profiles of the nearest node of a 32 px grid rather than a lookup of its own.
        const int Node = 32;
        var nodes = (size + Node - 1) / Node + 1;
        var grid = new ImmutableArray<StarProfile>[nodes * nodes];
        for (var j = 0; j < nodes; j++)
        {
            for (var i = 0; i < nodes; i++)
            {
                grid[(j * nodes) + i] = ProfilesFor(population, spec.Optics, spec.FrameX + (i * Node), spec.FrameY + (j * Node), channels);
            }
        }
        ImmutableArray<StarProfile> ProfilesNear((double X, double Y) at)
            => grid[(Math.Clamp((int)Math.Round(at.Y / Node), 0, nodes - 1) * nodes) + Math.Clamp((int)Math.Round(at.X / Node), 0, nodes - 1)];
        for (var c = 0; c < channels; c++)
        {
            var injected = new InjectedStar[placed.Length];
            for (var k = 0; k < placed.Length; k++)
            {
                var (s, at) = placed[k];
                var (px, py) = ChannelCentre(spec, source, at, c);
                var profile = ProfilesNear(at)[c];
                injected[k] = new InjectedStar(px, py, [s.A[c]], [profile], s.Saturated, s.Saturated ? [s.Clips[c]] : ImmutableArray<double>.Empty);
            }
            var floor = FloorSigma * source.Noise[c];
            var render = StarInjection.Render([starless[c]], size, size, null, injected, [floor], subs);
            composite[c] = render.Planes[0];
            var only = new float[n];
            for (var i = 0; i < n; i++)
            {
                only[i] = composite[c][i] - starless[c][i];
                starFlux[c] += only[i];
            }
            stars[c] = only;
        }

        // The master's noise: its shape (Warped at its resampling width, unit variance), its pixel level at the sky, growing
        // with the light as shot noise does.
        var noise = new Random(spec.Seed ^ 0x165667b1);
        for (var c = 0; c < channels; c++)
        {
            var field = NoiseField.Warped(size, size, NoiseRealisations, noise, source.ResampleSigma);
            var scale = source.Noise[c];
            var sky = Math.Max(1e-12, source.Sky[c]);
            for (var i = 0; i < n; i++)
            {
                var level = Math.Max(sky, composite[c][i]);
                composite[c][i] += (float)(field[i] * scale * Math.Sqrt(level / sky));
            }
        }
        return new SyntheticClusterRender(stars, starless, composite, truth.ToImmutable(), glow.Count, starFlux, glowFlux);
    }

    /// <summary>Stars the colours are drawn from: the brightest quarter (in green) of 4,000 pool draws, every channel above zero.</summary>
    public const int ColourDraws = 4000;

    private static ImmutableArray<double>[] ColourLibrary(InjectionPopulation population, int green, int seed)
    {
        var rng = new Random(seed ^ 0x6a09e667);
        var draws = Enumerable.Range(0, ColourDraws).Select(_ => population.DrawBrightness(rng, saturated: false).Amplitudes)
            .Where(a => a.All(static v => v > 0)).OrderByDescending(a => a[green]).ToArray();
        return draws.Length == 0 ? [Enumerable.Repeat(1.0, 3).ToImmutableArray()] : draws[..Math.Max(1, draws.Length / 4)];
    }

    /// <summary>Each channel's profile at a frame position under <paramref name="optics"/>.</summary>
    public static ImmutableArray<StarProfile> ProfilesFor(InjectionPopulation population, ClusterOptics optics, double frameX, double frameY, int channels)
    {
        if (optics == ClusterOptics.Plain)
        {
            var green = population.ProfilesAt(frameX, frameY, StarProfileFamily.Moffat)[Math.Min(1, channels - 1)];
            return [.. Enumerable.Repeat(green, channels)];
        }
        return population.ProfilesAt(frameX, frameY, StarProfileFamily.Field);
    }

    // A channel's centre for a star at scene position `at`: under lateral colour, magnified about the frame's centre by the
    // channel's radial scale.
    private static (double X, double Y) ChannelCentre(SyntheticClusterSpec spec, SyntheticClusterSource source, (double X, double Y) at, int channel)
    {
        if (spec.Optics != ClusterOptics.Lateral || spec.LateralPpm.IsDefaultOrEmpty)
        {
            return at;
        }
        var k = 1e-6 * spec.LateralPpm[Math.Min(channel, spec.LateralPpm.Length - 1)];
        var ox = ((source.FrameWidth - 1) / 2.0) - spec.FrameX;
        var oy = ((source.FrameHeight - 1) / 2.0) - spec.FrameY;
        return (ox + ((at.X - ox) * (1 + k)), oy + ((at.Y - oy) * (1 + k)));
    }

    /// <summary>A Moffat profile's integral at peak 1 (its residual table aside): pi alpha^2 / (beta - 1), the area the
    /// elongation keeps.</summary>
    public static double MoffatFlux(StarProfile profile)
    {
        var beta = Math.Max(1.05, profile.Beta);
        var alpha = profile.FwhmPx / (2.0 * Math.Sqrt(Math.Pow(2.0, 1.0 / beta) - 1.0));
        return Math.PI * alpha * alpha / (beta - 1.0);
    }

    /// <summary>
    /// A position drawn from King's (1962) projected profile, density proportional to
    /// (1 / sqrt(1 + (r/rc)^2) - 1 / sqrt(1 + (rt/rc)^2))^2 out to the tidal radius, at a uniform angle.
    /// </summary>
    internal static (double X, double Y) KingPosition(Random rng, double rc, double rt)
    {
        var edge = 1.0 / Math.Sqrt(1 + (rt / rc * (rt / rc)));
        double Density(double r) => Math.Pow(Math.Max(0.0, (1.0 / Math.Sqrt(1 + (r / rc * (r / rc)))) - edge), 2) * r;
        var peak = 0.0;
        for (var i = 1; i <= 200; i++)
        {
            peak = Math.Max(peak, Density(rt * i / 200.0));
        }
        while (true)
        {
            var r = rng.NextDouble() * rt;
            if (rng.NextDouble() * peak * 1.05 <= Density(r))
            {
                var angle = rng.NextDouble() * 2 * Math.PI;
                return (r * Math.Cos(angle), r * Math.Sin(angle));
            }
        }
    }

    private static (double X, double Y) DiscPosition(Random rng, double radius)
    {
        var r = radius * Math.Sqrt(rng.NextDouble());
        var angle = rng.NextDouble() * 2 * Math.PI;
        return (r * Math.Cos(angle), r * Math.Sin(angle));
    }

    /// <summary>A draw from N(&gt;a) proportional to a^-slope between <paramref name="lo"/> and <paramref name="hi"/>.</summary>
    internal static double TruncatedPowerLaw(Random rng, double lo, double hi, double slope)
    {
        var u = rng.NextDouble();
        if (Math.Abs(slope) < 1e-9)
        {
            return lo * Math.Pow(hi / lo, u);
        }
        var a = Math.Pow(lo, -slope);
        var b = Math.Pow(hi, -slope);
        return Math.Pow(a - (u * (a - b)), -1.0 / slope);
    }

    private static bool DepositBilinear(float[] plane, int size, double x, double y, double flux)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        if (x0 < -1 || y0 < -1 || x0 >= size || y0 >= size)
        {
            return false;
        }
        var fx = x - x0;
        var fy = y - y0;
        var any = false;
        for (var dy = 0; dy <= 1; dy++)
        {
            for (var dx = 0; dx <= 1; dx++)
            {
                var px = x0 + dx;
                var py = y0 + dy;
                if (px < 0 || py < 0 || px >= size || py >= size)
                {
                    continue;
                }
                var w = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                plane[(py * size) + px] += (float)(flux * w);
                any = true;
            }
        }
        return any;
    }

    // The centre `size` px of a `wide` px plane turned by `degrees` about its centre, bilinear.
    private static float[] Rotate(float[] wide, int wideSize, int size, double degrees)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180.0);
        var c0 = (wideSize - 1) / 2.0;
        var c1 = (size - 1) / 2.0;
        var output = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // The output pixel's place on the sky: the inverse rotation.
                var dx = x - c1;
                var dy = y - c1;
                var sx = c0 + (cos * dx) + (sin * dy);
                var sy = c0 - (sin * dx) + (cos * dy);
                var ix = Math.Clamp((int)Math.Floor(sx), 0, wideSize - 2);
                var iy = Math.Clamp((int)Math.Floor(sy), 0, wideSize - 2);
                var fx = Math.Clamp(sx - ix, 0, 1);
                var fy = Math.Clamp(sy - iy, 0, 1);
                var i = (iy * wideSize) + ix;
                output[(y * size) + x] = (float)(((1 - fy) * (((1 - fx) * wide[i]) + (fx * wide[i + 1])))
                    + (fy * (((1 - fx) * wide[i + wideSize]) + (fx * wide[i + wideSize + 1]))));
            }
        }
        return output;
    }

    /// <summary>The smoothing the nebula's pits are filled to, sigma in pixels.</summary>
    public const float NebulaPitSigmaPx = 6f;

    // The plate keeps pits where it was crowded (its builder's residue at subtracted stars, which the coarse scales carry):
    // wherever the luminance falls below its own smoothing, the deficit is added back in every channel at the local colour,
    // so the pits fill and nothing bright, and no channel on its own, is touched.
    private static float[][] FillPits(float[][] planes, int size, float sigma)
    {
        var channels = planes.Length;
        var n = size * size;
        var lum = new float[n];
        foreach (var plane in planes)
        {
            for (var i = 0; i < n; i++)
            {
                lum[i] += plane[i] / channels;
            }
        }
        var smoothLum = Image.SeparableGaussianBlur(lum, size, size, sigma);
        var output = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var smooth = Image.SeparableGaussianBlur(planes[c], size, size, sigma);
            var plane = new float[n];
            for (var i = 0; i < n; i++)
            {
                var deficit = Math.Max(0f, smoothLum[i] - lum[i]);
                plane[i] = planes[c][i] + (smoothLum[i] > 0 ? deficit * smooth[i] / smoothLum[i] : 0f);
            }
            output[c] = plane;
        }
        return output;
    }

    private static double Percentile(float[] values, double q)
    {
        var copy = new float[values.Length];
        values.AsSpan().CopyTo(copy);
        Array.Sort(copy);
        return copy[Math.Clamp((int)(q * (copy.Length - 1)), 0, copy.Length - 1)];
    }
}
