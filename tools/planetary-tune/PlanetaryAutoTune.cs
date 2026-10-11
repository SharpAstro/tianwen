using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;

namespace PlanetaryTune;

/// <summary>
/// What the AUTO mode chooses for a capture (docs/plans/planetary-stacking.md, A2, #1389): the keep fraction the sharpened master's band
/// error prefers, and the strength and target the sharpening's gains are derived at. Only settings the stack already has, never free
/// per-band gains, so whatever the tune picks is a setting <see cref="PlanetaryBestStack"/> runs as is.
/// </summary>
public sealed record PlanetaryAutoSettings(double Keep, double Strength, PlanetarySharpenTarget Target)
{
    /// <summary>Today's defaults: the stack's keep, the sharpening at the truth, through the pupil.</summary>
    public static readonly PlanetaryAutoSettings Defaults = new PlanetaryAutoSettings(new PlanetaryStackOptions().KeepFraction, 1, PlanetarySharpenTarget.Telescope);

    /// <summary><paramref name="options"/> with these settings in it.</summary>
    public PlanetaryBestStackOptions ApplyTo(PlanetaryBestStackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { Stack = options.Stack with { KeepFraction = Keep }, Strength = Strength, Target = Target };
    }
}

/// <summary>One candidate of the tune and its band error against the twin's truth (bands 1 to 4 inside 0.9 radii); null where it could not be scored.</summary>
public sealed record PlanetaryTuneTrial(PlanetaryAutoSettings Settings, double? BandError);

/// <summary>
/// The AUTO mode's per-capture tune (A2, #1389): on the capture's calibrated twin (A1, <c>planetary twin</c>), every candidate setting is
/// stacked, sharpened and scored against the twin's truth, and the best is what the real capture is stacked with. The twin is stacked
/// without de-rotation, since its truth is rendered at its reference frame's instant: a de-rotated stack, carried to the run's middle,
/// would be scored against a planet turned elsewhere; and uncropped, onto the truth's grid. A keep is stacked once and every strength
/// and target sharpened from it.
/// </summary>
public static class PlanetaryAutoTune
{
    /// <summary>The keep fractions tried: a sharpened master keeps about half (#1083), so the grid brackets it.</summary>
    public static readonly ImmutableArray<double> Keeps = [0.1, 0.25, 0.5, 0.75];

    /// <summary>The strengths tried: the stops one derivation fits (<see cref="PlanetarySharpening.StrengthStops"/>).</summary>
    public static ImmutableArray<double> Strengths => PlanetarySharpening.StrengthStops;

    /// <summary>The targets tried: through the pupil, and with the telescope undone too (#1366).</summary>
    public static readonly ImmutableArray<PlanetarySharpenTarget> Targets = [PlanetarySharpenTarget.Telescope, PlanetarySharpenTarget.Aperture];

    /// <summary>
    /// Every candidate on <paramref name="twin"/> scored against its <paramref name="truths"/> (one for a mono twin, red, green and blue for a
    /// colour one), stacked and sharpened as <paramref name="options"/> says but for the settings tuned; the trials in the order they ran and
    /// the best. <paramref name="options"/> must name the planet: the sharpening is derived only for a named one.
    /// </summary>
    public static async Task<(PlanetaryTuneTrial Best, ImmutableArray<PlanetaryTuneTrial> Trials)> TuneAsync(IPlanetaryFrameStream twin,
        IReadOnlyList<PlanetaryTruth> truths, PlanetaryBestStackOptions options, IProgress<PlanetaryTuneTrial>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(twin);
        ArgumentNullException.ThrowIfNull(truths);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Planet is not CatalogIndex planet)
        {
            throw new ArgumentException("the tune scores a derived sharpening, which needs the planet named", nameof(options));
        }
        var trials = ImmutableArray.CreateBuilder<PlanetaryTuneTrial>();
        foreach (var keep in Keeps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Uncropped: a master cropped to its coverage (#1300) is off the truth's grid and nothing would be scored.
            var stackOptions = options.Stack with { KeepFraction = keep, Derotation = null, Planet = options.Stack.Planet ?? planet, CropToCoverage = false };
            var stacked = await new LuckyImagingStacker().StackAsync(twin, stackOptions, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var target in Targets)
                {
                    foreach (var strength in Strengths)
                    {
                        var (sharpened, _) = PlanetaryBestStack.Sharpen(stacked.Master, planet, stacked.Epoch, options.Telescope, options.WavelengthsNm,
                            options.Fix, strength, options.LuminanceOnly, target);
                        double? error;
                        try
                        {
                            error = PlanetaryTruth.BandError(sharpened, truths, planet);
                        }
                        finally
                        {
                            sharpened.Release();
                        }
                        var trial = new PlanetaryTuneTrial(new PlanetaryAutoSettings(keep, strength, target), error);
                        trials.Add(trial);
                        progress?.Report(trial);
                    }
                }
            }
            finally
            {
                stacked.Master.Release();
            }
        }
        var all = trials.ToImmutable();
        var best = all.Where(t => t.BandError is not null).MinBy(t => t.BandError) ?? throw new InvalidOperationException("no candidate of the tune could be scored");
        return (best, all);
    }

    /// <summary>
    /// How much of the headroom over the defaults that <paramref name="own"/> (a twin's own best) has, <paramref name="chosen"/> (another
    /// twin's best, applied to this one) recovers: 1 when it does as well as the twin's own tune, 0 when no better than the defaults
    /// (A2's rule 1). Null where a score is missing or the own tune has no headroom.
    /// </summary>
    public static double? HeadroomRecovered(ImmutableArray<PlanetaryTuneTrial> trials, PlanetaryAutoSettings chosen, PlanetaryAutoSettings own)
    {
        double? ErrorOf(PlanetaryAutoSettings settings) => trials.FirstOrDefault(t => t.Settings == settings)?.BandError;
        if (ErrorOf(PlanetaryAutoSettings.Defaults) is not { } defaults || ErrorOf(chosen) is not { } chosenError || ErrorOf(own) is not { } ownError
            || defaults - ownError <= 0)
        {
            return null;
        }
        return (defaults - chosenError) / (defaults - ownError);
    }
}
