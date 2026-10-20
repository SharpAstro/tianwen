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
public sealed record SyntheticClusterRun(
    string MasterPath, string PlatePath, string OutDir, SyntheticClusterSpec Base,
    ImmutableArray<ClusterOptics> Optics, ImmutableArray<double> Nebulae, ImmutableArray<double> Rotations)
{
    /// <summary>One written scene.</summary>
    public sealed record Written(string Name, string CompositePath, int Stars, int GlowStars, double[] StarFlux, double[] GlowFlux);

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
        var absent = plate.AbsentPixels();
        var psf = field.Channels.Select(static c => (c.Fwhm, c.Beta)).ToArray();
        var population = InjectionPopulation.Build(catalogue, master, plate, 1.0, psf, absent, field);
        var background = SyntheticBackground.Build(plate, absent, field.Luminance.Fwhm, nebulaKnots: true);
        var (sky, noise, resample, ratio) = SyntheticClusterSource.MeasureSkyAndNoise(plate, absent);
        var source = new SyntheticClusterSource(population, background, sky, noise, resample, master.Width, master.Height);
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{population.AmplitudePool} stars to draw from ({population.SaturatedPool} saturated); sky {Join(sky, "G4")}, noise {Join(noise, "G3")}; " +
            $"noise band ratio {ratio:F3}, matched by a resampling width of {resample:F2} px; profiles {string.Join(" / ", field.Channels.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Fwhm:F2} px b{c.Beta:F2}")))}");
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
                    await File.WriteAllTextAsync(Path.Combine(OutDir, name + "_truth.csv"), TruthCsv(render.Truth), cancellationToken);
                    var w = new Written(name, composite, render.Truth.Length, render.GlowStars, render.StarFlux, render.GlowFlux);
                    written.Add(w);
                    progress?.Report(string.Create(CultureInfo.InvariantCulture,
                        $"{name}: {w.Stars} resolved stars, {w.GlowStars} unresolved; star light {Join(w.StarFlux, "G4")}, glow {Join(w.GlowFlux, "G4")}"));
                }
            }
        }
        return (written.ToImmutable(), line);
    }

    private static string Join(double[] values, string format) => string.Join(" / ", values.Select(v => v.ToString(format, CultureInfo.InvariantCulture)));

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
