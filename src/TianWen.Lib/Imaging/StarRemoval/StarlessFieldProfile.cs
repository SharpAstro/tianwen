using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging.StarRemoval;

/// <summary>
/// One channel of the profile the plate builder subtracted every star with: a Moffat of <paramref name="Alpha"/> and
/// <paramref name="Beta"/>, integrated over the pixel, plus the field's residual table (bins of
/// <see cref="RadialCorrection.BinWidth"/> pixels at <paramref name="TableAlpha"/>, a fraction of the star's amplitude, read
/// at a pixel's centre). Empty <paramref name="Table"/> where the field had too few stars to measure one.
/// </summary>
public sealed record FieldChannelProfile(double Alpha, double Beta, double TableAlpha, ImmutableArray<float> Table)
{
    /// <summary>The Moffat's full width at half maximum, in pixels.</summary>
    public double Fwhm => 2.0 * Alpha * Math.Sqrt(Math.Pow(2.0, 1.0 / Beta) - 1.0);
}

/// <summary>
/// The plate builder's field profile, per channel and for the luminance it calibrates on (docs/plans/star-remover-training.md,
/// "R1: the injector"): every amplitude in a plate's catalogue was fitted with it, so it is the one profile those amplitudes
/// mean anything with. Kept beside the plate as <c>&lt;stem&gt;_plate.profile.json</c>. A real star is a core and a halo
/// one Moffat cannot both follow, which is why the builder carries the table; the PSF store's single Moffat per channel holds
/// almost none of the light real stars carry past 4.5 px (0 to 2 percent against 3 to 42), and a saturated star's
/// amplitude, fitted on its wings with this profile and drawn with that one, came out 1.2 to 1.6 times too bright in the core.
/// </summary>
/// <param name="Channels">Each channel's profile (one for a mono master).</param>
/// <param name="Luminance">The luminance's, which the builder's star tests and saturated fits use; its alpha over the
/// stacked profile's seed is the report's <c>FieldWidthScale</c> and its beta the report's <c>FieldBeta</c>, so a profile
/// measured again can be checked against the plate it describes.</param>
public sealed record StarlessFieldProfile(ImmutableArray<FieldChannelProfile> Channels, FieldChannelProfile Luminance)
{
    /// <summary>The profile of the plate <c>&lt;stem&gt;_plate.fits</c> in <paramref name="platesDir"/>.</summary>
    public static string PathFor(string platesDir, string stem) => Path.Combine(platesDir, stem + "_plate.profile.json");

    /// <summary>Writes <paramref name="profile"/> to <paramref name="path"/>.</summary>
    public static async Task WriteAsync(string path, StarlessFieldProfile profile, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, profile, StarlessFieldProfileJsonContext.Default.StarlessFieldProfile, cancellationToken);
    }

    /// <summary>Reads a profile back; null where the plate has none (a store built before profiles were kept).</summary>
    public static async Task<StarlessFieldProfile?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, StarlessFieldProfileJsonContext.Default.StarlessFieldProfile, cancellationToken);
    }
}

[JsonSerializable(typeof(StarlessFieldProfile))]
[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
internal partial class StarlessFieldProfileJsonContext : JsonSerializerContext;
