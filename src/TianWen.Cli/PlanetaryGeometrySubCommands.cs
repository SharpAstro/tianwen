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
/// lighting its ephemeris gives, and, handed WinJUPOS's measurement of an image, compares the two. <c>planetary-aperture</c>
/// says which of the corpus' two telescopes took a Jupiter capture, by the rules the plan pre-registered.
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

    // The corpus' two telescopes (docs/plans/planetary-restoration.md, "The corpus"): a Skywatcher 10 inch Newtonian with a
    // four-vane spider, and a Skymax 102 Maksutov. The Maksutov's cutoff is reckoned at 400 nm in every plane, the shortest
    // any plane passes behind a UV/IR cut, so no plane's short-wavelength leak can make it look larger than it is. A halo
    // mostly at black cannot show a spike worth a fraction of an 8-bit step, so there not even the spider is looked for.
    private const double MaksutovApertureM = 0.102;
    private const double RuleOutWavelengthM = 400e-9;
    private const double HaloAtBlackLimit = 0.5;

    public Command BuildAperture()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "SER captures of Jupiter.", Arity = ArgumentArity.OneOrMore };
        var bestOpt = new Option<int>("--best") { Description = "How many of the best frames to stack and put through the spectrum.", DefaultValueFactory = _ => 2000 };
        var utcOpt = new Option<string?>("--utc") { Description = "The capture's time (ISO 8601, UTC), for a SER without timestamps." };
        var stacksOpt = new Option<string?>("--stacks") { Description = "A folder to write each capture's stack of best frames to, as <name>.stack.fits, to look at the halo the spider was sought in." };

        var command = new Command("planetary-aperture", "Which telescope took a Jupiter capture: the aperture's cutoff in each plane's averaged power spectrum, read as a lower bound, and a spider's spikes in the stack's halo (R1).")
        {
            Arguments = { inputsArg },
            Options = { bestOpt, utcOpt, stacksOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var stacks = parseResult.GetValue(stacksOpt);
            if (stacks is not null)
            {
                Directory.CreateDirectory(stacks);
            }
            var failed = 0;
            foreach (var input in parseResult.GetValue(inputsArg) ?? [])
            {
                ct.ThrowIfCancellationRequested();
                using var stream = SerFrameStream.Open(input);
                var utc = MidCapture(stream) ?? ParseUtc(parseResult.GetValue(utcOpt));
                if (utc is not { } when)
                {
                    consoleHost.WriteError($"{input}: no timestamps (pass --utc)");
                    failed++;
                    continue;
                }
                var aspect = PhysicalEphemeris.Compute(CatalogIndex.Jupiter, when);
                if (await PlanetaryApertureEvidence.MeasureAsync(stream, aspect, parseResult.GetValue(bestOpt), ct) is not { } evidence)
                {
                    consoleHost.WriteError($"{input}: no disk found");
                    failed++;
                    continue;
                }

                if (stacks is not null)
                {
                    evidence.Stack.WriteToFitsFile(Path.Combine(stacks, Path.GetFileNameWithoutExtension(input) + ".stack.fits"));
                }

                // Only the frames' cutoff rules the Maksutov out. The halves' shared detail is reported, never decided on: the
                // two halves share the sensor's fixed pattern, which reads as detail both hold (2022-09-03's luminance).
                var rulesOut = false;
                foreach (var plane in evidence.Planes)
                {
                    rulesOut |= ApertureCutoff.RulesOut(plane.Cutoff, MaksutovApertureM, plane.ArcsecPerPixel, RuleOutWavelengthM);
                }
                // The evidence is one-sided. A spider's spikes, or an aperture past the Maksutov's, say Newtonian; nothing here
                // says Maksutov, because a halo without spikes proves no absence: 2022-09-03's Blue read 2.4 two minutes after
                // its Red read 18.3 through the same Newtonian (a narrow annulus, blue light, the seeing).
                var spider = evidence.Spider?.Statistic;
                var verdict = rulesOut || spider > SpiderSignature.SpiderAbove
                    ? "Newtonian"
                    : spider is null || evidence.HaloAtBlack >= HaloAtBlackLimit ? "undecided (no halo to look in)" : "undecided (no spider seen)";

                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(input)}: {evidence.FramesUsed} of {evidence.FramesGraded} frames, R {evidence.Limb.EquatorialRadius:0.0} px, {evidence.ArcsecPerPixel:0.000}\"/px, {when:yyyy-MM-dd HH:mm} UTC -> {verdict}"));
                foreach (var plane in evidence.Planes)
                {
                    var bound = ApertureCutoff.ApertureM(plane.Cutoff.CutoffCyclesPerPixel, plane.ArcsecPerPixel, RuleOutWavelengthM);
                    var detailBound = ApertureCutoff.ApertureM(plane.DetailCyclesPerPixel, plane.ArcsecPerPixel, RuleOutWavelengthM);
                    var maksutovCutoff = ApertureCutoff.CyclesPerPixel(MaksutovApertureM, plane.ArcsecPerPixel, RuleOutWavelengthM);
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"    {plane.Plane,-4} {plane.ArcsecPerPixel:0.000}\"/px  frames' cutoff {plane.Cutoff.CutoffCyclesPerPixel:0.000} c/px{(plane.Cutoff.BeyondCorners ? " (past the corners)" : "")} (D >= {bound * 1000:0} mm)  " +
                        $"halves' detail to {plane.DetailCyclesPerPixel:0.000} c/px (D >= {detailBound * 1000:0} mm)  at 400 nm; the Maksutov's {maksutovCutoff:0.000} c/px  corners {plane.Cutoff.CornerSlopeSigmas:+0.0;-0.0} sigma"));
                }
                consoleHost.WriteScrollable(evidence.Spider is { } sp
                    ? string.Create(CultureInfo.InvariantCulture, $"    spider {sp.Statistic:0.0} at {sp.SpikeAngleDeg:0.0} deg, annulus {sp.InnerRadius:0} to {sp.OuterRadius:0} px, {evidence.HaloAtBlack:P0} of its samples at black")
                    : "    spider: no full annulus in the frame");
            }
            return failed == 0 ? 0 : 1;
        });
        return command;
    }

    private static DateTimeOffset? MidCapture(SerFrameStream stream)
        => stream.HasTimestamps && stream.TimestampOf(0) is { } first && stream.TimestampOf(stream.FrameCount - 1) is { } last
            ? first + ((last - first) / 2)
            : null;

    private static DateTimeOffset? ParseUtc(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
