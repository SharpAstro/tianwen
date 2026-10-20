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
/// find. Each is drawn as a star in the composite, and the STARLESS truth holds their smooth expected light (a galaxy's
/// unresolved light is not a star a remover takes, and its grain is).</param>
/// <param name="GlowSlope">The unresolved stars' luminance function, N(&gt;a) proportional to a to the minus this.</param>
/// <param name="NebulaStrength">The nebula's scale over the sky: 0 for none, 1 for the plate's own at
/// (<paramref name="NebulaX"/>, <paramref name="NebulaY"/>).</param>
/// <param name="NebulaX">Where in the master's frame the nebula's texture is drawn from.</param>
/// <param name="NebulaY">Where in the master's frame the nebula's texture is drawn from.</param>
/// <param name="Optics">How the channels are rendered.</param>
/// <param name="LateralPpm">Per channel, the radial scale of <see cref="ClusterOptics.Lateral"/>, parts per million.</param>
/// <param name="Seed">The sky's seed.</param>
/// <param name="SubClusters">Compact clusters inside the cluster (the LMC's own catalogued star clusters), each a King profile of
/// concentration <see cref="SyntheticClusterScene.SubClusterConcentration"/> centred where a cluster star would be, with its
/// own resolved stars and its own unresolved light, all three drawn per group from the ranges below. The defaults are the
/// LMC's catalogued clusters in the Tarantula master at 2.87 arcsec a pixel, measured on its starless plate and its catalogue
/// (44 clusters, catalogued diameters 14 to 94 px): the unresolved light's half-light radius 4.1 to 11.8 px from the 10th to
/// the 90th percentile (median 8.0), resolved stars over the field's 0 to 15 (median 5), unresolved light 0.01 to 0.76 (median
/// 0.23, the master's units: green amplitude times pixels).</param>
/// <param name="SubClusterHalfLightMinPx">The smallest group's half-light radius, pixels (log-uniform between the two).</param>
/// <param name="SubClusterHalfLightMaxPx">The largest group's half-light radius, pixels.</param>
/// <param name="SubClusterStarsMin">The fewest resolved stars in a group (uniform between the two).</param>
/// <param name="SubClusterStarsMax">The most resolved stars in a group.</param>
/// <param name="SubClusterGlowMin">The least unresolved light in a group, the master's units (log-uniform between the two).</param>
/// <param name="SubClusterGlowMax">The most unresolved light in a group.</param>
/// <param name="MinSeparationPx">The least distance between two resolved stars: the catalogue they are drawn from is already
/// de-blended, so two placed closer than its builder separates become one brighter star and the brightness function runs
/// bright (twice too many stars of significance 20 to 1,000 on the LMC field without it). 0 for none.</param>
/// <param name="NebulaKnots">Whether the nebula carries the background generator's own knots (its nebula knots, a compact one
/// held faint, <see cref="SyntheticBackground.KnotsPerCell"/> per <see cref="SyntheticBackground.KnotCellPx"/> px cell by
/// area), listed per scene as <see cref="SyntheticClusterRender.Knots"/>.</param>
public sealed record SyntheticClusterSpec(
    int Size, int FrameX, int FrameY, double RotationDeg,
    int ClusterStars, double CoreRadiusPx, double TidalRadiusPx, double FieldDensity, int SaturatedStars,
    double GlowShare, double GlowSlope,
    double NebulaStrength, int NebulaX, int NebulaY,
    ClusterOptics Optics, ImmutableArray<double> LateralPpm, int Seed,
    int SubClusters = 0, double SubClusterHalfLightMinPx = 4.1, double SubClusterHalfLightMaxPx = 11.8,
    int SubClusterStarsMin = 2, int SubClusterStarsMax = 12, double SubClusterGlowMin = 0.1, double SubClusterGlowMax = 0.8,
    double MinSeparationPx = 0.0, bool NebulaKnots = false);

/// <summary>One of a scene's compact groups, as rendered: its centre in scene pixels, its half-light radius, its resolved
/// stars, its unresolved light (green, the master's units) and whether the starless truth holds that light as smooth light
/// (only at <see cref="SyntheticClusterScene.SmoothMinHalfLightPx"/> and up; else it is all in the stars truth).</summary>
public readonly record struct ClusterGroup(double X, double Y, double HalfLightPx, int ResolvedStars, double UnresolvedLight, bool Smooth);

/// <summary>A resolved star of the scene's truth, at its green centre in scene pixels.</summary>
public readonly record struct ClusterTruthStar(double X, double Y, ImmutableArray<double> Amplitudes, bool Saturated, bool InCluster);

