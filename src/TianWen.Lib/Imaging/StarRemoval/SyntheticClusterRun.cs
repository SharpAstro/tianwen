using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// <c>tianwen dataset synthetic-cluster</c>: every variant of a synthetic cluster scene drawn from one master
/// (<see cref="SyntheticClusterScene"/>), the sky fixed by the seed and each of <paramref name="Optics"/> x
/// <paramref name="Nebulae"/> x <paramref name="Rotations"/> rendered through it.
/// </summary>
/// <param name="MasterPath">The master the scenes copy (its catalogue's units and its header).</param>
/// <param name="PlatePath">Its starless plate (<c>tianwen dataset starless-plates</c>), with the catalogue and the field
/// profile beside it.</param>
/// <param name="OutDir">Where the scenes go.</param>
/// <param name="Base">The scene the variants share; its rotation, optics and nebula strength are set per variant.</param>
/// <param name="Optics">The optics to render.</param>
/// <param name="Nebulae">The nebula strengths to render.</param>
/// <param name="Rotations">The sky rotations to render, degrees.</param>
/// <param name="PoolMargin">With a margin of 0 or more, the stars are drawn from the catalogue's stars inside the scene's
/// window in the frame grown by that many pixels, so the scene takes its own part of the field's brightness and colour (the
/// LMC's young clusters are bluer than the frame's average); below 0, from the whole frame.</param>
/// <param name="NebulaPlatePath">The plate the nebula's large scales are read from, when not <paramref name="PlatePath"/>: a
/// plate cleaned more thoroughly (the R0 builder's brush) can drive it once it exists.</param>
/// <param name="NebulaTextureFromScale">The finest starlet scale the nebula's texture is drawn at (2^j px).</param>
/// <param name="NebulaLogNormalWidth">The nebula texture's log-normal width (0 a Gaussian field).</param>
public sealed record SyntheticClusterRun(
    string MasterPath, string PlatePath, string OutDir, SyntheticClusterSpec Base,
    ImmutableArray<ClusterOptics> Optics, ImmutableArray<double> Nebulae, ImmutableArray<double> Rotations, int PoolMargin = -1,
    string? NebulaPlatePath = null, int NebulaTextureFromScale = SyntheticClusterScene.NebulaTextureFromScale, double NebulaLogNormalWidth = 0.0)
{
    /// <summary>One written scene.</summary>
    public sealed record Written(string Name, string CompositePath, int Stars, int GlowStars, double[] StarFlux, double[] GlowFlux, double[] SurfaceFlux);

    /// <summary>Builds the source once and writes every variant, reporting each as it lands.</summary>
    public async Task<(ImmutableArray<Written> Scenes, string SourceLine)> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!Image.TryReadFitsFile(MasterPath, out var master))
        {
            throw new InvalidDataException($"not a readable FITS image: {MasterPath}");
        }
        if (!Image.TryReadFitsFile(PlatePath, out var plate))
        {
            throw new InvalidDataException($"not a readable FITS image: {PlatePath}");
        }
        var platesDir = Path.GetDirectoryName(Path.GetFullPath(PlatePath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(PlatePath);
        stem = stem.EndsWith("_plate", StringComparison.Ordinal) ? stem[..^"_plate".Length] : stem;
        var catalogue = await StarlessCatalogue.ReadAsync(StarlessCatalogue.PathFor(platesDir, stem), cancellationToken);
        var field = await StarlessFieldProfile.ReadAsync(StarlessFieldProfile.PathFor(platesDir, stem), cancellationToken)
            ?? throw new InvalidOperationException($"no field profile beside {PlatePath}; run tianwen dataset starless-plates --profiles-only");
        if (PoolMargin >= 0)
        {
            var (x0, y0) = (Base.FrameX - PoolMargin, Base.FrameY - PoolMargin);
            var (x1, y1) = (Base.FrameX + Base.Size + PoolMargin, Base.FrameY + Base.Size + PoolMargin);
            catalogue = [.. catalogue.Where(s => s.X >= x0 && s.X < x1 && s.Y >= y0 && s.Y < y1)];
        }
        var absent = plate.AbsentPixels();
        var psf = field.Channels.Select(static c => (c.Fwhm, c.Beta)).ToArray();
        var population = InjectionPopulation.Build(catalogue, master, plate, 1.0, psf, absent, field);
        // The nebula is the background generator driven by its plate's large scales alone (SyntheticBackground.LargeScale),
        // which no star's fill reaches; NebulaPlatePath gives it another plate (a cleaner one) to read them from.
        var nebulaPlate = plate;
        var nebulaAbsent = absent;
        if (NebulaPlatePath is { } nebulaPath)
        {
            if (!Image.TryReadFitsFile(nebulaPath, out var other))
            {
                throw new InvalidDataException($"not a readable FITS image: {nebulaPath}");
            }
            (nebulaPlate, nebulaAbsent) = (other, other.AbsentPixels());
        }
        // Its texture's strength per scale is the plate's own at the place the scenes are drawn from, the master's noise in each
        // band taken out in quadrature.
        var (sky, noise, resample, ratio) = SyntheticClusterSource.MeasureSkyAndNoise(plate, absent);
        var noiseBands = SyntheticClusterSource.NoiseBandRms(noise, resample, SyntheticClusterScene.NebulaKeepFromScale);
        var region = SyntheticClusterScene.NebulaRegion(Base, master.Width, master.Height);
        // Its knots, when the spec asks for them, are the generator's own: nebula knots, a compact one held faint.
        var background = SyntheticBackground.Build(nebulaPlate, nebulaAbsent, field.Luminance.Fwhm, brightKnotsWide: true,
            steering: SyntheticClusterScene.NebulaSteer, nebulaKnots: true,
            largeScale: new SyntheticBackground.LargeScale(SyntheticClusterScene.NebulaKeepFromScale, NebulaTextureFromScale,
                region.X0, region.Y0, region.Size, noiseBands, NebulaLogNormalWidth));
        var nebulaSky = NebulaPlatePath is null ? null : SyntheticClusterSource.MeasureSkyAndNoise(nebulaPlate, nebulaAbsent).Sky;
        var source = new SyntheticClusterSource(population, background, sky, noise, resample, master.Width, master.Height, nebulaSky);
        var g = Math.Min(1, sky.Length - 1);
        var texture = string.Join(", ", Enumerable.Range(0, SyntheticClusterScene.NebulaKeepFromScale).Select(j => string.Create(CultureInfo.InvariantCulture,
            $"{1 << j} px {(background.LargeScaleRegionRms.IsDefaultOrEmpty ? 0.0 : background.LargeScaleRegionRms[g][j]) / noise[g]:F3}")));
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{population.AmplitudePool} stars to draw from ({population.SaturatedPool} saturated); sky {Join(sky, "G7")}, noise {Join(noise, "G4")}; " +
            $"noise band ratio {ratio:F3}, matched by a resampling width of {resample:F2} px; profiles {string.Join(" / ", field.Channels.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Fwhm:F2} px b{c.Beta:F2}")))}; " +
            $"nebula from {Path.GetFileName(NebulaPlatePath ?? PlatePath)} at ({region.X0}, {region.Y0}) {region.Size} px, its scales from {1 << SyntheticClusterScene.NebulaKeepFromScale} px up kept, " +
            $"texture (log-normal width {NebulaLogNormalWidth:0.##}) green RMS in the noise {texture}");
        progress?.Report(line);

        Directory.CreateDirectory(OutDir);
        var written = ImmutableArray.CreateBuilder<Written>();
        foreach (var optics in Optics)
        {
            foreach (var nebula in Nebulae)
            {
                foreach (var rotation in Rotations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var spec = Base with { Optics = optics, NebulaStrength = nebula, RotationDeg = rotation };
                    var name = string.Create(CultureInfo.InvariantCulture,
                        $"cluster_s{spec.Seed}_{optics.ToString().ToLowerInvariant()}_neb{nebula:0.##}_rot{rotation:0.#}");
                    var render = SyntheticClusterScene.Render(source, spec);
                    var composite = Path.Combine(OutDir, name + ".fits");
                    Write(render.Composite, spec.Size, master, composite);
                    Write(render.Stars, spec.Size, master, Path.Combine(OutDir, name + "_stars.fits"));
                    Write(render.Starless, spec.Size, master, Path.Combine(OutDir, name + "_starless.fits"));
                    Write(render.StarlessNoisy, spec.Size, master, Path.Combine(OutDir, name + "_starless_noisy.fits"));
                    Write(render.Unresolved, spec.Size, master, Path.Combine(OutDir, name + "_unresolved.fits"));
                    await File.WriteAllTextAsync(Path.Combine(OutDir, name + "_truth.csv"), TruthCsv(render.Truth), cancellationToken);
                    if (spec.NebulaKnots && nebula > 0)
                    {
                        await File.WriteAllTextAsync(Path.Combine(OutDir, name + "_knots.csv"), KnotsCsv(render.Knots), cancellationToken);
                    }
                    var w = new Written(name, composite, render.Truth.Length, render.GlowStars, render.StarFlux, render.GlowFlux, render.SurfaceFlux);
                    written.Add(w);
                    var groups = string.Join(", ", render.Groups.Select(static gr => string.Create(CultureInfo.InvariantCulture,
                        $"({gr.X:F0}, {gr.Y:F0}, {gr.HalfLightPx:F1}, {gr.ResolvedStars}, {gr.UnresolvedLight:F3}, {(gr.Smooth ? "smooth" : "stars")})")));
                    var knotsShown = spec.NebulaKnots && nebula > 0 ? $", {render.Knots.Length} of its knots in the scene" : "";
                    progress?.Report(string.Create(CultureInfo.InvariantCulture,
                        $"{name}: {w.Stars} resolved stars, {w.GlowStars} unresolved; star light {Join(w.StarFlux, "G4")}, unresolved stars {Join(w.GlowFlux, "G4")}, " +
                        $"their smooth surface {Join(w.SurfaceFlux, "G4")}; nebula from ({render.NebulaAt.X}, {render.NebulaAt.Y}){knotsShown}; " +
                        $"groups (x, y, half-light px, resolved stars, unresolved light, where that light is) {groups}"));
                }
            }
        }
        return (written.ToImmutable(), line);
    }

    private static string Join(double[] values, string format) => string.Join(" / ", values.Select(v => v.ToString(format, CultureInfo.InvariantCulture)));

    /// <summary>A scene's knots, one row each: the centre in scene pixels, the Gaussian's major and minor sigma (pixels), the
    /// major axis's angle (degrees) and the peak per channel.</summary>
    internal static string KnotsCsv(ImmutableArray<SyntheticBackground.Knot> knots)
    {
        var sb = new StringBuilder("x,y,sigma_major,sigma_minor,angle_deg,peak_r,peak_g,peak_b\n");
        foreach (var k in knots)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{k.X:F3},{k.Y:F3},{k.SigmaMajorPx:F4},{k.SigmaMinorPx:F4},{k.AngleRad * 180.0 / Math.PI:F2},{string.Join(",", k.Peak.Select(p => p.ToString("G6", CultureInfo.InvariantCulture)))}\n");
        }
        return sb.ToString();
    }

    private static string TruthCsv(ImmutableArray<ClusterTruthStar> truth)
    {
        var sb = new StringBuilder("x,y,amp_r,amp_g,amp_b,saturated,in_cluster\n");
        foreach (var s in truth)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{s.X:F3},{s.Y:F3},{string.Join(",", s.Amplitudes.Select(a => a.ToString("G6", CultureInfo.InvariantCulture)))},{(s.Saturated ? 1 : 0)},{(s.InCluster ? 1 : 0)}\n");
        }
        return sb.ToString();
    }

    private static void Write(float[][] planes, int size, Image like, string path)
    {
        var data = new float[planes.Length][,];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = new float[size, size];
            Buffer.BlockCopy(planes[c], 0, plane, 0, size * size * sizeof(float));
            data[c] = plane;
        }
        new Image(data, BitDepth.Float32, 1f, 0f, 0f, like.ImageMeta).WriteToFitsFile(path);
    }
}
