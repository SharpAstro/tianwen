using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Degradation;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-limb</c> (docs/plans/planetary-restoration.md, R1): fits a planet's disk at its limb, with the shape and
/// lighting its ephemeris gives, and, handed WinJUPOS's measurement of an image, compares the two. <c>planetary-aperture</c>
/// says which of the corpus' two telescopes took a Jupiter capture, by the rules the plan pre-registered.
/// <c>planetary-render-truth</c> renders a global map at a capture's geometry through its telescope (T1, R2).
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
            consoleHost.WriteScrollable("image                                   x0        y0        R       axis   k     sigma  side  rms       rms/disk | WinJUPOS dx     dy     dR (%)   rotation");
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
                    $"{Path.GetFileName(imagePath),-38} {fit.CenterX,8:0.000}  {fit.CenterY,8:0.000}  {fit.EquatorialRadius,7:0.000}  {fit.AxisAngleDeg,6:0.00}  {fit.LimbDarkening,4:0.00}  {fit.PsfSigma,5:0.00}  {fit.SunSide,4:+0;-0;0}  {fit.RmsResidual,8:0.00000}  {fit.RmsResidual / fit.Brightness,7:0.00000}");
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

    // The corpus' two telescopes (docs/plans/planetary-restoration.md, "The corpus"): a Saxon 10 inch f/4.7 Newtonian with a
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
                var utc = stream.MidCapture ?? ParseUtc(parseResult.GetValue(utcOpt));
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

    public Command BuildSeeing()
    {
        var inputsArg = new Argument<string[]>("inputs") { Description = "Mono SER captures of a planet.", Arity = ArgumentArity.OneOrMore };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var utcOpt = new Option<string?>("--utc") { Description = "The capture's time (ISO 8601, UTC), for a SER without timestamps." };
        var fpsOpt = new Option<double?>("--fps") { Description = "The frame rate, for a SER without timestamps." };
        var pairsOpt = new Option<int>("--pairs") { Description = "Pairs of consecutive frames to read the warp and the noise from.", DefaultValueFactory = _ => 500 };
        var warpFramesOpt = new Option<int>("--warp-frames") { Description = "Consecutive frames averaged (aligned) before the warp is read: one frame of a faint capture cannot place a patch.", DefaultValueFactory = _ => 1 };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The alignment points' patch, a power of two.", DefaultValueFactory = _ => CaptureStatisticsOptions.DefaultAlignmentPatchSize };
        var spacingOpt = new Option<int>("--ap-spacing") { Description = "The alignment points' spacing.", DefaultValueFactory = _ => CaptureStatisticsOptions.DefaultAlignmentPointSpacing };
        var firstOpt = new Option<int>("--first") { Description = "The first frame measured.", DefaultValueFactory = _ => 0 };
        var plainOpt = new Option<bool>("--plain-correlation") { Description = "Register frames and points by a plain cross-correlation, not phase correlation (R5)." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only this many frames, from --first." };
        var planeOpt = new Option<string?>("--plane") { Description = "A colour capture's photosite colour to measure: r, g (the greens on the red rows), g2 or b (R5a)." };

        var command = new Command("planetary-seeing",
            "A capture's statistics, measured as a synthetic capture's are (R2): the shift's seeing and mount parts, the warp, the quality distribution, each band's noise, the camera's levels and gain.")
        {
            Arguments = { inputsArg },
            Options = { planetOpt, utcOpt, fpsOpt, pairsOpt, warpFramesOpt, patchOpt, spacingOpt, firstOpt, framesOpt, plainOpt, planeOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var failed = 0;
            var plane = ParsePlane(parseResult.GetValue(planeOpt));
            if (parseResult.GetValue(planeOpt) is { } named && plane is null)
            {
                consoleHost.WriteError($"--plane {named}: r, g, g2 or b");
                return 1;
            }
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            foreach (var input in parseResult.GetValue(inputsArg) ?? [])
            {
                ct.ThrowIfCancellationRequested();
                using var reader = SerReader.Open(input);
                using var whole = new SerFrameStream(reader, ownsReader: false);
                var first = Math.Clamp(parseResult.GetValue(firstOpt), 0, whole.FrameCount - 1);
                using var window = new PlanetaryFrameWindow(whole, first, Math.Min(whole.FrameCount - first, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
                // A colour capture is measured a photosite colour at a time: the statistics are a mono capture's.
                if (window.Layout == PlanetaryFrameLayout.SplitCfa && plane is null)
                {
                    consoleHost.WriteError($"{input}: a colour capture; pass --plane r, g, g2 or b");
                    failed++;
                    continue;
                }
                using var planeStream = plane is { } channel && window.Layout == PlanetaryFrameLayout.SplitCfa ? new CfaPlaneStream(window, channel) : null;
                IPlanetaryFrameStream stream = planeStream is null ? window : planeStream;
                if ((stream.MidCapture ?? ParseUtc(parseResult.GetValue(utcOpt))) is not { } when)
                {
                    consoleHost.WriteError($"{input}: no timestamps (pass --utc)");
                    failed++;
                    continue;
                }
                var options = new CaptureStatisticsOptions(PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when)))
                {
                    FullScaleAdu = reader.MaxSampleValue,
                    FramesPerSecond = parseResult.GetValue(fpsOpt),
                    Pairs = parseResult.GetValue(pairsOpt),
                    WarpFrames = parseResult.GetValue(warpFramesOpt),
                    AlignmentPatchSize = parseResult.GetValue(patchOpt),
                    AlignmentPointSpacing = parseResult.GetValue(spacingOpt),
                    WhitenedCorrelation = !parseResult.GetValue(plainOpt),
                };
                var progress = new Progress<string>(line => consoleHost.WriteScrollable("    " + line));
                if (await PlanetaryCaptureStatistics.MeasureAsync(stream, options, progress, ct) is not { } statistics)
                {
                    consoleHost.WriteError($"{input}: no disk found");
                    failed++;
                    continue;
                }
                WriteStatistics(Path.GetFileName(input), statistics);
            }
            return failed == 0 ? 0 : 1;
        });
        return command;
    }

    public Command BuildDegrade()
    {
        var inputArg = new Argument<string>("capture") { Description = "The real mono SER capture whose seeing, motion and camera the synthetic one takes." };
        var mapOpt = new Option<string>("--map") { Description = "The global map (FITS; OPAL's).", Required = true };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The synthetic SER to write; its truth and record go beside it.", Required = true };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var kOpt = new Option<double>("--k") { Description = "Minnaert's exponent for the map's filter.", DefaultValueFactory = _ => 0.95 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var r0Opt = new Option<double>("--r0") { Description = "The Fried parameter at 500 nm, cm.", DefaultValueFactory = _ => 5 };
        var windOpt = new Option<double>("--wind") { Description = "The wind carrying the screen, m/s.", DefaultValueFactory = _ => 10 };
        var outerScaleOpt = new Option<double?>("--outer-scale") { Description = "The turbulence's outer scale, m (von Karman; none for Kolmogorov)." };
        var scatterOpt = new Option<double>("--scatter") { Description = "The share of the light the telescope scatters wide (0 for none).", DefaultValueFactory = _ => 0 };
        var scatterCoreOpt = new Option<double>("--scatter-core") { Description = "The scatter kernel's core, arcsec.", DefaultValueFactory = _ => 5 };
        var farWingOpt = new Option<bool>("--far-wing") { Description = "Give every frame the pupil's diffraction wing past the PSF grid's 32 px (#1222); the twins' --scatter was calibrated without it." };
        var realStatisticsOpt = new Option<string?>("--real-statistics") { Description = "A file the real capture's statistics are read from when it holds them for this capture, frames and options, and saved to otherwise." };
        var localR0Opt = new Option<double?>("--local-r0") { Description = "A layer of turbulence at the telescope (tube, mirror), its Fried parameter at 500 nm, cm (none by default)." };
        var localOuterScaleOpt = new Option<double>("--local-outer-scale") { Description = "The local layer's outer scale, m.", DefaultValueFactory = _ => 0.25 };
        var localWindOpt = new Option<double>("--local-wind") { Description = "The local layer's drift across the pupil, m/s.", DefaultValueFactory = _ => 1 };
        var localRenewOpt = new Option<double?>("--local-renew-ms") { Description = "The time over which the local layer renews itself in place, ms (its air boiling, R4 per-point); by default only as its drift brings the screen round." };
        var defocusOpt = new Option<double>("--defocus-nm") { Description = "The telescope's own defocus, RMS wavefront error in nm.", DefaultValueFactory = _ => 0 };
        var exposureOpt = new Option<double>("--exposure-ms") { Description = "Each frame's exposure, ms, over which the wind moves the air (0 for an instant).", DefaultValueFactory = _ => 0 };
        var gainOpt = new Option<double?>("--gain") { Description = "Electrons an ADU (else from the finest band's noise on the disk)." };
        var warpRmsOpt = new Option<double>("--warp-rms") { Description = "The local warp's RMS per axis, px (0 for none).", DefaultValueFactory = _ => 0 };
        var warpLengthOpt = new Option<double>("--warp-length") { Description = "The warp's correlation length, px.", DefaultValueFactory = _ => 20 };
        var warpLagOpt = new Option<double>("--warp-lag1") { Description = "The warp's correlation a frame later.", DefaultValueFactory = _ => 0.9 };
        var seedOpt = new Option<int>("--seed") { Description = "The draws' seed.", DefaultValueFactory = _ => 1 };
        var replayOpt = new Option<bool>("--replay-shifts") { Description = "Move each disk by the real capture's measured shift (its tilt taken out), not by the screen's tilt on the mount's drift." };
        var pairsOpt = new Option<int>("--pairs") { Description = "Pairs of consecutive frames the statistics read the warp and the noise from.", DefaultValueFactory = _ => 500 };
        var warpFramesOpt = new Option<int>("--warp-frames") { Description = "Frames averaged before the warp is read.", DefaultValueFactory = _ => 1 };
        var patchOpt = new Option<int>("--ap-patch") { Description = "The statistics' alignment-point patch.", DefaultValueFactory = _ => CaptureStatisticsOptions.DefaultAlignmentPatchSize };
        var spacingOpt = new Option<int>("--ap-spacing") { Description = "The statistics' alignment-point spacing.", DefaultValueFactory = _ => CaptureStatisticsOptions.DefaultAlignmentPointSpacing };
        var plainOpt = new Option<bool>("--plain-correlation") { Description = "The statistics register frames and points by a plain cross-correlation, not phase correlation (R5)." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the capture's first frames, measured and made (a quicker trial)." };
        var bayerMapsOpt = new Option<string?>("--bayer-maps") { Description = "A colour capture's three maps, red, green and blue, a comma list (R5a; OPAL's F631N, F502N and F395N)." };
        var bayerWavelengthsOpt = new Option<string>("--bayer-wavelengths") { Description = "The camera's red, green and blue effective wavelengths, nm.", DefaultValueFactory = _ => "610,535,460" };
        var truthUpsampleOpt = new Option<string?>("--truth-upsample") { Description = "A colour twin's truths rendered at these scales too, a comma list (1.5 for a Bayer drizzle at 1.5x; R5a), each .truth.<colour>.x<scale>.fits." };
        var bayerKOpt = new Option<string?>("--bayer-k") { Description = "Minnaert's exponent for each colour's map, red, green and blue (OPAL's: 0.999, 0.950, 0.850); --k for all three by default." };
        var spanOpt = new Option<double?>("--span-minutes") { Description = "Spread the synthetic capture's frames evenly in time over this many minutes, in their order, so the planet turns as it would over a run (R6 part 2: a de-rotation's twin). Each frame keeps the real capture's seeing; the air between two frames is no longer the next instant's." };
        var truthAtOpt = new Option<string>("--truth-at") { Description = "The instant the truth is rendered at: reference (the statistics' reference frame's) or middle (the capture's middle, where a de-rotated stack shows the planet).", DefaultValueFactory = _ => "reference" };
        var psfTruthOpt = new Option<bool>("--psf-truth") { Description = "Write every frame's PSF, shift and brightness beside the capture (<capture>.psf), the truth a multi-frame bound is computed against (R8 part 1; about 64 KB a frame). Mono only." };
        var moonsOpt = new Option<double>("--moons") { Description = "Every Galilean moon within this many radii of Jupiter's centre, at its place as the frames go, in the frames and the truth (R8 follow-up 4); 0 for none." };
        var moonLevelOpt = new Option<double>("--moon-level") { Description = "The moons' surface brightness over the disk's mean inside 0.8 radii.", DefaultValueFactory = _ => 1 };
        var highR0Opt = new Option<double?>("--high-r0") { Description = "The free air at an altitude, its Fried parameter at 500 nm, cm (R4 per-point, #1071): each point of the disk looks through it at its own footprint, so the blur and the warp vary over the disk. None by default; then --warp-rms must stay 0, and <capture>.field records each frame's per-point truth." };
        var highAltitudeOpt = new Option<double>("--high-altitude") { Description = "The layer's altitude along the line of sight, km.", DefaultValueFactory = _ => 10 };
        var highWindOpt = new Option<double>("--high-wind") { Description = "The wind carrying the layer, m/s.", DefaultValueFactory = _ => 20 };
        var highWindAngleOpt = new Option<double>("--high-wind-angle") { Description = "The layer's wind direction, degrees from +x toward +y.", DefaultValueFactory = _ => 30 };
        var highOuterScaleOpt = new Option<double?>("--high-outer-scale") { Description = "The layer's outer scale, m (none for Kolmogorov)." };
        var fieldGridOpt = new Option<int>("--field-grid") { Description = "The spacing of the points the layer's PSF is computed at, px.", DefaultValueFactory = _ => 6 };
        var ringLevelsOpt = new Option<string?>("--ring-levels")
        {
            Description = "Saturn's rings' mean levels over the map's mean albedo, C,B,Cassini,A, drawn with their radial structure (S3, SaturnRings.Structured): "
                + "what the twin is calibrated by; on a colour capture four for every colour or twelve, red's, green's and blue's. Default: the nominal ones.",
        };
        var noTwinRingsOpt = new Option<bool>("--no-rings") { Description = "Draw Saturn's globe alone, without its rings." };

        var command = new Command("planetary-degrade",
            "A synthetic capture from a global map with a real capture's own seeing, motion and camera (R2): measure the real one, make the synthetic one, measure it the same way, and compare.")
        {
            Arguments = { inputArg },
            Options = { mapOpt, outputOpt, planetOpt, kOpt, telescopeOpt, wavelengthOpt, r0Opt, windOpt, outerScaleOpt, exposureOpt, defocusOpt, localR0Opt, localOuterScaleOpt, localWindOpt, localRenewOpt, scatterOpt, scatterCoreOpt, farWingOpt, realStatisticsOpt, gainOpt, warpRmsOpt, warpLengthOpt, warpLagOpt, seedOpt, replayOpt, pairsOpt, warpFramesOpt, patchOpt, spacingOpt, plainOpt, framesOpt, bayerMapsOpt, bayerWavelengthsOpt, bayerKOpt, truthUpsampleOpt, spanOpt, truthAtOpt, psfTruthOpt, moonsOpt, moonLevelOpt, highR0Opt, highAltitudeOpt, highWindOpt, highWindAngleOpt, highOuterScaleOpt, fieldGridOpt, ringLevelsOpt, noTwinRingsOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg) ?? "";
            var output = parseResult.GetValue(outputOpt) ?? "";
            var mapPath = parseResult.GetValue(mapOpt) ?? "";
            if (PlanetMap.ReadFits(mapPath) is not { } map)
            {
                consoleHost.WriteError($"{mapPath}: no map");
                return 1;
            }
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var progress = new Progress<string>(line => consoleHost.WriteScrollable("    " + line));
            // Saturn (S3): its map read through the zonal fill, its rings drawn at the levels asked, a colour's own where given: the
            // globe is yellower than the rings, so they stand brighter over it in blue (B 0.72 of the globe against red's 0.61 on
            // 2022-10-09).
            SaturnRings? twinRings = null;
            var colourRings = new SaturnRings?[3];
            if (planet == CatalogIndex.Saturn)
            {
                map = map.FilledZonally();
                if (!parseResult.GetValue(noTwinRingsOpt))
                {
                    var levels = CommaNumbers(parseResult.GetValue(ringLevelsOpt));
                    if (levels.Length is not (0 or 4 or 12))
                    {
                        consoleHost.WriteError("--ring-levels takes four levels, C, B, the Cassini division and A, or twelve, red's, green's and blue's");
                        return 1;
                    }
                    // Drawn with their radial structure: the limb fit's four flat levels read a real capture's that way (S3).
                    SaturnRings At(int first) => SaturnRings.Structured(levels[first], levels[first + 1], levels[first + 2], levels[first + 3]);
                    var main = SaturnRings.Main.Rings;
                    twinRings = levels.Length == 0 ? SaturnRings.Structured(main[0].Level, main[1].Level, main[2].Level, main[3].Level) : At(0);
                    for (var c = 0; c < 3; c++)
                    {
                        colourRings[c] = levels.Length == 12 ? At(4 * c) : twinRings;
                    }
                }
            }

            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var frames = Math.Min(parseResult.GetValue(framesOpt) ?? whole.FrameCount, whole.FrameCount);
            using var real = new PlanetaryFrameWindow(whole, 0, frames);
            if (real.MidCapture is not { } mid || reader.Timestamps is not { IsDefaultOrEmpty: false } allTimes)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var times = allTimes[..frames];
            if (parseResult.GetValue(spanOpt) is { } spanMinutes)
            {
                // The frames spread over the span, in their order and at their own spacing scaled: the seeing each frame was made
                // with is the capture's, the planet's turn the span's.
                var (first, taken) = (times[0], (times[^1] - times[0]).TotalSeconds);
                if (spanMinutes <= 0 || taken <= 0)
                {
                    consoleHost.WriteError("--span-minutes needs a positive span and a capture of more than one instant");
                    return 1;
                }
                var stretch = spanMinutes * 60 / taken;
                times = [.. times.Select(t => first + ((t - first) * stretch))];
            }
            var truthAtMiddle = (parseResult.GetValue(truthAtOpt) ?? "reference").ToLowerInvariant() switch
            {
                "middle" => true,
                "reference" => false,
                var other => throw new ArgumentException($"--truth-at {other}: reference or middle"),
            };
            var middle = times[0] + ((times[^1] - times[0]) / 2);
            var aspect = PhysicalEphemeris.Compute(planet, mid);
            var measure = new CaptureStatisticsOptions(PlanetaryLimbFit.OptionsFor(aspect))
            {
                FullScaleAdu = reader.MaxSampleValue,
                Pairs = parseResult.GetValue(pairsOpt),
                WarpFrames = parseResult.GetValue(warpFramesOpt),
                AlignmentPatchSize = parseResult.GetValue(patchOpt),
                AlignmentPointSpacing = parseResult.GetValue(spacingOpt),
                WhitenedCorrelation = !parseResult.GetValue(plainOpt),
            };
            // A real capture measured once serves every synthetic one compared with it; a colour capture's photosite colours are
            // each measured, and cached, on their own.
            var statisticsPath = parseResult.GetValue(realStatisticsOpt);
            async Task<CaptureStatistics?> MeasureReal(IPlanetaryFrameStream stream, string colour)
            {
                var cachePath = statisticsPath is null || colour.Length == 0 ? statisticsPath : $"{statisticsPath}.{colour}";
                var key = colour.Length == 0
                    ? $"{Path.GetFullPath(input)} | {stream.FrameCount} frames | {measure}"
                    : $"{Path.GetFullPath(input)} | {colour} | {stream.FrameCount} frames | {measure}";
                var label = colour.Length == 0 ? Path.GetFileName(input) : $"{Path.GetFileName(input)} ({colour})";
                if (cachePath is not null && await PlanetaryCaptureStatistics.TryLoadAsync(cachePath, key, ct) is { } cached)
                {
                    consoleHost.WriteScrollable($"{label}: statistics read from {cachePath}");
                    return cached;
                }
                consoleHost.WriteScrollable($"measuring {label}");
                var measured = await PlanetaryCaptureStatistics.MeasureAsync(stream, measure, progress, ct);
                if (measured is null)
                {
                    consoleHost.WriteError($"{input}: no disk found{(colour.Length == 0 ? "" : $" in its {colour} photosites")}");
                    return null;
                }
                if (cachePath is not null)
                {
                    await PlanetaryCaptureStatistics.SaveAsync(measured, key, cachePath, ct);
                }
                return measured;
            }

            var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? MaksutovPupil : NewtonianPupil;
            // Everything the air, the telescope, the warp and the draws set: the same for every colour of one capture.
            DegradeOptions Atmosphere(double wavelengthM, double k) => new DegradeOptions(pupil, wavelengthM)
            {
                R0M = parseResult.GetValue(r0Opt) / 100,
                WindMps = parseResult.GetValue(windOpt),
                OuterScaleM = parseResult.GetValue(outerScaleOpt) ?? double.PositiveInfinity,
                ExposureSeconds = parseResult.GetValue(exposureOpt) / 1000,
                DefocusNm = parseResult.GetValue(defocusOpt),
                LocalR0M = parseResult.GetValue(localR0Opt) / 100 ?? double.PositiveInfinity,
                LocalOuterScaleM = parseResult.GetValue(localOuterScaleOpt),
                LocalWindMps = parseResult.GetValue(localWindOpt),
                LocalRenewSeconds = parseResult.GetValue(localRenewOpt) / 1000,
                ScatterFraction = parseResult.GetValue(scatterOpt),
                ScatterCoreArcsec = parseResult.GetValue(scatterCoreOpt),
                FarWing = parseResult.GetValue(farWingOpt),
                MinnaertK = k,
                WarpRmsPx = parseResult.GetValue(warpRmsOpt),
                WarpLengthPx = parseResult.GetValue(warpLengthOpt),
                WarpLag1 = parseResult.GetValue(warpLagOpt),
                Seed = parseResult.GetValue(seedOpt),
                KeepScreenTilt = !parseResult.GetValue(replayOpt),
                MoonsWithinRadii = parseResult.GetValue(moonsOpt),
                MoonLevel = parseResult.GetValue(moonLevelOpt),
                HighR0M = parseResult.GetValue(highR0Opt) / 100 ?? double.PositiveInfinity,
                HighAltitudeM = parseResult.GetValue(highAltitudeOpt) * 1000,
                HighWindMps = parseResult.GetValue(highWindOpt),
                HighWindAngleDeg = parseResult.GetValue(highWindAngleOpt),
                HighOuterScaleM = parseResult.GetValue(highOuterScaleOpt) ?? double.PositiveInfinity,
                FieldGridPx = parseResult.GetValue(fieldGridOpt),
                Rings = twinRings,
            };
            // The camera's own terms from the far sky, where the planet's scattered light has gone (the synthetic capture scatters
            // its own light into the ring), as they were before the camera rounded them.
            static DegradeOptions WithCamera(DegradeOptions atmosphere, CameraEstimate estimate, double gainElectrons) => atmosphere with
            {
                FullScaleAdu = estimate.FullScaleAdu,
                OffsetAdu = estimate.LocalSkyLevel,
                ReadNoiseAdu = estimate.FarSkyNoise,
                ElectronsPerAdu = gainElectrons,
                DiskLevelAdu = estimate.DiskLevel,
            };
            string Describe(DegradeOptions o, DiskPlacement at, double pixelScale) => string.Create(CultureInfo.InvariantCulture,
                $"disk at {at.CenterX:0.00}, {at.CenterY:0.00}, R {at.EquatorialRadius:0.00} px ({pixelScale:0.0000}\"/px), north {at.NorthAngleDeg:0.0} deg; " +
                $"r0 {o.R0M * 100:0.0} cm at 500 nm, outer scale {(double.IsPositiveInfinity(o.OuterScaleM) ? "none" : $"{o.OuterScaleM:0.#} m")}, wind {o.WindMps:0} m/s, exposure {o.ExposureSeconds * 1000:0.#} ms, defocus {o.DefocusNm:0} nm RMS, {(double.IsFinite(o.LocalR0M) ? $"a local layer of r0 {o.LocalR0M * 100:0.0} cm, outer scale {o.LocalOuterScaleM:0.00} m, drifting {o.LocalWindMps:0.#} m/s{(o.LocalRenewSeconds is { } renew ? $", renewing over {renew * 1000:0} ms" : "")}, " : "")}{(o.ScatterFraction > 0 ? $"{o.ScatterFraction * 100:0.##} % scattered with a core of {o.ScatterCoreArcsec:0.#}\", " : "")}{o.WavelengthM * 1e9:0} nm, oversampled {PlanetaryDegrade.OversampleFor(pixelScale, pupil.DiameterM, o.WavelengthM)}x; " +
                $"camera offset {o.OffsetAdu:0.00}, read noise {o.ReadNoiseAdu:0.000} ADU, {o.ElectronsPerAdu:0.0} e-/ADU, disk {o.DiskLevelAdu:0.0} ADU; {DescribeWarp(o)}" +
                $"{(o.KeepScreenTilt ? "the screen's tilt on the mount's drift" : "the real shifts replayed")}");
            // The planet's own level: the one measured is a frame's, through the seeing, the diffraction and the scatter, which carry
            // light out of the circle it is read in; the render takes it back (S3). The layer at an altitude's per-point PSFs keep the
            // level as measured.
            DegradeOptions AtShownLevel(DegradeOptions o, PlanetMap m, DiskPlacement at, double pixelScale, string what)
            {
                if (o.HasHighLayer)
                {
                    return o;
                }
                var gain = PlanetaryDegrade.ShownLevelGain(m, planet, times, at, pixelScale, o);
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"{what}: the planet's own level is {gain:0.000} of the {o.DiskLevelAdu:0.0} ADU measured, the light the blur carries out of 0.8 radii put back"));
                return o with { DiskLevelAdu = o.DiskLevelAdu * gain };
            }
            // The warp: the layer at an altitude's, which makes it from each point's tilt, or the one asked for.
            static string DescribeWarp(DegradeOptions o) => o.HasHighLayer
                ? string.Create(CultureInfo.InvariantCulture,
                    $"a layer at {o.HighAltitudeM / 1000:0.#} km of r0 {o.HighR0M * 100:0.0} cm, outer scale {(double.IsPositiveInfinity(o.HighOuterScaleM) ? "none" : $"{o.HighOuterScaleM:0.#} m")}, wind {o.HighWindMps:0.#} m/s at {o.HighWindAngleDeg:0} deg, its PSF every {o.FieldGridPx} px (its tilts the warp); ")
                : string.Create(CultureInfo.InvariantCulture, $"warp {o.WarpRmsPx:0.00} px; ");

            if (real.Layout == PlanetaryFrameLayout.SplitCfa)
            {
                return await MakeColourTwin();
            }

            if (await MeasureReal(real, "") is not { } truth)
            {
                return 1;
            }

            // The disk's placement at the reference frame: the limb of a stack of the best frames, which the stacker aligns to
            // its own sharpest frame, carried onto the statistics' reference by that frame's shift.
            var stacked = await new LuckyImagingStacker().StackGlobalAsync(real, new PlanetaryStackOptions { KeepFraction = 0.05 }, ct);
            if (PlanetaryLimbFit.Fit(stacked.Master, PlanetaryLimbFit.OptionsFor(aspect)) is not { } limb)
            {
                consoleHost.WriteError($"{input}: the stack's limb could not be fitted");
                return 1;
            }
            var reference = new DiskPlacement(limb.CenterX - truth.ShiftX[stacked.ReferenceIndex], limb.CenterY - truth.ShiftY[stacked.ReferenceIndex],
                limb.EquatorialRadius, limb.NorthAngleDeg);
            var scale = aspect.AngularDiameterArcsec / 2 / limb.EquatorialRadius;

            var camera = truth.Camera;
            var gain = parseResult.GetValue(gainOpt) ?? PlanetaryDegrade.GainFor(camera.DiskLevel, truth.Noise[0].Disk, camera.FarSkyNoise);
            if (gain is not { } electronsPerAdu)
            {
                consoleHost.WriteError("the disk's finest band leaves no room for shot noise: pass --gain");
                return 1;
            }
            var options = WithCamera(Atmosphere(parseResult.GetValue(wavelengthOpt) * 1e-9, parseResult.GetValue(kOpt)), camera, electronsPerAdu);
            options = AtShownLevel(options, map, reference, scale, Path.GetFileName(output));
            if (options.HasHighLayer && (options.WarpRmsPx > 0 || parseResult.GetValue(psfTruthOpt) || !options.KeepScreenTilt))
            {
                consoleHost.WriteError($"{input}: a layer at an altitude makes the warp and each point's PSF itself: leave --warp-rms at 0, and --psf-truth and --replay-shifts off");
                return 1;
            }
            // The seeing's motion is the screen's own tilt, on the mount's slow drift; or the real shifts, replayed whole.
            var (moveX, moveY) = options.KeepScreenTilt ? (truth.MountX, truth.MountY) : (truth.ShiftX, truth.ShiftY);
            consoleHost.WriteScrollable($"making {Path.GetFileName(output)}: {Describe(options, reference, scale)}");

            var depth = camera.FullScaleAdu <= 255 ? 8 : 16;
            var partial = output + ".partial";
            var bytesPerSample = depth == 8 ? 1 : 2;
            ImmutableArray<SyntheticFrame> made;
            // A warp's truth goes beside the capture, frame by frame, for the dewarp to be scored against (R5), and on request each frame's
            // PSF, for the multi-frame bound (R8).
            var warpPartial = SyntheticWarpFile.PathFor(output) + ".partial";
            var psfPartial = SyntheticPsfFile.PathFor(output) + ".partial";
            var fieldPartial = SyntheticFieldFile.PathFor(output) + ".partial";
            var psfTruth = parseResult.GetValue(psfTruthOpt);
            var warped = options.WarpRmsPx > 0 || options.HasHighLayer;
            using (var warpWriter = warped ? new SyntheticWarpFile.Writer(warpPartial) : null)
            using (var psfWriter = psfTruth ? new SyntheticPsfFile.Writer(psfPartial, SyntheticPsfHeader.For(options, scale)) : null)
            using (var fieldWriter = options.HasHighLayer ? new SyntheticFieldFile.Writer(fieldPartial, PlanetaryDegrade.FieldPatchPx) : null)
            using (var writer = new SerWriter(partial, reader.Width, reader.Height, SerColorId.Mono, depth, instrument: "TianWen planetary-degrade"))
            {
                var buffer = new byte[reader.Width * reader.Height * bytesPerSample];
                var done = new Progress<int>(frames => { if (frames % 2048 < 64) { consoleHost.WriteScrollable($"    {frames} of {times.Length} frames"); } });
                made = await PlanetaryDegrade.MakeAsync(map, planet, times, reference, scale, moveX, moveY, truth.Flux, reader.Width, reader.Height, options,
                    (index, samples) => writer.AppendFrame(Pack(samples, buffer, depth), times[index]),
                    done, warpWriter is null ? null : (_, warp) => warpWriter.Append(warp), psfWriter is null ? null : (_, optics) => psfWriter.Append(optics),
                    fieldWriter is null ? null : (_, frame) => fieldWriter.Append(frame), ct);
            }
            File.Move(partial, output, overwrite: true);
            if (warped)
            {
                File.Move(warpPartial, SyntheticWarpFile.PathFor(output), overwrite: true);
            }
            if (options.HasHighLayer)
            {
                File.Move(fieldPartial, SyntheticFieldFile.PathFor(output), overwrite: true);
            }
            if (psfTruth)
            {
                File.Move(psfPartial, SyntheticPsfFile.PathFor(output), overwrite: true);
            }

            // The truth the synthetic capture is scored against: the same map at the reference frame's time (or the capture's
            // middle, --truth-at middle) through the pupil alone, in ADU over the sky.
            var referenceTime = truthAtMiddle ? middle : times[truth.ReferenceIndex];
            var referenceAspect = PhysicalEphemeris.Compute(planet, referenceTime);
            var truthImage = PlanetaryRender.RenderDiffracted(map, referenceAspect, reference, reader.Width, reader.Height, options.MinnaertK, pupil, options.WavelengthM, scale,
                moons: options.MoonsAt(planet, referenceTime), rings: options.Rings);
            WriteTruth(Path.ChangeExtension(output, ".truth.fits"), truthImage, reader.Width, reader.Height, reference, options, referenceTime, mapPath);
            WriteRecord(Path.ChangeExtension(output, ".frames.csv"), made);

            consoleHost.WriteScrollable($"measuring {Path.GetFileName(output)}");
            using var synthetic = SerFrameStream.Open(output);
            if (await PlanetaryCaptureStatistics.MeasureAsync(synthetic, measure, progress, ct) is not { } made2)
            {
                consoleHost.WriteError($"{output}: no disk found");
                return 1;
            }
            WriteStatistics(Path.GetFileName(input), truth);
            WriteStatistics(Path.GetFileName(output), made2);
            WriteComparison(truth, made2);
            return 0;

            // A colour capture's twin (R5a): each photosite colour measured on its own; the colours placed where their own
            // limbs are, which differ by the atmosphere's dispersion; one atmosphere and one motion, green's, for the three.
            async Task<int> MakeColourTwin()
            {
                var mapPaths = CommaList(parseResult.GetValue(bayerMapsOpt));
                if (parseResult.GetValue(psfTruthOpt))
                {
                    consoleHost.WriteError($"{input}: --psf-truth is for a mono capture; a colour one's three passes share no one PSF");
                    return 1;
                }
                if (parseResult.GetValue(highR0Opt) is not null)
                {
                    consoleHost.WriteError($"{input}: --high-r0 is for a mono capture (R4 per-point); a colour one records no per-point truth");
                    return 1;
                }
                if (mapPaths.Length != 3)
                {
                    consoleHost.WriteError($"{input}: a colour capture; pass --bayer-maps with its red, green and blue maps");
                    return 1;
                }
                var colourMaps = new PlanetMap[3];
                for (var c = 0; c < 3; c++)
                {
                    if (PlanetMap.ReadFits(mapPaths[c]) is not { } colourMap)
                    {
                        consoleHost.WriteError($"{mapPaths[c]}: no map");
                        return 1;
                    }
                    colourMaps[c] = planet == CatalogIndex.Saturn ? colourMap.FilledZonally() : colourMap;
                }
                var wavelengths = CommaNumbers(parseResult.GetValue(bayerWavelengthsOpt));
                var ks = parseResult.GetValue(bayerKOpt) is { } kText ? CommaNumbers(kText) : [parseResult.GetValue(kOpt), parseResult.GetValue(kOpt), parseResult.GetValue(kOpt)];
                if (wavelengths.Length != 3 || ks.Length != 3)
                {
                    consoleHost.WriteError("--bayer-wavelengths and --bayer-k take three numbers each, red, green and blue");
                    return 1;
                }
                // The colours are held until the last is made: a synthetic capture this size is made in parts.
                if ((long)times.Length * reader.Width * reader.Height * 2 > 2L << 30)
                {
                    consoleHost.WriteError($"{times.Length} frames of {reader.Width} x {reader.Height} are more than a colour twin holds at once: pass --frames");
                    return 1;
                }
                var (_, ox, oy) = reader.ColorId.ToSensorType();
                using var redPlane = new CfaPlaneStream(real, CfaPlaneStream.Red);
                using var greenPlane = new CfaPlaneStream(real, CfaPlaneStream.Green1);
                using var bluePlane = new CfaPlaneStream(real, CfaPlaneStream.Blue);
                if (await MeasureReal(redPlane, "r") is not { } redTruth || await MeasureReal(greenPlane, "g") is not { } greenTruth
                    || await MeasureReal(bluePlane, "b") is not { } blueTruth)
                {
                    return 1;
                }

                // Green's placement, found as the mono path finds a disk's, on its plane: the limb of a stack of the best frames,
                // carried onto the statistics' reference by that frame's shift.
                var greenStack = await new LuckyImagingStacker().StackGlobalAsync(greenPlane, new PlanetaryStackOptions { KeepFraction = 0.05 }, ct);
                var limbOptions = PlanetaryLimbFit.OptionsFor(aspect);
                if (PlanetaryLimbFit.Fit(greenStack.Master, limbOptions) is not { } greenLimb)
                {
                    consoleHost.WriteError($"{input}: the green stack's limb could not be fitted");
                    return 1;
                }
                var (gx, gy) = (greenLimb.CenterX - greenTruth.ShiftX[greenStack.ReferenceIndex], greenLimb.CenterY - greenTruth.ShiftY[greenStack.ReferenceIndex]);

                // The dispersion: the four planes stacked on ONE registration, each colour's limb against green's in the same stack.
                var planes = await new LuckyImagingStacker().StackPlanesAsync(real, [.. Enumerable.Range(0, real.FrameCount)], greenStack.ReferenceIndex, whiten: false, ct);
                (double X, double Y)? Offset(int channel)
                {
                    var ownFit = PlanetaryLimbFit.Fit(planes.ChannelImage(channel), limbOptions);
                    var greenFit = PlanetaryLimbFit.Fit(planes.ChannelImage(CfaPlaneStream.Green1), limbOptions);
                    return ownFit is { } of && greenFit is { } gf ? (of.CenterX - gf.CenterX, of.CenterY - gf.CenterY) : null;
                }
                var redOffset = Offset(CfaPlaneStream.Red);
                var blueOffset = Offset(CfaPlaneStream.Blue);
                planes.Release();
                if (redOffset is not { } dr || blueOffset is not { } db)
                {
                    consoleHost.WriteError($"{input}: a colour plane's limb could not be fitted");
                    return 1;
                }

                // On the sensor: a plane's pixel (i, j) is the photosite (2i + px, 2j + py) of its colour.
                DiskPlacement OnSensor(double x, double y, int channel)
                {
                    var (px, py) = CfaPlaneStream.PhaseOf(channel, ox, oy);
                    return new DiskPlacement((2 * x) + px, (2 * y) + py, 2 * greenLimb.EquatorialRadius, greenLimb.NorthAngleDeg);
                }
                var greenPlacement = OnSensor(gx, gy, CfaPlaneStream.Green1);
                var redPlacement = OnSensor(gx + dr.X, gy + dr.Y, CfaPlaneStream.Red);
                var bluePlacement = OnSensor(gx + db.X, gy + db.Y, CfaPlaneStream.Blue);
                var sensorScale = aspect.AngularDiameterArcsec / 2 / greenPlacement.EquatorialRadius;
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"the colours' dispersion, sensor px from green: red {redPlacement.CenterX - greenPlacement.CenterX:+0.00;-0.00}, {redPlacement.CenterY - greenPlacement.CenterY:+0.00;-0.00}; blue {bluePlacement.CenterX - greenPlacement.CenterX:+0.00;-0.00}, {bluePlacement.CenterY - greenPlacement.CenterY:+0.00;-0.00}"));

                // Each colour's own gain, read on its own noise as the mono path reads one: a colour camera's white balance is a
                // digital gain applied before its 8 bits, so a colour's electrons an ADU are the sensor's over its balance. On
                // 2024-12-15 blue carries 0.93 of green's finest noise at 0.64 of its level, and green's gain gave its twin 0.86 of it.
                double? GainOf(CaptureStatistics statistics) => parseResult.GetValue(gainOpt)
                    ?? PlanetaryDegrade.GainFor(statistics.Camera.DiskLevel, statistics.Noise[0].Disk, statistics.Camera.FarSkyNoise);
                if (GainOf(redTruth) is not { } redGain || GainOf(greenTruth) is not { } greenGain || GainOf(blueTruth) is not { } blueGain)
                {
                    consoleHost.WriteError("a colour's finest band leaves no room for shot noise: pass --gain");
                    return 1;
                }
                var red = new BayerColour(colourMaps[0], redPlacement,
                    AtShownLevel(WithCamera(Atmosphere(wavelengths[0] * 1e-9, ks[0]) with { Rings = colourRings[0] }, redTruth.Camera, redGain), colourMaps[0], redPlacement, sensorScale, "red"));
                var green = new BayerColour(colourMaps[1], greenPlacement,
                    AtShownLevel(WithCamera(Atmosphere(wavelengths[1] * 1e-9, ks[1]) with { Rings = colourRings[1] }, greenTruth.Camera, greenGain), colourMaps[1], greenPlacement, sensorScale, "green"));
                var blue = new BayerColour(colourMaps[2], bluePlacement,
                    AtShownLevel(WithCamera(Atmosphere(wavelengths[2] * 1e-9, ks[2]) with { Rings = colourRings[2] }, blueTruth.Camera, blueGain), colourMaps[2], bluePlacement, sensorScale, "blue"));
                foreach (var (name, colour) in new[] { ("red", red), ("green", green), ("blue", blue) })
                {
                    consoleHost.WriteScrollable($"making {Path.GetFileName(output)}, {name}: {Describe(colour.Options, colour.Placement, sensorScale)}");
                }

                // One motion, green's, in the sensor's pixels.
                var (planeMoveX, planeMoveY) = green.Options.KeepScreenTilt ? (greenTruth.MountX, greenTruth.MountY) : (greenTruth.ShiftX, greenTruth.ShiftY);
                ImmutableArray<double> colourMoveX = [.. planeMoveX.Select(v => 2 * v)];
                ImmutableArray<double> colourMoveY = [.. planeMoveY.Select(v => 2 * v)];
                var colourDepth = greenTruth.Camera.FullScaleAdu <= 255 ? 8 : 16;
                var colourPartial = output + ".partial";
                var colourWarpPartial = SyntheticWarpFile.PathFor(output) + ".partial";
                (ImmutableArray<SyntheticFrame> Red, ImmutableArray<SyntheticFrame> Green, ImmutableArray<SyntheticFrame> Blue) madeColours;
                using (var warpWriter = green.Options.WarpRmsPx > 0 ? new SyntheticWarpFile.Writer(colourWarpPartial) : null)
                using (var writer = new SerWriter(colourPartial, reader.Width, reader.Height, reader.ColorId, colourDepth, instrument: "TianWen planetary-degrade"))
                {
                    var buffer = new byte[reader.Width * reader.Height * (colourDepth == 8 ? 1 : 2)];
                    var done = new Progress<int>(frames => { if (frames % 2048 < 64) { consoleHost.WriteScrollable($"    {frames} of {times.Length} frames"); } });
                    madeColours = await PlanetaryDegrade.MakeBayerAsync(planet, times, red, green, blue, sensorScale, colourMoveX, colourMoveY, greenTruth.Flux,
                        reader.Width, reader.Height, ox, oy, (index, samples) => writer.AppendFrame(Pack(samples, buffer, colourDepth), times[index]),
                        done, warpWriter is null ? null : (_, warp) => warpWriter.Append(warp), ct);
                }
                File.Move(colourPartial, output, overwrite: true);
                if (green.Options.WarpRmsPx > 0)
                {
                    File.Move(colourWarpPartial, SyntheticWarpFile.PathFor(output), overwrite: true);
                }

                // A truth for each colour, each through the pupil alone at its own wavelength, where its own disk is, at the
                // reference frame's time or the capture's middle.
                var colourTime = truthAtMiddle ? middle : times[greenTruth.ReferenceIndex];
                var colourAspect = PhysicalEphemeris.Compute(planet, colourTime);
                foreach (var (name, colour, path) in new[] { ("r", red, mapPaths[0]), ("g", green, mapPaths[1]), ("b", blue, mapPaths[2]) })
                {
                    var render = PlanetaryRender.RenderDiffracted(colour.Map, colourAspect, colour.Placement, reader.Width, reader.Height, colour.Options.MinnaertK, pupil,
                        colour.Options.WavelengthM, sensorScale, moons: colour.Options.MoonsAt(planet, colourTime), rings: colour.Options.Rings);
                    WriteTruth(Path.ChangeExtension(output, $".truth.{name}.fits"), render, reader.Width, reader.Height, colour.Placement, colour.Options, colourTime, path);
                    // At a drizzle's scale: rendered there, never the 1x truth resampled (the plan's rule for R5a).
                    foreach (var upsample in CommaNumbers(parseResult.GetValue(truthUpsampleOpt)))
                    {
                        var (w, h) = ((int)Math.Round(reader.Width * upsample), (int)Math.Round(reader.Height * upsample));
                        var at = colour.Placement with
                        {
                            CenterX = ((colour.Placement.CenterX + 0.5) * upsample) - 0.5,
                            CenterY = ((colour.Placement.CenterY + 0.5) * upsample) - 0.5,
                            EquatorialRadius = colour.Placement.EquatorialRadius * upsample,
                        };
                        var fine = PlanetaryRender.RenderDiffracted(colour.Map, colourAspect, at, w, h, colour.Options.MinnaertK, pupil, colour.Options.WavelengthM, sensorScale / upsample,
                            moons: colour.Options.MoonsAt(planet, colourTime), rings: colour.Options.Rings);
                        WriteTruth(Path.ChangeExtension(output, string.Create(CultureInfo.InvariantCulture, $".truth.{name}.x{upsample:0.##}.fits")), fine, w, h, at, colour.Options, colourTime, path);
                    }
                }
                WriteRecord(Path.ChangeExtension(output, ".frames.csv"), madeColours.Green);

                // Each colour of the twin measured as its real one was.
                using var colourTwin = SerFrameStream.Open(output);
                foreach (var (name, channel, realOne) in new[] { ("r", CfaPlaneStream.Red, redTruth), ("g", CfaPlaneStream.Green1, greenTruth), ("b", CfaPlaneStream.Blue, blueTruth) })
                {
                    consoleHost.WriteScrollable($"measuring {Path.GetFileName(output)} ({name})");
                    using var twinPlane = new CfaPlaneStream(colourTwin, channel);
                    if (await PlanetaryCaptureStatistics.MeasureAsync(twinPlane, measure, progress, ct) is not { } twinOne)
                    {
                        consoleHost.WriteError($"{output}: no disk found in its {name} photosites");
                        return 1;
                    }
                    WriteStatistics($"{Path.GetFileName(input)} ({name})", realOne);
                    WriteStatistics($"{Path.GetFileName(output)} ({name})", twinOne);
                    WriteComparison(realOne, twinOne);
                }
                return 0;
            }
        });
        return command;
    }

    // A frame's samples as the SER's bytes: one a sample at 8 bits, two (little-endian) at 16.
    private static byte[] Pack(ushort[] samples, byte[] buffer, int depth)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            if (depth == 8)
            {
                buffer[i] = (byte)samples[i];
            }
            else
            {
                BitConverter.TryWriteBytes(buffer.AsSpan(2 * i, 2), samples[i]);
            }
        }
        return buffer;
    }

    // A comma list, its entries trimmed and the empty ones dropped.
    private static string[] CommaList(string? text) => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double[] CommaNumbers(string? text) => [.. CommaList(text).Select(t => double.Parse(t, CultureInfo.InvariantCulture))];

    // The truth, scaled as the frames are (ADU over the sky), with the geometry it was rendered at in its header.
    private static void WriteTruth(string path, float[] render, int width, int height, DiskPlacement placement, DegradeOptions options, DateTimeOffset utc, string mapPath)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - placement.CenterX;
                var dy = y - placement.CenterY;
                if ((dx * dx) + (dy * dy) < 0.64 * placement.EquatorialRadius * placement.EquatorialRadius)
                {
                    sum += render[(y * width) + x];
                    count++;
                }
            }
        }
        var gain = count > 0 && sum > 0 ? options.DiskLevelAdu / (sum / count) : 1;
        var plane = new float[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = (float)(render[(y * width) + x] * gain);
            }
        }
        var headers = new Dictionary<string, (object Value, string Comment)>
        {
            ["DATE-OBS"] = (utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "the reference frame's time"),
            ["DISKX"] = (placement.CenterX, "disk centre x, px (0-based)"),
            ["DISKY"] = (placement.CenterY, "disk centre y, px (0-based)"),
            ["DISKR"] = (placement.EquatorialRadius, "equatorial radius, px"),
            ["NORTHANG"] = (placement.NorthAngleDeg, "direction to the north pole, deg from +x toward +y"),
            ["WAVELEN"] = (options.WavelengthM * 1e9, "the wavelength imaged, nm"),
            ["SRCMAP"] = (Path.GetFileName(mapPath), "the global map rendered"),
        };
        Image.FromChannel(plane).WriteToFitsFile(path, null, headers);
    }

    // Each frame's shift and Strehl ratio, the truth frame selection is judged against.
    private static void WriteRecord(string path, ImmutableArray<SyntheticFrame> frames)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("frame,shift_x,shift_y,strehl");
        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},{f.ShiftX:0.0000},{f.ShiftY:0.0000},{f.Strehl:0.00000}"));
        }
    }

    // The plan's five statistics side by side, each as the synthetic's over the real's (R2's pre-registration: within 10 %).
    private void WriteComparison(CaptureStatistics real, CaptureStatistics synthetic)
    {
        var inv = CultureInfo.InvariantCulture;
        void Row(string name, double a, double b) => consoleHost.WriteScrollable(string.Create(inv,
            $"    {name,-34} real {a,10:0.0000}  synthetic {b,10:0.0000}  ratio {b / a,6:0.000}{(Math.Abs((b / a) - 1) <= 0.1 ? "" : "  OUTSIDE 10 %")}"));
        consoleHost.WriteScrollable("comparison (synthetic over real):");
        Row("shift RMS, seeing part (px)", real.SeeingRms, synthetic.SeeingRms);
        Row("the same by the limb (px)", real.LimbSeeingRms, synthetic.LimbSeeingRms);
        Row("aligner's error against the limb (px)", real.AlignerErrorRms, synthetic.AlignerErrorRms);
        Row("single frames' radius RMS (px)", real.LimbRadiusRms, synthetic.LimbRadiusRms);
        Row("limb edge width, every frame (px)", real.LimbWidthAll, synthetic.LimbWidthAll);
        Row("limb edge width, best tenth (px)", real.LimbWidthBest, synthetic.LimbWidthBest);
        if (real.LimbAll is { } ra && synthetic.LimbAll is { } sa)
        {
            Row("limb darkening k, every frame", ra.LimbDarkening, sa.LimbDarkening);
            Row("limb blur sigma, every frame (px)", ra.PsfSigma, sa.PsfSigma);
            Row("limb blur wing's share, every frame", ra.HaloFraction, sa.HaloFraction);
            // Saturn's rings, each over the globe's brightness as the ringed limb fit reads them (S3).
            if (ra.RingLevels is { } realRings && sa.RingLevels is { } twinRings && realRings.Length == twinRings.Length)
            {
                for (var i = 0; i < realRings.Length; i++)
                {
                    Row($"ring {SaturnRings.Main.Rings[i].Name} over the globe", realRings[i], twinRings[i]);
                }
            }
        }
        if (real.LimbBest is { } rb && synthetic.LimbBest is { } sb)
        {
            Row("limb blur sigma, best tenth (px)", rb.PsfSigma, sb.PsfSigma);
        }
        var (realFrames, syntheticFrames) = (FrameLimbPercentiles(real), FrameLimbPercentiles(synthetic));
        if (realFrames is { } rf && syntheticFrames is { } sf)
        {
            Row("single frames' edge width, p10 (px)", rf.Width[0], sf.Width[0]);
            Row("single frames' edge width, p50 (px)", rf.Width[1], sf.Width[1]);
            Row("single frames' edge width, p90 (px)", rf.Width[2], sf.Width[2]);
            Row("single frames' blur sigma, p10 (px)", rf.Sigma[0], sf.Sigma[0]);
            Row("single frames' blur sigma, p50 (px)", rf.Sigma[1], sf.Sigma[1]);
            Row("single frames' blur sigma, p90 (px)", rf.Sigma[2], sf.Sigma[2]);
        }
        for (var j = 0; j < real.Halo.Length; j++)
        {
            Row($"halo {PlanetaryCaptureStatistics.HaloAnnuli[j]:0.0#} to {PlanetaryCaptureStatistics.HaloAnnuli[j + 1]:0.0#} radii (ADU)", real.Halo[j], synthetic.Halo[j]);
        }
        Row("disk level over the local sky (ADU)", real.Camera.DiskLevel, synthetic.Camera.DiskLevel);
        Row("flux, quarter-second RMS", real.FluxSlowRms, synthetic.FluxSlowRms);
        Row("flux, frame to frame RMS", real.FluxFastRms, synthetic.FluxFastRms);
        if (real.Warp.Bound == WarpLengthBound.Measured && synthetic.Warp.Bound == WarpLengthBound.Measured)
        {
            Row("warp correlation length (px)", real.Warp.CorrelationLength, synthetic.Warp.CorrelationLength);
        }
        else
        {
            consoleHost.WriteScrollable(string.Create(inv,
                $"    {"warp correlation length (px)",-34} not measured: real {real.Warp.Bound} {real.Warp.CorrelationLength:0.0}, synthetic {synthetic.Warp.Bound} {synthetic.Warp.CorrelationLength:0.0}"));
        }
        Row("warp RMS (px)", real.Warp.Rms, synthetic.Warp.Rms);
        for (var i = 0; i < PlanetaryCaptureStatistics.Percentiles.Length; i++)
        {
            if (i == 2)
            {
                continue;
            }
            Row($"quality p{PlanetaryCaptureStatistics.Percentiles[i]:0} over median", real.QualityPercentiles[i] / real.QualityPercentiles[2], synthetic.QualityPercentiles[i] / synthetic.QualityPercentiles[2]);
        }
        Row("quality median (absolute)", real.QualityPercentiles[2], synthetic.QualityPercentiles[2]);
        Row("quality lag-1", real.QualityLag1, synthetic.QualityLag1);
        for (var j = 0; j < real.Noise.Length; j++)
        {
            Row($"noise band {j + 1}, sky (ADU)", real.Noise[j].Sky, synthetic.Noise[j].Sky);
            Row($"noise band {j + 1}, disk (ADU)", real.Noise[j].Disk, synthetic.Noise[j].Disk);
        }
    }

    // The single frames' limb edge widths and fitted blur sigmas at their 10th, 50th and 90th percentiles; null with no frames.
    private static (ImmutableArray<double> Width, ImmutableArray<double> Sigma, double K)? FrameLimbPercentiles(CaptureStatistics s)
    {
        var widths = s.FrameLimbs.Select(f => f.EdgeWidth).Where(double.IsFinite).ToArray();
        var fits = s.FrameLimbs.Select(f => f.Fit).OfType<LimbFit>().ToArray();
        if (widths.Length == 0 || fits.Length == 0)
        {
            return null;
        }
        ImmutableArray<double> tenths = [10, 50, 90];
        return (PlanetaryCaptureStatistics.PercentilesOf(widths, tenths), PlanetaryCaptureStatistics.PercentilesOf(fits.Select(f => f.PsfSigma).ToArray(), tenths),
            PlanetaryCaptureStatistics.PercentilesOf(fits.Select(f => f.LimbDarkening).ToArray(), [50])[0]);
    }

    private void WriteStatistics(string name, CaptureStatistics s)
    {
        var inv = CultureInfo.InvariantCulture;
        var seconds = (s.Frames - 1) / s.FramesPerSecond;
        consoleHost.WriteScrollable(string.Create(inv,
            $"{name}: {s.Frames} frames over {seconds:0.0} s ({s.FramesPerSecond:0.0} fps), the sharpest {s.ReferenceIndex}, disk at {s.DiskX:0.0}, {s.DiskY:0.0}, R {s.DiskRadius:0.0} px"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    shift: seeing {s.SeeingRms:0.000} px RMS per axis; mount {s.MountRate:0.000} px/s, wandering {s.MountWander:0.000} px about its line; " +
            $"flux {100 * s.FluxSlowRms:0.00} % RMS over quarter seconds, {100 * s.FluxFastRms:0.000} % frame to frame"));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    limb edge width: {s.LimbWidthAll:0.000} px in the mean of every frame aligned, {s.LimbWidthBest:0.000} px in the best tenth's"));
        foreach (var (which, fit) in new[] { ("every frame", s.LimbAll), ("best tenth", s.LimbBest) })
        {
            // Saturn's rings, each over the globe's brightness (S3).
            var rings = fit is { RingLevels: { } levels } ? $"; rings {string.Join(", ", levels.Select(l => l.ToString("0.000", inv)))} of the globe" : "";
            consoleHost.WriteScrollable(fit is { } f
                ? string.Create(inv, $"    limb fit, {which}: R {f.EquatorialRadius:0.000} px, k {f.LimbDarkening:0.000}, blur sigma {f.PsfSigma:0.000} px with {100 * f.HaloFraction:0} % in a wing of {f.HaloWidth:0.0} px, rms {f.RmsResidual:0.00000}{rings}")
                : $"    limb fit, {which}: no disk");
        }
        if (FrameLimbPercentiles(s) is { } frames)
        {
            consoleHost.WriteScrollable(string.Create(inv,
                $"    single frames' limbs ({s.FrameLimbs.Length}): edge width p10 {frames.Width[0]:0.000}, p50 {frames.Width[1]:0.000}, p90 {frames.Width[2]:0.000} px; " +
                $"blur sigma p10 {frames.Sigma[0]:0.000}, p50 {frames.Sigma[1]:0.000}, p90 {frames.Sigma[2]:0.000} px; k {frames.K:0.000}"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    by the single frames' limbs: seeing {s.LimbSeeingRms:0.000} px RMS per axis; the aligner off by {s.AlignerErrorRms:0.000} px RMS per axis; " +
                $"radius {s.LimbRadiusRms:0.000} px RMS; {s.LimbOutliers} fits left out"));
        }
        var w = s.Warp;
        consoleHost.WriteScrollable(string.Create(inv,
            $"    warp: {w.Points} points, {w.Rms:0.000} px RMS per axis, correlation length {(w.Bound switch { WarpLengthBound.AtLeast => ">= ", WarpLengthBound.AtMost => "<= ", _ => "" })}{w.CorrelationLength:0.0} px, lag-1 {w.Lag1:0.000}"));
        consoleHost.WriteScrollable("    warp correlation: " + string.Join("  ", w.Curve.Select(b => string.Create(inv, $"{b.Separation:0}px {b.Correlation:+0.00;-0.00} ({b.Pairs})"))));
        var p50 = s.QualityPercentiles[2];
        consoleHost.WriteScrollable(string.Create(inv,
            $"    quality: median {p50:0.000000}; p5 {s.QualityPercentiles[0] / p50:0.000}, p25 {s.QualityPercentiles[1] / p50:0.000}, p75 {s.QualityPercentiles[3] / p50:0.000}, p95 {s.QualityPercentiles[4] / p50:0.000} of it; lag-1 {s.QualityLag1:0.000}"));
        consoleHost.WriteScrollable("    noise (ADU, one frame): " + string.Join("  ", s.Noise.Select(b => string.Create(inv, $"band {b.Band} sky {b.Sky:0.000} disk {b.Disk:0.000}"))));
        var c = s.Camera;
        consoleHost.WriteScrollable("    halo over the local sky: " + string.Join(", ", s.Halo.Select((h, j) =>
            string.Create(inv, $"{h:+0.000;-0.000} ADU at {PlanetaryCaptureStatistics.HaloAnnuli[j]:0.0#} to {PlanetaryCaptureStatistics.HaloAnnuli[j + 1]:0.0#} radii"))));
        consoleHost.WriteScrollable(string.Create(inv,
            $"    camera: sky {c.SkyLevel:0.000} ADU with {c.SkyNoise:0.000} noise at 1.3 to 1.6 radii, {c.FarSkyLevel:0.000} with {c.FarSkyNoise:0.000} beyond 3 and {c.LocalSkyLevel:0.000} at 2.5 to 3.5 before rounding; disk {c.DiskLevel:0.0} over the local sky, full scale {c.FullScaleAdu:0}"));
    }

    // The corpus' pupils: the Newtonian's 58 mm secondary is its specification's; the Maksutov's spot is not confirmed.
    internal static readonly Pupil NewtonianPupil = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001);
    internal static readonly Pupil MaksutovPupil = new Pupil(MaksutovApertureM, ObstructionRatio: 0.3);

    public Command BuildRenderTruth()
    {
        var mapOpt = new Option<string>("--map") { Description = "The global map (FITS; OPAL's, planetographic latitude, west longitude, north first).", Required = true };
        var utcOpt = new Option<string>("--utc") { Description = "The instant to render (ISO 8601, UTC).", Required = true };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var likeOpt = new Option<string?>("--like") { Description = "An image whose disk to render at: its size, and the limb fit's centre, radius and north end." };
        var centerOpt = new Option<string?>("--center") { Description = "The disk's centre, x,y in pixels (without --like)." };
        var radiusOpt = new Option<double?>("--radius") { Description = "The equatorial radius in pixels (without --like)." };
        var northOpt = new Option<double?>("--north") { Description = "The direction to the north pole, degrees from +x toward +y (overrides --like's)." };
        var sizeOpt = new Option<string?>("--size") { Description = "The frame, WxH (without --like)." };
        var mirroredOpt = new Option<bool>("--mirrored") { Description = "The image is the sky's mirror image (east to the right of north)." };
        var kOpt = new Option<double>("--k") { Description = "Minnaert's exponent for the map's filter (OPAL's readme lists it).", DefaultValueFactory = _ => 0.95 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian, maksutov or none (no diffraction).", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's wavelength in nm, for the diffraction.", DefaultValueFactory = _ => 550 };
        var seeingFwhmOpt = new Option<double?>("--seeing-fwhm") { Description = "Blur the truth by a Moffat of this FWHM in pixels, as seeing would." };
        var seeingBetaOpt = new Option<double>("--seeing-beta") { Description = "The seeing Moffat's beta.", DefaultValueFactory = _ => 3 };
        var fitOpt = new Option<bool>("--fit") { Description = "Fit the render's limb and say how far the fit lands from the geometry rendered (T1)." };
        var upsampleOpt = new Option<double>("--upsample")
        {
            Description = "Render at this many pixels for each of the geometry's: the truth for a stack drizzled by the same factor (R5a).",
            DefaultValueFactory = _ => 1,
        };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Where to write the render (FITS)." };
        var noRingsOpt = new Option<bool>("--no-rings") { Description = "Render Saturn's globe alone, without its rings (S1, #1231)." };

        var command = new Command("planetary-render-truth",
            "Render a planet's global map at an instant and a disk's geometry, through the telescope's pupil: the truth the limb fit and the restoration are measured against (T1).")
        {
            Options = { mapOpt, utcOpt, planetOpt, likeOpt, centerOpt, radiusOpt, northOpt, sizeOpt, mirroredOpt, kOpt, telescopeOpt, wavelengthOpt, seeingFwhmOpt, seeingBetaOpt, fitOpt, upsampleOpt, outputOpt, noRingsOpt },
        };

        command.SetAction((parseResult, ct) =>
        {
            var mapPath = parseResult.GetValue(mapOpt) ?? "";
            if (PlanetMap.ReadFits(mapPath) is not { } map)
            {
                consoleHost.WriteError($"{mapPath}: no map");
                return Task.FromResult(1);
            }
            if (ParseUtc(parseResult.GetValue(utcOpt)) is not { } utc)
            {
                consoleHost.WriteError("--utc is not an ISO 8601 time");
                return Task.FromResult(1);
            }
            var planet = (parseResult.GetValue(planetOpt) ?? "jupiter").ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var aspect = PhysicalEphemeris.Compute(planet, utc);
            // Saturn's map holds zeros where the rings hid the globe from Hubble; its rings are drawn unless asked not to be.
            var rings = planet == CatalogIndex.Saturn && !parseResult.GetValue(noRingsOpt) ? SaturnRings.Main : null;
            if (planet == CatalogIndex.Saturn)
            {
                map = map.FilledZonally();
            }

            int width, height;
            DiskPlacement placement;
            if (parseResult.GetValue(likeOpt) is { } like)
            {
                if (!Image.TryReadImageFile(like, out var image) || PlanetaryLimbFit.Fit(image, PlanetaryLimbFit.OptionsFor(aspect)) is not { } fit)
                {
                    consoleHost.WriteError($"{like}: no disk to render like");
                    return Task.FromResult(1);
                }
                (width, height) = (image.Width, image.Height);
                placement = new DiskPlacement(fit.CenterX, fit.CenterY, fit.EquatorialRadius, fit.NorthAngleDeg, parseResult.GetValue(mirroredOpt));
            }
            else if (ParsePair(parseResult.GetValue(centerOpt)) is { } center && parseResult.GetValue(radiusOpt) is { } radius
                && ParsePair(parseResult.GetValue(sizeOpt), 'x') is { } size)
            {
                (width, height) = ((int)size.A, (int)size.B);
                placement = new DiskPlacement(center.A, center.B, radius, parseResult.GetValue(northOpt) ?? -90, parseResult.GetValue(mirroredOpt));
            }
            else
            {
                consoleHost.WriteError("give --like, or --center, --radius and --size");
                return Task.FromResult(1);
            }
            if (parseResult.GetValue(northOpt) is { } north)
            {
                placement = placement with { NorthAngleDeg = north };
            }
            // A finer grid over the same sky: pixel x spans [x - 0.5, x + 0.5], so its centre maps to (x + 0.5) f - 0.5.
            var upsample = parseResult.GetValue(upsampleOpt);
            if (upsample <= 0)
            {
                consoleHost.WriteError("--upsample must be positive");
                return Task.FromResult(1);
            }
            if (upsample != 1)
            {
                (width, height) = ((int)Math.Round(width * upsample), (int)Math.Round(height * upsample));
                placement = placement with
                {
                    CenterX = ((placement.CenterX + 0.5) * upsample) - 0.5,
                    CenterY = ((placement.CenterY + 0.5) * upsample) - 0.5,
                    EquatorialRadius = placement.EquatorialRadius * upsample,
                };
            }

            var scale = aspect.AngularDiameterArcsec / 2 / placement.EquatorialRadius;
            var k = parseResult.GetValue(kOpt);
            var wavelength = parseResult.GetValue(wavelengthOpt) * 1e-9;
            var truth = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() switch
            {
                "none" => PlanetaryRender.Render(map, aspect, placement, width, height, k, rings: rings),
                "maksutov" => PlanetaryRender.RenderDiffracted(map, aspect, placement, width, height, k, MaksutovPupil, wavelength, scale, rings: rings),
                _ => PlanetaryRender.RenderDiffracted(map, aspect, placement, width, height, k, NewtonianPupil, wavelength, scale, rings: rings),
            };
            var fwhm = parseResult.GetValue(seeingFwhmOpt);
            var seen = fwhm is { } f ? PsfKernel.Moffat(f, parseResult.GetValue(seeingBetaOpt)).Convolve(truth, width, height) : truth;

            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"{planet} at {utc:yyyy-MM-dd HH:mm:ss} UTC: CM III {aspect.CentralMeridianIII:0.00}, sub-observer latitude {aspect.SubObserverLatitude:0.00} ({aspect.SubObserverLatitudeCentric:0.00} planetocentric), phase {aspect.PhaseAngle:0.00}, pole at {aspect.PolePositionAngle:0.00}; " +
                $"disk at {placement.CenterX:0.000}, {placement.CenterY:0.000}, R {placement.EquatorialRadius:0.000} px ({scale:0.0000}\"/px), north at {placement.NorthAngleDeg:0.00} deg{(placement.Mirrored ? ", mirrored" : "")}"));

            if (parseResult.GetValue(fitOpt))
            {
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = seen[(y * width) + x];
                    }
                }
                if (PlanetaryLimbFit.Fit(Image.FromChannel(plane), PlanetaryLimbFit.OptionsFor(aspect)) is { } fitted)
                {
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"the limb fit: centre off {fitted.CenterX - placement.CenterX:+0.000;-0.000}, {fitted.CenterY - placement.CenterY:+0.000;-0.000} px, " +
                        $"radius {100 * (fitted.EquatorialRadius - placement.EquatorialRadius) / placement.EquatorialRadius:+0.00;-0.00} %, axis {fitted.AxisAngleDeg:0.00} deg, " +
                        $"k {fitted.LimbDarkening:0.00}, sigma {fitted.PsfSigma:0.00}, albedo at the poles {1 + fitted.ZonalAlbedo1 + fitted.ZonalAlbedo2 + fitted.ZonalAlbedo4:0.00} and {1 - fitted.ZonalAlbedo1 + fitted.ZonalAlbedo2 + fitted.ZonalAlbedo4:0.00}"));
                }
                else
                {
                    consoleHost.WriteError("the limb fit found no disk in the render");
                }
            }

            if (parseResult.GetValue(outputOpt) is { } output)
            {
                var plane = new float[height, width];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        plane[y, x] = seen[(y * width) + x];
                    }
                }
                var headers = new Dictionary<string, (object Value, string Comment)>
                {
                    ["DATE-OBS"] = (utc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "the instant rendered"),
                    ["CM3"] = (aspect.CentralMeridianIII, "central meridian, System III, deg"),
                    ["DISKX"] = (placement.CenterX, "disk centre x, px (0-based)"),
                    ["DISKY"] = (placement.CenterY, "disk centre y, px (0-based)"),
                    ["DISKR"] = (placement.EquatorialRadius, "equatorial radius, px"),
                    ["NORTHANG"] = (placement.NorthAngleDeg, "direction to the north pole, deg from +x toward +y"),
                    ["MINNAERT"] = (k, "Minnaert k put back"),
                    ["SRCMAP"] = (Path.GetFileName(mapPath), "the global map rendered"),
                };
                Image.FromChannel(plane).WriteToFitsFile(output, null, headers);
                consoleHost.WriteScrollable($"wrote {output}. OPAL maps are CC BY 4.0: credit the OPAL program (PI Simon, GO13937), doi 10.17909/T9G593.");
            }
            return Task.FromResult(0);
        });
        return command;
    }

    private static (double A, double B)? ParsePair(string? text, char separator = ',')
    {
        var parts = text?.Split(separator);
        return parts is [var a, var b]
            && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : null;
    }

    // A photosite colour's channel of a split-CFA frame, by the name a verb takes it by; null for none or an unknown name.
    internal static int? ParsePlane(string? name) => name?.ToLowerInvariant() switch
    {
        "r" or "red" => CfaPlaneStream.Red,
        "g" or "g1" or "green" => CfaPlaneStream.Green1,
        "g2" => CfaPlaneStream.Green2,
        "b" or "blue" => CfaPlaneStream.Blue,
        _ => null,
    };

    internal static DateTimeOffset? ParseUtc(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