/// <summary>
/// A rendered scene. The composite is the sky, the nebula, every star (resolved and unresolved, each drawn as a star) and the
/// master's noise, in its shape and at its level for the light there. The starless truth (noise-free) is the sky, the nebula
/// and the broad unresolved populations' light (<see cref="SyntheticClusterScene.SmoothMinHalfLightPx"/>): their density
/// times their light, through the PSF, never their points, with their own grain from
/// <see cref="SyntheticClusterScene.GrainFromScale"/> up. The stars truth is the composite, noise-free, less the starless:
/// every star, a compact cluster's unresolved ones whole, less that light, so it is signed (below zero where the faint stars
/// fall short of their expectation).
/// </summary>
/// <param name="Stars">The stars truth.</param>
/// <param name="Starless">The starless truth.</param>
/// <param name="Composite">The composite.</param>
/// <param name="Unresolved">The unresolved stars the starless truth's smooth light stands for, drawn as stars, noise-free and
/// with no sky (a compact cluster's are not among them).</param>
/// <param name="Truth">The resolved stars.</param>
/// <param name="GlowStars">How many unresolved stars were drawn.</param>
/// <param name="StarFlux">Per channel, the resolved stars' light in the scene.</param>
/// <param name="GlowFlux">Per channel, those unresolved stars' light in the scene (<paramref name="Unresolved"/>'s sum).</param>
/// <param name="SurfaceFlux">Per channel, the smooth expected light's sum in the scene: <paramref name="GlowFlux"/> but for the
/// draw's own scatter.</param>
/// <param name="NebulaAt">Where in the master's frame the nebula was drawn from, inside the frame by
/// <see cref="SyntheticClusterScene.NebulaEdgeMarginPx"/>.</param>
/// <param name="Groups">The compact groups.</param>
/// <param name="StarlessNoisy">The starless truth with the master's noise at its own level, the draw the composite is built on:
/// the composite is it plus the stars plus the stars' own shot noise, so it is a remover's training target, as an injected
/// draw's plate is, and what to set beside a real starless, which carries its master's noise (a noise-free truth
/// hard-stretched to 8 bits draws terraces).</param>
/// <param name="Knots">The nebula's knots where the scene shows them (scene pixels, their axes turned with the sky, their peaks
/// per channel at the nebula's strength): fine structure in the starless truth that is not the PSF.</param>
public sealed record SyntheticClusterRender(
    float[][] Stars, float[][] Starless, float[][] Composite, float[][] Unresolved, ImmutableArray<ClusterTruthStar> Truth, int GlowStars,
    double[] StarFlux, double[] GlowFlux, double[] SurfaceFlux, (int X, int Y) NebulaAt, ImmutableArray<ClusterGroup> Groups,
    float[][] StarlessNoisy, ImmutableArray<SyntheticBackground.Knot> Knots);

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
/// <param name="NebulaSky">Per channel, the darkest sky of the plate the nebula is read from, when not the source's own
/// (<see cref="Sky"/>): the level the nebula is drawn above.</param>
public sealed record SyntheticClusterSource(
    InjectionPopulation Population, SyntheticBackground? Background, double[] Sky, double[] Noise, double ResampleSigma,
    int FrameWidth, int FrameHeight, double[]? NebulaSky = null)
{
    /// <summary>
    /// Channel <paramref name="channel"/>'s noise as the exporter states one (<see cref="LinearDegradation.NoiseCalibration"/>):
    /// <see cref="Noise"/> at <see cref="Sky"/>, its variance in proportion to the light above a zero pedestal, at the master's
    /// own depth (one frame, <c>SigmaAt(level, 1.0)</c>). The scene draws its noise by it and a tile's conditioning plane is
    /// computed from it, so the plane is the noise the tile holds.
    /// </summary>
    public LinearDegradation.NoiseCalibration NoiseCalibration(int channel)
        => new(PedestalAdu: 0.0, BackgroundAdu: Math.Max(1e-12, Sky[channel]), OneSubSigmaAdu: Noise[channel], StackedFrames: 1);

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

    /// <summary>
    /// The master's noise in each B3 starlet band, per channel: the RMS of a unit field of the noise's shape
    /// (<see cref="NoiseField.Warped"/> at <paramref name="resampleSigma"/>, <see cref="SyntheticClusterScene.NoiseRealisations"/>
    /// subs) per band, away from its edge, times each channel's pixel noise.
    /// </summary>
    public static double[][] NoiseBandRms(double[] noise, double resampleSigma, int scales)
    {
        const int Size = 512;
        var field = NoiseField.Warped(Size, Size, SyntheticClusterScene.NoiseRealisations, new Random(0x2545f491), resampleSigma);
        var decomposition = ATrousWaveletTransform.Decompose(field, Size, Size, scales);
        var unit = new double[scales];
        for (var j = 0; j < scales; j++)
        {
            var detail = decomposition.Detail(j);
            var margin = Math.Min(Size / 4, 2 << j);
            var (sum, count) = (0.0, 0);
            for (var y = margin; y < Size - margin; y++)
            {
                for (var x = margin; x < Size - margin; x++)
                {
                    sum += (double)detail[(y * Size) + x] * detail[(y * Size) + x];
                    count++;
                }
            }
            unit[j] = Math.Sqrt(sum / count);
        }
        return [.. noise.Select(sigma => unit.Select(u => u * sigma).ToArray())];
    }
}

/// <summary>
/// Composes a synthetic crowded-cluster scene from a real master's measurements (docs/plans/star-remover-training.md): a
/// King-profile cluster and a uniform field of resolved stars whose brightness and colour are drawn from the master's own
/// catalogue (<see cref="InjectionPopulation.DrawBrightness"/>), rendered as the injector renders (<see cref="StarInjection"/>)
/// through one of three optics (<see cref="ClusterOptics"/>); under them, the sky, the stars too faint to find (each drawn as a
/// star, their smooth expected light the starless truth's glow) and optionally the plate's nebula
/// (<see cref="SyntheticBackground"/>); over all, the master's noise in its shape. Every random draw of the sky comes from the
/// spec's seed in a fixed order, so a rotation or another optics re-renders the same stars.
/// </summary>
public static class SyntheticClusterScene
{
    /// <summary>A star is rendered out to where its light falls below this many noise sigma.</summary>
    public const double FloorSigma = 0.02;

