using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// The one format of a starless plate's catalogue (<c>&lt;stem&gt;_plate.stars.csv</c> beside the plate): every point source
/// the builder found and what it made of it. <c>tianwen dataset starless-plates</c> writes it for the review and for a
/// miss to be looked up; the star injector reads it back for the field's own stars (where they were, how bright, in which
/// colours, which were saturated).
/// </summary>
public static class StarlessCatalogue
{
    /// <summary>The header; the columns after <c>hole_depth</c> joined later and are absent from an older file.</summary>
    public const string Header = "x,y,significance,amplitude,width,sky,sigma,outcome,saturated,inpainted,core_residual,core_bias,second_pass,hole_depth,model,amplitudes,sky_above,texture";

    /// <summary>The catalogue of the plate <c>&lt;stem&gt;_plate.fits</c> in <paramref name="platesDir"/>.</summary>
    public static string PathFor(string platesDir, string stem) => Path.Combine(platesDir, stem + "_plate.stars.csv");

    /// <summary>Writes <paramref name="stars"/>; a star's channel amplitudes go in one field, joined by semicolons.</summary>
    public static async Task WriteAsync(string path, ImmutableArray<FittedStar> stars, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var s in stars)
        {
            var amplitudes = s.ChannelAmplitudes.IsDefaultOrEmpty
                ? ""
                : string.Join(';', s.ChannelAmplitudes.Select(static a => a.ToString("G6", CultureInfo.InvariantCulture)));
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{s.X:F2},{s.Y:F2},{s.Significance:F1},{s.Amplitude:G5},{s.WidthScale:F3},{s.Sky:G5},{s.LocalSigma:G4},{s.Outcome},{(s.Saturated ? 1 : 0)},{(s.Inpainted ? 1 : 0)},{s.CoreResidual:F3},{s.CoreBias:F3},{(s.SecondPass ? 1 : 0)},{s.HoleDepth:F2},{s.Model},{amplitudes},{s.SkyAbove:F1},{s.Texture:F3}\n"));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken);
    }

    /// <summary>
    /// Reads a catalogue back. A file from before the model and amplitude columns reads with
    /// <see cref="StarFitModel.None"/> and no amplitudes, which the injector cannot draw from; it says so rather than
    /// guessing. One from before the sky columns reads NaN for <see cref="FittedStar.SkyAbove"/> and
    /// <see cref="FittedStar.Texture"/>.
    /// </summary>
    public static async Task<ImmutableArray<FittedStar>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var stars = ImmutableArray.CreateBuilder<FittedStar>(Math.Max(0, lines.Length - 1));
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }
            var f = line.Split(',');
            if (f.Length < 14)
            {
                throw new InvalidDataException($"{path}:{i + 1}: {f.Length} fields, under the 14 a catalogue row has");
            }
            static float F(string v) => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
            var model = f.Length > 14 && Enum.TryParse<StarFitModel>(f[14], out var m) ? m : StarFitModel.None;
            var amplitudes = f.Length > 15 && f[15].Length > 0
                ? f[15].Split(';').Select(F).ToImmutableArray()
                : ImmutableArray<float>.Empty;
            var skyAbove = f.Length > 16 ? F(f[16]) : float.NaN;
            var texture = f.Length > 17 ? F(f[17]) : float.NaN;
            stars.Add(new FittedStar(
                F(f[0]), F(f[1]), F(f[2]), F(f[3]), F(f[4]), F(f[5]), F(f[6]),
                Enum.Parse<StarFitOutcome>(f[7]), f[8] == "1", f[9] == "1", F(f[10]), F(f[11]), f[12] == "1", F(f[13]),
                model, amplitudes, skyAbove, texture));
        }
        return stars.ToImmutable();
    }
}
