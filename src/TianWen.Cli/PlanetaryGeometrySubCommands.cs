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

        var command = new Command("planetary-seeing",
            "A capture's statistics, measured as a synthetic capture's are (R2): the shift's seeing and mount parts, the warp, the quality distribution, each band's noise, the camera's levels and gain.")
        {
            Arguments = { inputsArg },
            Options = { planetOpt, utcOpt, fpsOpt, pairsOpt, warpFramesOpt, patchOpt, spacingOpt, firstOpt, framesOpt, plainOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var failed = 0;
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            foreach (var input in parseResult.GetValue(inputsArg) ?? [])
            {
                ct.ThrowIfCancellationRequested();
                using var reader = SerReader.Open(input);
                using var whole = new SerFrameStream(reader, ownsReader: false);
                var first = Math.Clamp(parseResult.GetValue(firstOpt), 0, whole.FrameCount - 1);
                using var stream = new PlanetaryFrameWindow(whole, first, Math.Min(whole.FrameCount - first, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
                if ((MidCapture(stream) ?? ParseUtc(parseResult.GetValue(utcOpt))) is not { } when)
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
        var realStatisticsOpt = new Option<string?>("--real-statistics") { Description = "A file the real capture's statistics are read from when it holds them for this capture, frames and options, and saved to otherwise." };
        var localR0Opt = new Option<double?>("--local-r0") { Description = "A layer of turbulence at the telescope (tube, mirror), its Fried parameter at 500 nm, cm (none by default)." };
        var localOuterScaleOpt = new Option<double>("--local-outer-scale") { Description = "The local layer's outer scale, m.", DefaultValueFactory = _ => 0.25 };
        var localWindOpt = new Option<double>("--local-wind") { Description = "The local layer's drift across the pupil, m/s.", DefaultValueFactory = _ => 1 };
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

        var command = new Command("planetary-degrade",
            "A synthetic capture from a global map with a real capture's own seeing, motion and camera (R2): measure the real one, make the synthetic one, measure it the same way, and compare.")
        {
            Arguments = { inputArg },
            Options = { mapOpt, outputOpt, planetOpt, kOpt, telescopeOpt, wavelengthOpt, r0Opt, windOpt, outerScaleOpt, exposureOpt, defocusOpt, localR0Opt, localOuterScaleOpt, localWindOpt, scatterOpt, scatterCoreOpt, realStatisticsOpt, gainOpt, warpRmsOpt, warpLengthOpt, warpLagOpt, seedOpt, replayOpt, pairsOpt, warpFramesOpt, patchOpt, spacingOpt, plainOpt, framesOpt },
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

            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            var frames = Math.Min(parseResult.GetValue(framesOpt) ?? whole.FrameCount, whole.FrameCount);
            using var real = new PlanetaryFrameWindow(whole, 0, frames);
            if (MidCapture(real) is not { } mid || reader.Timestamps is not { IsDefaultOrEmpty: false } allTimes)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var times = allTimes[..frames];
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
            // A real capture measured once serves every synthetic one compared with it.
            var statisticsPath = parseResult.GetValue(realStatisticsOpt);
            var key = $"{Path.GetFullPath(input)} | {real.FrameCount} frames | {measure}";
            var truth = statisticsPath is null ? null : await PlanetaryCaptureStatistics.TryLoadAsync(statisticsPath, key, ct);
            if (truth is not null)
            {
                consoleHost.WriteScrollable($"{Path.GetFileName(input)}: statistics read from {statisticsPath}");
            }
            else
            {
                consoleHost.WriteScrollable($"measuring {Path.GetFileName(input)}");
                truth = await PlanetaryCaptureStatistics.MeasureAsync(real, measure, progress, ct);
                if (truth is null)
                {
                    consoleHost.WriteError($"{input}: no disk found");
                    return 1;
                }
                if (statisticsPath is not null)
                {
                    await PlanetaryCaptureStatistics.SaveAsync(truth, key, statisticsPath, ct);
                }
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
            // The camera's own terms from the far sky, where the planet's scattered light has gone (the synthetic capture scatters
            // its own light into the ring), as they were before the camera rounded them.
            var readNoise = camera.FarSkyNoise;
            var gain = parseResult.GetValue(gainOpt) ?? PlanetaryDegrade.GainFor(camera.DiskLevel, truth.Noise[0].Disk, readNoise);
            if (gain is not { } electronsPerAdu)
            {
                consoleHost.WriteError("the disk's finest band leaves no room for shot noise: pass --gain");
                return 1;
            }
            var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? MaksutovPupil : NewtonianPupil;
            var options = new DegradeOptions(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9)
            {
                R0M = parseResult.GetValue(r0Opt) / 100,
                WindMps = parseResult.GetValue(windOpt),
                OuterScaleM = parseResult.GetValue(outerScaleOpt) ?? double.PositiveInfinity,
                ExposureSeconds = parseResult.GetValue(exposureOpt) / 1000,
                DefocusNm = parseResult.GetValue(defocusOpt),
                LocalR0M = parseResult.GetValue(localR0Opt) / 100 ?? double.PositiveInfinity,
                LocalOuterScaleM = parseResult.GetValue(localOuterScaleOpt),
                LocalWindMps = parseResult.GetValue(localWindOpt),
                ScatterFraction = parseResult.GetValue(scatterOpt),
                ScatterCoreArcsec = parseResult.GetValue(scatterCoreOpt),
                MinnaertK = parseResult.GetValue(kOpt),
                FullScaleAdu = camera.FullScaleAdu,
                OffsetAdu = camera.LocalSkyLevel,
                ReadNoiseAdu = readNoise,
                ElectronsPerAdu = electronsPerAdu,
                DiskLevelAdu = camera.DiskLevel,
                WarpRmsPx = parseResult.GetValue(warpRmsOpt),
                WarpLengthPx = parseResult.GetValue(warpLengthOpt),
                WarpLag1 = parseResult.GetValue(warpLagOpt),
                Seed = parseResult.GetValue(seedOpt),
                KeepScreenTilt = !parseResult.GetValue(replayOpt),
            };
            // The seeing's motion is the screen's own tilt, on the mount's slow drift; or the real shifts, replayed whole.
            var (moveX, moveY) = options.KeepScreenTilt ? (truth.MountX, truth.MountY) : (truth.ShiftX, truth.ShiftY);
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"making {Path.GetFileName(output)}: disk at {reference.CenterX:0.00}, {reference.CenterY:0.00}, R {reference.EquatorialRadius:0.00} px ({scale:0.0000}\"/px), north {reference.NorthAngleDeg:0.0} deg; " +
                $"r0 {options.R0M * 100:0.0} cm at 500 nm, outer scale {(double.IsPositiveInfinity(options.OuterScaleM) ? "none" : $"{options.OuterScaleM:0.#} m")}, wind {options.WindMps:0} m/s, exposure {options.ExposureSeconds * 1000:0.#} ms, defocus {options.DefocusNm:0} nm RMS, {(double.IsFinite(options.LocalR0M) ? $"a local layer of r0 {options.LocalR0M * 100:0.0} cm, outer scale {options.LocalOuterScaleM:0.00} m, drifting {options.LocalWindMps:0.#} m/s, " : "")}{(options.ScatterFraction > 0 ? $"{options.ScatterFraction * 100:0.##} % scattered with a core of {options.ScatterCoreArcsec:0.#}\", " : "")}{options.WavelengthM * 1e9:0} nm, oversampled {PlanetaryDegrade.OversampleFor(scale, pupil.DiameterM, options.WavelengthM)}x; " +
                $"camera offset {options.OffsetAdu:0.00}, read noise {options.ReadNoiseAdu:0.000} ADU, {options.ElectronsPerAdu:0.0} e-/ADU, disk {options.DiskLevelAdu:0.0} ADU; warp {options.WarpRmsPx:0.00} px; " +
                $"{(options.KeepScreenTilt ? "the screen's tilt on the mount's drift" : "the real shifts replayed")}"));

            var depth = camera.FullScaleAdu <= 255 ? 8 : 16;
            var partial = output + ".partial";
            var bytesPerSample = depth == 8 ? 1 : 2;
            ImmutableArray<SyntheticFrame> made;
            using (var writer = new SerWriter(partial, reader.Width, reader.Height, SerColorId.Mono, depth, instrument: "TianWen planetary-degrade"))
            {
                var buffer = new byte[reader.Width * reader.Height * bytesPerSample];
                var done = new Progress<int>(frames => { if (frames % 2048 < 64) { consoleHost.WriteScrollable($"    {frames} of {times.Length} frames"); } });
                made = await PlanetaryDegrade.MakeAsync(map, planet, times, reference, scale, moveX, moveY, truth.Flux, reader.Width, reader.Height, options, (index, samples) =>
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
                    writer.AppendFrame(buffer, times[index]);
                }, done, ct);
            }
            File.Move(partial, output, overwrite: true);

            // The truth the synthetic capture is scored against: the same map at the reference frame's time through the pupil
            // alone, in ADU over the sky.
            var referenceTime = times[truth.ReferenceIndex];
            var referenceAspect = PhysicalEphemeris.Compute(planet, referenceTime);
            var truthImage = PlanetaryRender.RenderDiffracted(map, referenceAspect, reference, reader.Width, reader.Height, options.MinnaertK, pupil, options.WavelengthM, scale);
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
        });
        return command;
    }

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
            consoleHost.WriteScrollable(fit is { } f
                ? string.Create(inv, $"    limb fit, {which}: R {f.EquatorialRadius:0.000} px, k {f.LimbDarkening:0.000}, blur sigma {f.PsfSigma:0.000} px with {100 * f.HaloFraction:0} % in a wing of {f.HaloWidth:0.0} px, rms {f.RmsResidual:0.00000}")
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
    private static readonly Pupil NewtonianPupil = new Pupil(0.254, ObstructionRatio: 58.0 / 254, Vanes: 4, VaneWidthM: 0.001);
    private static readonly Pupil MaksutovPupil = new Pupil(MaksutovApertureM, ObstructionRatio: 0.3);

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

        var command = new Command("planetary-render-truth",
            "Render a planet's global map at an instant and a disk's geometry, through the telescope's pupil: the truth the limb fit and the restoration are measured against (T1).")
        {
            Options = { mapOpt, utcOpt, planetOpt, likeOpt, centerOpt, radiusOpt, northOpt, sizeOpt, mirroredOpt, kOpt, telescopeOpt, wavelengthOpt, seeingFwhmOpt, seeingBetaOpt, fitOpt, upsampleOpt, outputOpt },
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
                "none" => PlanetaryRender.Render(map, aspect, placement, width, height, k),
                "maksutov" => PlanetaryRender.RenderDiffracted(map, aspect, placement, width, height, k, MaksutovPupil, wavelength, scale),
                _ => PlanetaryRender.RenderDiffracted(map, aspect, placement, width, height, k, NewtonianPupil, wavelength, scale),
            };
            var fwhm = parseResult.GetValue(seeingFwhmOpt);
            var seen = fwhm is { } f ? PsfKernel.Moffat(f, parseResult.GetValue(seeingBetaOpt)).Convolve(truth, width, height) : truth;

            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"{planet} at {utc:yyyy-MM-dd HH:mm:ss} UTC: CM III {aspect.CentralMeridianIII:0.00}, sub-observer latitude {aspect.SubObserverLatitude:0.00}, phase {aspect.PhaseAngle:0.00}; " +
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

    internal static DateTimeOffset? MidCapture(IPlanetaryFrameStream stream)
        => stream.HasTimestamps && stream.TimestampOf(0) is { } first && stream.TimestampOf(stream.FrameCount - 1) is { } last
            ? first + ((last - first) / 2)
            : null;

    internal static DateTimeOffset? ParseUtc(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
