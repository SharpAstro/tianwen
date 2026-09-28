using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-limb</c> (docs/plans/planetary-restoration.md, R1): fits a planet's disk at its limb, with the shape and
/// lighting its ephemeris gives, and, handed WinJUPOS's measurement of an image, compares the two.
/// </summary>
internal sealed class PlanetaryGeometrySubCommands(IConsoleHost consoleHost)
{
    public Command BuildLimb()
    {
        var inputsArg = new Argument<string[]>("inputs")
        {
            Description = "Images to fit, or WinJUPOS .ims.xml measurements (the image beside each is fitted and compared with it).",
            Arity = ArgumentArity.OneOrMore,
        };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn, for an image with no .ims.xml.", DefaultValueFactory = _ => "jupiter" };
        var utcOpt = new Option<string?>("--utc") { Description = "The image's time (ISO 8601, UTC), for an image with no .ims.xml." };
        var phaseOpt = new Option<double?>("--phase") { Description = "Override the ephemeris' phase angle, in degrees (0 fits a fully lit disk): a diagnostic." };
        var sunSideOpt = new Option<int?>("--sun-side") { Description = "Force which end of the equator is lit (+1 or -1) instead of fitting both: a diagnostic." };

        var command = new Command("planetary-limb", "Fit a planet's disk at its limb (centre, equatorial radius, axis) with the ephemeris' shape and phase; compare with WinJUPOS's own outline.")
        {
            Arguments = { inputsArg },
            Options = { planetOpt, utcOpt, phaseOpt, sunSideOpt },
        };

        command.SetAction((parseResult, ct) =>
        {
            var failed = 0;
            consoleHost.WriteScrollable("image                                   x0        y0        R       axis   k     sigma  side  rms       | WinJUPOS dx     dy     dR (%)   rotation");
            foreach (var input in parseResult.GetValue(inputsArg) ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var measurement = input.EndsWith(".ims.xml", StringComparison.OrdinalIgnoreCase) ? WinJuposMeasurement.TryRead(input) : null;
                var imagePath = measurement is { } m ? m.LocalImage(input) : input;
                if (imagePath is null || !Image.TryReadImageFile(imagePath, out var image))
                {
                    consoleHost.WriteError($"{input}: no image to fit");
                    failed++;
                    continue;
                }
                var planet = (measurement?.Body ?? parseResult.GetValue(planetOpt) ?? "jupiter").ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
                if ((measurement?.Utc ?? ParseUtc(parseResult.GetValue(utcOpt))) is not { } utc)
                {
                    consoleHost.WriteError($"{input}: no time (pass --utc, or a WinJUPOS .ims.xml)");
                    failed++;
                    continue;
                }
                var aspect = PhysicalEphemeris.Compute(planet, utc);
                var options = PlanetaryLimbFit.OptionsFor(aspect) with { SunSide = parseResult.GetValue(sunSideOpt) };
                if (parseResult.GetValue(phaseOpt) is { } phase)
                {
                    options = options with { PhaseAngleDeg = phase };
                }
                if (PlanetaryLimbFit.Fit(image, options) is not { } fit)
                {
                    consoleHost.WriteError($"{input}: no disk found");
                    failed++;
                    continue;
                }
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(imagePath),-38} {fit.CenterX,8:0.000}  {fit.CenterY,8:0.000}  {fit.EquatorialRadius,7:0.000}  {fit.AxisAngleDeg,6:0.00}  {fit.LimbDarkening,4:0.00}  {fit.PsfSigma,5:0.00}  {fit.SunSide,4:+0;-0;0}  {fit.RmsResidual,8:0.00000}");
                if (measurement is { } w)
                {
                    line += string.Create(CultureInfo.InvariantCulture,
                        $" | {fit.CenterX - w.CenterX,+6:+0.00;-0.00} {fit.CenterY - w.CenterY,+6:+0.00;-0.00} {100 * (fit.EquatorialRadius - w.EquatorialRadius) / w.EquatorialRadius,+7:+0.00;-0.00}  {w.RotationAngleDeg,7:0.0}");
                }
                consoleHost.WriteScrollable(line);
            }
            return Task.FromResult(failed == 0 ? 0 : 1);
        });
        return command;
    }

    private static DateTimeOffset? ParseUtc(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