    /// <summary>The faintest unresolved star's green peak, in green's noise sigma. With a slope near 1 the light is spread evenly
    /// over each factor of brightness, so stars under a tenth of the noise would add almost nothing to the glow but nearly all
    /// the count. The brightest is the catalogue's own faint end (<see cref="PoolFaintQuantile"/>): in a crowded field every
    /// star below it went uncatalogued, and without them the real field's local noise was twice the synthetic one's (the
    /// plate builder's per-star sigma 1.7e-4 against 8.0e-5 on the LMC field), so every synthetic star read twice the
    /// significance.</summary>
    public const double GlowMinSigma = 0.1;

    /// <summary>The quantile of the pool's green amplitudes taken as the catalogue's faint end.</summary>
    public const double PoolFaintQuantile = 0.05;

    /// <summary>An unresolved star at or above this green peak (noise sigma) is rendered as a point source through the optics;
    /// below it, deposited and blurred to the channel's FWHM.</summary>
    public const double GlowPointSigma = 1.0;

    /// <summary>An unresolved point star is rendered out to where its light falls below this many noise sigma, far below
    /// <see cref="FloorSigma"/>: at that floor a 1 sigma star's Moffat lost 7 % of its light past the edge, and the
    /// unresolved stars fell 2 % short of the smooth surface that stands for them.</summary>
    public const double GlowFloorSigma = 1e-4;

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
        // The groups: each centred where a cluster star would be, its half-light radius log-uniform over the range (its core and
        // tidal radii from that at the groups' concentration), its resolved stars uniform and its unresolved light log-uniform.
        var halfLightOverCore = KingHalfLightOverCore(SubClusterConcentration);
        var groups = new KingDisc[Math.Max(0, spec.SubClusters)];
        var groupStars = new int[groups.Length];
        var groupGlow = new double[groups.Length];
        static double LogUniform(Random rng, double min, double max)
        {
            var lo = Math.Log(Math.Max(1e-12, min));
            return Math.Exp(lo + (rng.NextDouble() * (Math.Log(Math.Max(Math.Max(1e-12, min), max)) - lo)));
        }
        for (var k = 0; k < groups.Length; k++)
        {
            var (gx, gy) = KingPosition(rng, spec.CoreRadiusPx, spec.TidalRadiusPx);
            var rc = LogUniform(rng, spec.SubClusterHalfLightMinPx, spec.SubClusterHalfLightMaxPx) / halfLightOverCore;
            groups[k] = new KingDisc(gx, gy, rc, SubClusterConcentration * rc);
            groupStars[k] = rng.Next(Math.Max(0, spec.SubClusterStarsMin), Math.Max(spec.SubClusterStarsMin, spec.SubClusterStarsMax) + 1);
            groupGlow[k] = LogUniform(rng, spec.SubClusterGlowMin, spec.SubClusterGlowMax);
        }
        (double X, double Y) GroupPosition(int k)
        {
            var (dx, dy) = KingPosition(rng, groups[k].Rc, groups[k].Rt);
            return (groups[k].X + dx, groups[k].Y + dy);
        }
        (double X, double Y) ClusterPosition() => KingPosition(rng, spec.CoreRadiusPx, spec.TidalRadiusPx);
        // Resolved stars keep MinSeparationPx apart (a hash of cells that wide): a draw too close to one already placed is
        // drawn again, up to PlacementTries times, then placed where it fell.
        var cell = Math.Max(1.0, spec.MinSeparationPx);
        var placedCells = new Dictionary<(int, int), List<(double X, double Y)>>();
        bool TooClose(double x, double y)
        {
            if (spec.MinSeparationPx <= 0)
            {
                return false;
            }
            var (cx, cy) = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (placedCells.TryGetValue((cx + dx, cy + dy), out var list)
                        && list.Any(p => ((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)) < spec.MinSeparationPx * spec.MinSeparationPx))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        (double X, double Y) Spaced(Func<(double X, double Y)> draw)
        {
            var p = draw();
            for (var t = 0; t < InjectionPopulation.PlacementTries && TooClose(p.X, p.Y); t++)
            {
                p = draw();
            }
            var key = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));
            if (!placedCells.TryGetValue(key, out var list))
            {
                list = [];
                placedCells[key] = list;
            }
            list.Add(p);
            return p;
        }
        for (var i = 0; i < spec.ClusterStars; i++)
        {
            var (x, y) = Spaced(ClusterPosition);
            var (a, clips) = population.DrawBrightness(rng, saturated: false);
            resolved.Add((x, y, Recolour(a), clips, false, true));
        }
        for (var k = 0; k < groups.Length; k++)
        {
            for (var i = 0; i < groupStars[k]; i++)
            {
                var (x, y) = Spaced(() => GroupPosition(k));
                var (a, clips) = population.DrawBrightness(rng, saturated: false);
                resolved.Add((x, y, Recolour(a), clips, false, true));
            }
        }
        for (var i = 0; i < fieldCount; i++)
        {
            var (x, y) = Spaced(() => DiscPosition(rng, reach));
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

        // The unresolved stars: a truncated power law in green peak below detection, each a real star's colour. The cluster's
        // and the field's are placed as their resolved stars are, until their light is GlowShare of all starlight (the
        // resolved stars' read off green's Moffat), both counted inside the scene's inscribed circle, which every rotation
        // shares, so the draw does not depend on it; then each group's, placed by its King profile, until its light is its own.
        var greenProfile = population.ProfilesAt(spec.FrameX + centre, spec.FrameY + centre, StarProfileFamily.Moffat)[g];
        var unitFlux = MoffatFlux(greenProfile);
        var inscribed = size / 2.0;
        bool InCircle(double x, double y) => (x * x) + (y * y) <= inscribed * inscribed;
        var resolvedFlux = resolved.Where(s => InCircle(s.X, s.Y)).Sum(s => s.A[g]) * unitFlux;
        var glowTarget = spec.GlowShare is > 0 and < 1 ? resolvedFlux * spec.GlowShare / (1 - spec.GlowShare) : 0.0;
        // Each unresolved star is its place, its green peak, the colour it was drawn with (a pool star's amplitudes, shared,
        // not copied) and its population (0 the field, 1 the cluster, k + 2 group k): tens of millions can be needed.
        var glow = new List<(float X, float Y, float Peak, ImmutableArray<double> Colour, short Population)>();
        var glowInCircle = 0.0;
        var noiseG = source.Noise[g];
        var lo = GlowMinSigma * noiseG;
        var hi = Math.Max(2 * lo, PoolQuantile(population, g, PoolFaintQuantile, spec.Seed));
        while (glowInCircle < glowTarget && glow.Count < 40_000_000)
        {
            var inCluster = rng.NextDouble() < clusterShare;
            var (x, y) = inCluster ? ClusterPosition() : DiscPosition(rng, reach);
            var peak = TruncatedPowerLaw(rng, lo, hi, spec.GlowSlope);
            glow.Add(((float)x, (float)y, (float)peak, colours[rng.Next(colours.Length)], (short)(inCluster ? 1 : 0)));
            if (InCircle(x, y))
            {
                glowInCircle += peak * unitFlux;
            }
        }
        for (var k = 0; k < groups.Length; k++)
        {
            for (var light = 0.0; light < groupGlow[k];)
            {
                var (x, y) = GroupPosition(k);
                var peak = TruncatedPowerLaw(rng, lo, hi, spec.GlowSlope);
                glow.Add(((float)x, (float)y, (float)peak, colours[rng.Next(colours.Length)], (short)(k + 2)));
                light += peak * unitFlux;
            }
        }

        // The nebula: the source's own generator (built from the plate's large scales, SyntheticClusterSource.Background), its
        // noise-free draw wider than the scene, turned with the sky, from a place whose whole tile lies inside the frame: past
        // the frame's edge the background repeats its edge pixels, which drew a straight seam down the nebula. It is the draw
        // less its plate's own sky, with no floor: clipped at the draw's own 5th percentile, every low region became a flat black
        // patch with a hard edge, and a real dust lane has no flat floor (the owner, 2026-10-20). Below the plate's sky it dips
        // as the plate does.
        // With NebulaKnots, the generator's own knots are drawn into it by its own rules (nebula knots, a compact one held
        // faint, as many per area as the exporter's cells hold): fine structure that is not the PSF, which the starless truth
        // keeps, where a round texture finer than 16 px made quasi-stars (the owner, 2026-10-20).
        float[][]? nebula = null;
        var knots = ImmutableArray<SyntheticBackground.Knot>.Empty;
        var wide = NebulaTilePx(size);
        var nebulaAt = (X: InsideFrame(spec.NebulaX, wide, source.FrameWidth), Y: InsideFrame(spec.NebulaY, wide, source.FrameHeight));
        if (spec.NebulaStrength > 0 && source.Background is { } background)
        {
            // The texture's energy matched over the part of the tile the scene shows unturned, the region its strength was read on.
            var drawn = background.Cell(nebulaAt.X - (wide / 2), nebulaAt.Y - (wide / 2), wide, source.Noise, new Random(spec.Seed ^ 0x5bd1e995),
                out var tileKnots, withKnots: spec.NebulaKnots, energyInset: (wide - size) / 2, knotsByArea: true);
            knots = KnotsInScene(tileKnots, wide, size, spec.RotationDeg, spec.NebulaStrength);
            var nebulaSky = source.NebulaSky ?? source.Sky;
            nebula = new float[channels][];
            for (var c = 0; c < channels; c++)
            {
                var turned = Rotate(drawn[c], wide, size, spec.RotationDeg);
                for (var i = 0; i < n; i++)
                {
                    turned[i] = (float)((turned[i] - nebulaSky[c]) * spec.NebulaStrength);
                }
                nebula[c] = turned;
            }
        }

        // A star's light per channel is the master's: its amplitudes were fitted with each channel's own profile, so the
        // measured profiles set the flux whatever the optics draw it with (plain optics, green's shape in every channel, must
        // change the shape and never the colour).
        var midX = spec.FrameX + centre;
        var midY = spec.FrameY + centre;
        var midProfiles = ProfilesFor(population, spec.Optics, midX, midY, channels);
        var measuredFlux = ProfilesFor(population, ClusterOptics.Measured, midX, midY, channels).Select(MoffatFlux).ToArray();
        var keepFlux = midProfiles.Select((p, c) => measuredFlux[c] / MoffatFlux(p)).ToArray();

        // Which unresolved populations the starless truth holds as smooth light: those whose half-light radius reaches
        // SmoothMinHalfLightPx (the cluster's King profile when it is that broad, and the field, which is uniform). A
        // smaller one is a compact cluster: a disc a few PSF widths across that the eye reads as a star, so its whole light
        // is drawn as stars and stays in the stars truth.
        // Populations: 0 the field, 1 the cluster's King profile, k + 2 group k.
        var populations = groups.Length + 2;
        var mainKing = new KingDisc(0, 0, spec.CoreRadiusPx, spec.TidalRadiusPx);
        var smooth = new bool[populations];
        smooth[0] = true;
        smooth[1] = KingHalfLightOverCore(spec.TidalRadiusPx / Math.Max(1e-9, spec.CoreRadiusPx)) * spec.CoreRadiusPx >= SmoothMinHalfLightPx;
        for (var k = 0; k < groups.Length; k++)
        {
            smooth[k + 2] = groups[k].Rc * halfLightOverCore >= SmoothMinHalfLightPx;
        }

        // The unresolved stars, each drawn as a star at its channel's centre: deposited and blurred to the channel's FWHM, or
        // from GlowPointSigma up rendered as the channel's Moffat (its halo far under the noise) out to GlowFloorSigma, so
        // each carries its whole light; those the starless holds as smooth light apart from the rest. That light per
        // population and channel, wherever it fell, is what the smooth surface spreads.
        var unresolved = new float[channels][];
        var compactUnresolved = new float[channels][];
        var glowFlux = new double[channels];
        var glowTotal = new double[populations, channels];
        var pointPeak = GlowPointSigma * noiseG;
        var pointProfiles = spec.Optics == ClusterOptics.Plain ? midProfiles : population.ProfilesAt(midX, midY, StarProfileFamily.Moffat);
        var pointKeep = pointProfiles.Select((p, c) => measuredFlux[c] / MoffatFlux(p)).ToArray();
        var glowSubs = new Random(spec.Seed ^ 0x7f4a7c15);
        var sigmaBlur = midProfiles.Select(p => (float)(p.FwhmPx / 2.3548)).ToArray();
        for (var c = 0; c < channels; c++)
        {
            var deposit = new[] { new float[n], new float[n] };
            var points = new[] { new List<InjectedStar>(), new List<InjectedStar>() };
            foreach (var (x, y, peak, colour, which) in glow)
            {
                var cg = colour[g] > 0 ? colour[g] : colour.Max();
                var amplitude = cg > 0 ? peak * colour[c] / cg : peak;
                glowTotal[which, c] += amplitude * measuredFlux[c];
                var layer = smooth[which] ? 0 : 1;
                var (px, py) = ChannelCentre(spec, source, ToScene(x, y), c);
                if (px < -Margin || py < -Margin || px >= size + Margin || py >= size + Margin)
                {
                    continue;
                }
                if (peak >= pointPeak)
                {
                    points[layer].Add(new InjectedStar(px, py, [amplitude * pointKeep[c]], [pointProfiles[c]], false, ImmutableArray<double>.Empty));
                }
                else
                {
                    DepositBilinear(deposit[layer], size, px, py, amplitude * measuredFlux[c]);
                }
            }
            var planes = new float[2][];
            for (var layer = 0; layer < 2; layer++)
            {
                var plane = Image.SeparableGaussianBlur(deposit[layer], size, size, sigmaBlur[c]);
                if (points[layer].Count > 0)
                {
                    plane = StarInjection.Render([plane], size, size, null, points[layer], [GlowFloorSigma * source.Noise[c]], glowSubs).Planes[0];
                }
                planes[layer] = plane;
            }
            unresolved[c] = planes[0];
            compactUnresolved[c] = planes[1];
            foreach (var v in planes[0])
            {
                glowFlux[c] += v;
            }
        }

        // The starless truth: the sky, the nebula and the smooth populations' expected light, never their points: per
        // population, its density (the field's disc, the cluster's or a group's King profile, each one over the plane) times
        // its whole light, blurred as its faint stars are, through each channel's Gaussian.
        var (mainEdge, mainNorm) = (mainKing.Edge, mainKing.Norm);
        var fieldDensity = reach > 0 ? 1.0 / (Math.PI * reach * reach) : 0.0;
        var densities = new Func<double, double, double>[populations];
        densities[0] = (sx, sy) => (sx * sx) + (sy * sy) <= reach * reach ? fieldDensity : 0.0;
        densities[1] = (sx, sy) => mainKing.Density(sx, sy, mainEdge, mainNorm);
        for (var k = 0; k < groups.Length; k++)
        {
            var (disc, edge, norm) = (groups[k], groups[k].Edge, groups[k].Norm);
            densities[k + 2] = (sx, sy) => disc.Density(sx, sy, edge, norm);
        }
        var opticalCentre = OpticalCentre(spec, source);
        var side = size + (2 * SurfacePadPx);
        var sharedGrids = new float[populations][];
        var starless = new float[channels][];
        var surfaceFlux = new double[channels];
        for (var c = 0; c < channels; c++)
        {
            var lateral = LateralScale(spec, c);
            var light = new float[side * side];
            for (var p = 0; p < populations; p++)
            {
                if (!smooth[p])
                {
                    continue;
                }
                var grid = lateral == 0.0
                    ? sharedGrids[p] ??= DensityGrid(densities[p], size, centre, sin, cos, opticalCentre, 0.0)
                    : DensityGrid(densities[p], size, centre, sin, cos, opticalCentre, lateral);
                var total = glowTotal[p, c];
                for (var i = 0; i < light.Length; i++)
                {
                    light[i] += (float)(grid[i] * total);
                }
            }
            var blurred = Image.SeparableGaussianBlur(light, side, side, sigmaBlur[c]);
            var surface = new float[n];
            var deviation = new float[n];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var expected = blurred[((y + SurfacePadPx) * side) + x + SurfacePadPx];
                    var i = (y * size) + x;
                    surface[i] = expected;
                    deviation[i] = unresolved[c][i] - expected;
                    surfaceFlux[c] += expected;
                }
            }
            // The smooth populations' own grain at the scales no star is read at: their draw's departure from its
            // expectation, smoothed through the starlet's first GrainFromScale scales (what is left varies over 2^GrainFromScale
            // px and up). A mottled haze, as the real glow is; the finer grain stays in the stars truth.
            var grain = ATrousWaveletTransform.Decompose(deviation, size, size, GrainFromScale).Residual;
            var plane = new float[n];
            for (var i = 0; i < n; i++)
            {
                plane[i] = (float)(source.Sky[c] + surface[i] + grain[i] + (nebula?[c][i] ?? 0f));
            }
            starless[c] = plane;
        }
        var groupsRendered = ImmutableArray.CreateRange(Enumerable.Range(0, groups.Length).Select(k =>
        {
            var (x, y) = ToScene(groups[k].X, groups[k].Y);
            return new ClusterGroup(x, y, groups[k].Rc * halfLightOverCore, groupStars[k], glowTotal[k + 2, g], smooth[k + 2]);
        }));

        // The resolved stars, rendered channel by channel onto the sky, the nebula and the unresolved stars (so a saturated star
        // clips against the light under it, as the sensor clips the sum), each channel at its own centre and profile.
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
        var profileGrid = new ImmutableArray<StarProfile>[nodes * nodes];
        for (var j = 0; j < nodes; j++)
        {
            for (var i = 0; i < nodes; i++)
            {
                profileGrid[(j * nodes) + i] = ProfilesFor(population, spec.Optics, spec.FrameX + (i * Node), spec.FrameY + (j * Node), channels);
            }
        }
        ImmutableArray<StarProfile> ProfilesNear((double X, double Y) at)
            => profileGrid[(Math.Clamp((int)Math.Round(at.Y / Node), 0, nodes - 1) * nodes) + Math.Clamp((int)Math.Round(at.X / Node), 0, nodes - 1)];
        for (var c = 0; c < channels; c++)
        {
            var injected = new InjectedStar[placed.Length];
            for (var k = 0; k < placed.Length; k++)
            {
                var (s, at) = placed[k];
                var (px, py) = ChannelCentre(spec, source, at, c);
                var profile = ProfilesNear(at)[c];
                injected[k] = new InjectedStar(px, py, [s.A[c] * keepFlux[c]], [profile], s.Saturated, s.Saturated ? [s.Clips[c]] : ImmutableArray<double>.Empty);
            }
            var under = new float[n];
            for (var i = 0; i < n; i++)
            {
                under[i] = (float)source.Sky[c] + (nebula?[c][i] ?? 0f) + unresolved[c][i] + compactUnresolved[c][i];
            }
            var render = StarInjection.Render([under], size, size, null, injected, [FloorSigma * source.Noise[c]], subs);
            composite[c] = render.Planes[0];
            var only = new float[n];
            for (var i = 0; i < n; i++)
            {
                only[i] = composite[c][i] - starless[c][i];
                starFlux[c] += composite[c][i] - under[i];
            }
            stars[c] = only;
        }

        // The master's noise, by the exporter's own rule (SyntheticClusterSource.NoiseCalibration: its shape, Warped at its
        // resampling width, its pixel level at the sky, its variance growing with the light). The noisy truth is the starless
        // truth plus one draw at its own level, and the composite is that noisy truth plus the stars plus the stars' own
        // shot noise, a second draw of the same shape at sqrt(sigma(composite)^2 - sigma(starless)^2): an injected draw over
        // its plate (synth4), so the composite less the noisy truth is the stars and their own noise, and a remover trained
        // toward the noisy truth keeps the frame's noise as synth4's does.
        var noise = new Random(spec.Seed ^ 0x165667b1);
        var starNoise = new Random(spec.Seed ^ 0x27d4eb2f);
        var starlessNoisy = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            var field = NoiseField.Warped(size, size, NoiseRealisations, noise, source.ResampleSigma);
            var starField = NoiseField.Warped(size, size, NoiseRealisations, starNoise, source.ResampleSigma);
            starlessNoisy[c] = AddNoise(starless[c], composite[c], source.NoiseCalibration(c), field, starField);
        }
        return new SyntheticClusterRender(stars, starless, composite, unresolved, truth.ToImmutable(), glow.Count, starFlux, glowFlux, surfaceFlux,
            nebulaAt, groupsRendered, starlessNoisy, knots);
    }

    /// <summary>
    /// One channel's noise: returns the noisy starless truth, <paramref name="starless"/> plus <paramref name="field"/> at its
    /// own level, and turns <paramref name="composite"/> (noise-free, the starless truth plus the stars) into that noisy truth
    /// plus the stars plus their own shot noise, <paramref name="starField"/> at
    /// <c>sqrt(sigma(composite)^2 - sigma(starless)^2)</c>, in place. Where a pixel holds no star the composite IS the noisy
    /// truth, and elsewhere it differs by the stars and their own noise alone, as an injected draw differs from its plate.
    /// </summary>
    internal static float[] AddNoise(float[] starless, float[] composite, LinearDegradation.NoiseCalibration calibration,
        ReadOnlySpan<float> field, ReadOnlySpan<float> starField)
    {
        var noisy = new float[starless.Length];
        for (var i = 0; i < noisy.Length; i++)
        {
            var without = starless[i];
            var stars = composite[i] - without;
            var sigmaWithout = calibration.SigmaAt(without, 1.0);
            var sigmaWith = calibration.SigmaAt(composite[i], 1.0);
            noisy[i] = without + (float)(field[i] * sigmaWithout);
            var extra = Math.Sqrt(Math.Max(0.0, (sigmaWith * sigmaWith) - (sigmaWithout * sigmaWithout)));
            composite[i] = noisy[i] + stars + (float)(starField[i] * extra);
        }
        return noisy;
    }

    // A quantile of the pool's green amplitudes, read off ColourDraws draws.
    private static double PoolQuantile(InjectionPopulation population, int green, double quantile, int seed)
    {
        var rng = new Random(seed ^ 0x510e527f);
        var amplitudes = Enumerable.Range(0, ColourDraws).Select(_ => population.DrawBrightness(rng, saturated: false).Amplitudes[green])
            .Where(static a => a > 0).OrderBy(static a => a).ToArray();
        return amplitudes.Length == 0 ? 0.0 : amplitudes[Math.Clamp((int)(quantile * (amplitudes.Length - 1)), 0, amplitudes.Length - 1)];
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
        var k = LateralScale(spec, channel);
        if (k == 0.0)
        {
            return at;
        }
        var (ox, oy) = OpticalCentre(spec, source);
        return (ox + ((at.X - ox) * (1 + k)), oy + ((at.Y - oy) * (1 + k)));
    }

    // A channel's radial scale under lateral colour (0 without it).
    private static double LateralScale(SyntheticClusterSpec spec, int channel)
        => spec.Optics != ClusterOptics.Lateral || spec.LateralPpm.IsDefaultOrEmpty ? 0.0 : 1e-6 * spec.LateralPpm[Math.Min(channel, spec.LateralPpm.Length - 1)];

    // The frame's centre in scene pixels: what lateral colour magnifies about.
    private static (double X, double Y) OpticalCentre(SyntheticClusterSpec spec, SyntheticClusterSource source)
        => (((source.FrameWidth - 1) / 2.0) - spec.FrameX, ((source.FrameHeight - 1) / 2.0) - spec.FrameY);

    /// <summary>How far inside the master's frame the nebula's whole tile is drawn from, pixels.</summary>
    public const int NebulaEdgeMarginPx = 64;

    // A tile centre along one axis such that a tile `wide` px across lies inside a frame `frame` px long with
    // NebulaEdgeMarginPx to spare (the frame's middle when it cannot).
    private static int InsideFrame(int centre, int wide, int frame)
    {
        var lo = (wide / 2) + NebulaEdgeMarginPx;
        var hi = frame - NebulaEdgeMarginPx - wide + (wide / 2);
        return lo <= hi ? Math.Clamp(centre, lo, hi) : frame / 2;
    }

    /// <summary>The groups' concentration, tidal over core radius. A compact LMC cluster's measured half-light radius sets
    /// the rest; at 3 the half-light radius is 0.89 core radii, 0.30 of the tidal radius.</summary>
    public const double SubClusterConcentration = 3.0;

    // The smooth surface is computed this far past the scene on every side, so the blur sees the light just outside it.
    private const int SurfacePadPx = 16;

    /// <summary>The share of a King (1962) profile's projected light within <paramref name="r"/> of its centre, for core
    /// radius <paramref name="rc"/> and tidal radius <paramref name="rt"/>: the closed form of the surface density's integral.</summary>
    public static double KingLightWithin(double r, double rc, double rt)
    {
        var edge = 1.0 / Math.Sqrt(1 + (rt / rc * (rt / rc)));
        return KingIntegral(Math.Clamp(r, 0, rt), rc, edge) / KingIntegral(rt, rc, edge);
    }

    // The integral of (1 / sqrt(1 + (r/rc)^2) - edge)^2 over the disc of radius r, over pi rc^2: with u = 1 + (r/rc)^2,
    // ln u - 4 edge (sqrt u - 1) + edge^2 (u - 1).
    private static double KingIntegral(double r, double rc, double edge)
    {
        var u = 1 + (r / rc * (r / rc));
        return Math.Log(u) - (4 * edge * (Math.Sqrt(u) - 1)) + (edge * edge * (u - 1));
    }

    /// <summary>A King profile's half-light radius over its core radius, at a concentration of tidal over core radius.</summary>
    public static double KingHalfLightOverCore(double concentration)
    {
        var (lo, hi) = (0.0, concentration);
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            (lo, hi) = KingLightWithin(mid, 1.0, concentration) < 0.5 ? (mid, hi) : (lo, mid);
        }
        return (lo + hi) / 2;
    }

    // A King profile placed on the sky: its centre, core and tidal radii, and its surface density normalised to one over
    // the plane (pi rc^2 times the closed-form integral out to the tidal radius).
    private readonly record struct KingDisc(double X, double Y, double Rc, double Rt)
    {
        public double Edge => 1.0 / Math.Sqrt(1 + (Rt / Rc * (Rt / Rc)));

        public double Norm => Math.PI * Rc * Rc * KingIntegral(Rt, Rc, Edge);

        // The density at a sky point, given this disc's Edge and Norm (read once by the caller).
        public double Density(double sx, double sy, double edge, double norm)
        {
            var r2 = ((sx - X) * (sx - X)) + ((sy - Y) * (sy - Y));
            if (r2 > Rt * Rt)
            {
                return 0.0;
            }
            var v = (1.0 / Math.Sqrt(1 + (r2 / (Rc * Rc)))) - edge;
            return v * v / norm;
        }
    }

    // A population's density (per unit area on the sky, one over the plane) at each pixel of a grid SurfacePadPx wider than
    // the scene on every side: each pixel the mean of 3 x 3 samples, a channel's place taken back through its lateral
    // magnification (and the area with it), then back through the sky's rotation.
    private static float[] DensityGrid(Func<double, double, double> density, int size, double centre, double sin, double cos,
        (double X, double Y) opticalCentre, double lateral)
    {
        var side = size + (2 * SurfacePadPx);
        var grid = new float[side * side];
        var shrink = 1.0 / (1 + lateral);
        var area = shrink * shrink;
        ReadOnlySpan<double> offsets = [-1.0 / 3.0, 0.0, 1.0 / 3.0];
        for (var j = 0; j < side; j++)
        {
            for (var i = 0; i < side; i++)
            {
                var sum = 0.0;
                foreach (var oy in offsets)
                {
                    foreach (var ox in offsets)
                    {
                        var qx = opticalCentre.X + ((i - SurfacePadPx + ox - opticalCentre.X) * shrink) - centre;
                        var qy = opticalCentre.Y + ((j - SurfacePadPx + oy - opticalCentre.Y) * shrink) - centre;
                        sum += density((cos * qx) + (sin * qy), (-sin * qx) + (cos * qy));
                    }
                }
                grid[(j * side) + i] = (float)(sum / 9.0 * area);
            }
        }
        return grid;
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

    /// <summary>
    /// A tile's knots where the scene shows them: the tile turned by <paramref name="degrees"/> about both centres, as
    /// <see cref="Rotate"/> turns its pixels (scene = c1 + M^T (tile - c0)), each knot's axis turned with it and its peak times
    /// <paramref name="strength"/>; a knot whose centre falls more than its own reach outside the scene is left out.
    /// </summary>
    internal static ImmutableArray<SyntheticBackground.Knot> KnotsInScene(ImmutableArray<SyntheticBackground.Knot> tileKnots, int wideSize, int size,
        double degrees, double strength)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180.0);
        var c0 = (wideSize - 1) / 2.0;
        var c1 = (size - 1) / 2.0;
        var inScene = ImmutableArray.CreateBuilder<SyntheticBackground.Knot>();
        foreach (var k in tileKnots)
        {
            var (u, v) = (k.X - c0, k.Y - c0);
            var (x, y) = (c1 + (cos * u) - (sin * v), c1 + (sin * u) + (cos * v));
            var reach = 4 * k.SigmaMajorPx;
            if (x < -reach || y < -reach || x >= size + reach || y >= size + reach)
            {
                continue;
            }
            inScene.Add(k with { X = x, Y = y, AngleRad = k.AngleRad + (degrees * Math.PI / 180.0), Peak = [.. k.Peak.Select(p => p * strength)] });
        }
        return inScene.ToImmutable();
    }

    // The centre `size` px of a `wide` px plane turned by `degrees` about its centre, bilinear.
    internal static float[] Rotate(float[] wide, int wideSize, int size, double degrees)
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

    /// <summary>
    /// The least half-light radius, pixels, at which an unresolved population's light is the starless truth's smooth light (10
    /// PSF widths on the LMC master). The owner's rule (2026-10-20): the starless truth holds nothing the eye reads as a star,
    /// whatever its measured width, so a compact cluster (the LMC's own are 4.1 to 11.8 px) is drawn as stars and stays in
    /// the stars truth whole; only the broad light, the field's and a cluster's tens to hundreds of pixels across, is
    /// starless.
    /// </summary>
    public const double SmoothMinHalfLightPx = 16.0;

    /// <summary>
    /// The starless truth keeps the smooth populations' own grain from 2^this px up (16 px): their draw's departure from its
    /// expectation, smoothed past the scales a star is read at, so the glow is a mottled haze, as a real one is, and never an
    /// airbrushed disc (the owner, 2026-10-20). The finer grain is in the stars truth.
    /// </summary>
    public const int GrainFromScale = 4;

    /// <summary>The nebula generator's first kept starlet scale (2^6 = 64 px): the plate's structure from there up
    /// (<see cref="SyntheticBackground.LargeScale"/>). The Tarantula plate's local RMS is flat at 0.3 noise from 8 to 32 px,
    /// its faint stars' confusion and its builder's fills, the cells the owner saw at 20 to 40 px; the nebula's own spectrum
    /// rises from 64 px (0.37 there, 1.08 at 128 px).</summary>
    public const int NebulaKeepFromScale = 6;

    /// <summary>The finest scale the nebula's texture is drawn at by default (2^4 = 16 px): finer, its knots are discs a few PSF
    /// widths across, read as stars (gate 1 of the truth), and on the LMC field the plate's power there is its faint stars'
    /// confusion and its builder's residue, not a nebula's.</summary>
    public const int NebulaTextureFromScale = 4;

    /// <summary>
    /// Where in the master's frame a scene's nebula is drawn from: the <see cref="SyntheticClusterSpec.Size"/> px square the
    /// scene shows of it (the middle of the wider tile drawn for its rotation), its tile inside the frame by
    /// <see cref="NebulaEdgeMarginPx"/>.
    /// </summary>
    public static (int X0, int Y0, int Size) NebulaRegion(SyntheticClusterSpec spec, int frameWidth, int frameHeight)
    {
        var wide = NebulaTilePx(spec.Size);
        var (x, y) = (InsideFrame(spec.NebulaX, wide, frameWidth), InsideFrame(spec.NebulaY, wide, frameHeight));
        return (x - (spec.Size / 2), y - (spec.Size / 2), spec.Size);
    }

    // The nebula tile drawn for a scene: wide enough that every rotation covers the scene.
    private static int NebulaTilePx(int size) => (int)Math.Ceiling(size * Math.Sqrt(2.0)) + 4;

    /// <summary>The nebula's steer: R2e's (0.5, 2, 0.8), read off the kept scales only.</summary>
    public static SyntheticBackground.Steering NebulaSteer => new(0.5, 2.0, 0.8);
}
