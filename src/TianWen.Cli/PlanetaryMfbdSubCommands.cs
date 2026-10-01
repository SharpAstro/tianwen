using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Stat;
using SharpAstro.Ser;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// The product's two ends of a multi-frame blind deconvolution run as a reference (docs/plans/planetary-restoration.md, R8 follow-up 4
/// part 3, #1140): <c>planetary-lucky-frames</c> writes the frames a stack keeps, registered onto it and normalised, as a FITS cube beside
/// the stack and the truth in one window; <c>planetary-score</c> scores what comes back against that truth, and a mean PSF against the
/// oracle. The deconvolution between them is glue around an outside tool; every number read is read here.
/// </summary>
internal sealed class PlanetaryMfbdSubCommands(IConsoleHost consoleHost)
{
    private const int Bands = 4;
    private static readonly double[] Frequencies = [0.1, 0.2, 0.3, 0.4, 0.45];

    public Command BuildLuckyFrames()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var outputOpt = new Option<string>("--output", "-o") { Description = "The stem the cube, the stack and the truth are written at.", Required = true };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits), written in the same window." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames kept, by the gradient, as the stack keeps them.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px (a power of two).", DefaultValueFactory = _ => 128 };

        var command = new Command("planetary-lucky-frames",
            "The frames a stack keeps, registered onto it and normalised, as a FITS cube beside the stack and the truth in one window: the input to a multi-frame deconvolution run as a reference (R8 follow-up 4 part 3, #1140).")
        {
            Arguments = { inputArg },
            Options = { outputOpt, truthOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var stem = parseResult.GetValue(outputOpt) ?? "";
            var size = parseResult.GetValue(windowOpt);
            var keep = parseResult.GetValue(keepOpt);
            var frames = parseResult.GetValue(framesOpt);
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            using var prepared = await PlanetaryWindowedStack.CreateAsync(consoleHost, input, parseResult.GetValue(truthOpt), planet, frames, keep,
                parseResult.GetValue(telescopeOpt) ?? "newtonian", parseResult.GetValue(wavelengthOpt), size, halves: false, ct);
            if (prepared is null)
            {
                return 1;
            }

            // The frames the stacker keeps: graded by the same estimator and selected by the same rule.
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            using var stream = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, frames ?? whole.FrameCount));
            var grades = await new FrameGrader(new GradientEnergyEstimator()).GradeAllAsync(stream, cancellationToken: ct);
            var selected = FrameGrader.SelectBest(grades, keep);

            // Each one normalised to its own disk, cut about the stack's own disk, and moved onto the stack's window by a plain, climbed
            // correlation (which also carries it onto a twin's truth, where the stack was).
            var registrar = new CorrelationRegistrar(prepared.Stack, size);
            var (x0, y0) = ((int)Math.Round(prepared.Own.X) - (size / 2), (int)Math.Round(prepared.Own.Y) - (size / 2));
            var cube = new float[selected.Length][,];
            var shifts = new List<double>();
            for (var k = 0; k < selected.Length; k++)
            {
                var image = await stream.LoadAsync(selected[k], ct);
                try
                {
                    var normalised = PlanetaryMetrics.Normalise(image.GetChannelSpan(0), prepared.Width, prepared.Height, prepared.Own);
                    var (moved, dx, dy) = registrar.Register(CorrelationRegistrar.Crop(normalised, prepared.Width, prepared.Height, x0, y0, size));
                    cube[k] = ToPlane(moved, size);
                    shifts.Add(Math.Sqrt((dx * dx) + (dy * dy)));
                }
                finally
                {
                    image.Release();
                }
            }

            var header = Header(prepared, keep);
            new Image(cube, BitDepth.Float32, float.NaN, float.NaN, 0f, new ImageMeta { SensorType = SensorType.Monochrome })
                .WriteToFitsFile(stem + ".frames.fits", null, header);
            Image.FromChannel(ToPlane(prepared.Stack, size)).WriteToFitsFile(stem + ".stack.fits", null, header);
            if (prepared.Truth is { } truth)
            {
                Image.FromChannel(ToPlane(truth, size)).WriteToFitsFile(stem + ".truth.fits", null, header);
            }
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {selected.Length} of {grades.Length} frames by the gradient, moved {shifts.Average():0.00} px on average (at most {shifts.Max():0.00}) onto the stack; wrote {stem}.frames.fits, .stack.fits{(prepared.Truth is null ? "" : ", .truth.fits")} ({size} px, the disk at {prepared.Disk.X:0.00}, {prepared.Disk.Y:0.00}, R {prepared.Disk.Radius:0.00} px; {prepared.ArcsecPerPixel:0.0000}\"/px)"));
            return 0;
        });
        return command;
    }

    public Command BuildScore()
    {
        var restoredArg = new Argument<string>("restored") { Description = "A restoration in the window planetary-lucky-frames wrote (its first plane)." };
        var truthOpt = new Option<string?>("--truth") { Description = "The window's truth (planetary-lucky-frames' .truth.fits): the scores; without it, only the PSFs are read." };
        var stackOpt = new Option<string?>("--stack") { Description = "The window's stack (.stack.fits): scored beside it, and the oracle a PSF is read against." };
        var psfOpt = new Option<string?>("--psf") { Description = "A cube of PSFs, one a frame (any centring): their mean's transfer over the pupil's diffraction, against the oracle." };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil the PSF's transfer is over.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };

        var command = new Command("planetary-score",
            "A restoration scored against the truth in the window planetary-lucky-frames wrote: R8's band errors inside 0.9 radii, and a mean PSF's transfer against the oracle (R8 follow-up 4 part 3, #1140).")
        {
            Arguments = { restoredArg },
            Options = { truthOpt, stackOpt, psfOpt, telescopeOpt, wavelengthOpt },
        };

        command.SetAction((parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var restoredPath = parseResult.GetValue(restoredArg) ?? "";
            var (truthPath, stackPath) = (parseResult.GetValue(truthOpt), parseResult.GetValue(stackOpt));
            // The window's geometry from whichever of the two was written beside the frames; a real capture has no truth.
            if ((truthPath ?? stackPath) is not { } windowPath || PlanetaryMeasureSubCommand.ReadTruth(windowPath, consoleHost) is not { } window
                || !Image.TryReadFitsFile(restoredPath, out var restoredImage))
            {
                consoleHost.WriteError($"{restoredPath}: not readable beside the window's truth or stack");
                return System.Threading.Tasks.Task.FromResult(1);
            }
            var size = restoredImage.Width;
            var disk = window.Disk;
            var truth = truthPath is not null ? window.Plane : null;
            var registrar = new CorrelationRegistrar(window.Plane, size);
            string Row(Func<double, double> transfer) => string.Join(" ", Frequencies.Select(f => transfer(f).ToString("0.000", inv)));
            float[] Onto(float[] plane) => registrar.Register(PlanetaryMetrics.Normalise(plane, size, size, disk)).Moved;
            void Score(string name, float[] plane, float[] reference)
            {
                var (moved, dx, dy) = registrar.Register(PlanetaryMetrics.Normalise(plane, size, size, disk));
                var bands = PlanetaryMetrics.Fidelity(moved, reference, size, size, disk, Bands);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    {name} (moved {dx:+0.00;-0.00}, {dy:+0.00;-0.00} px onto the truth): transfer {string.Join(", ", bands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} (sum {bands.Sum(b => b.Error):0.000}); undershoot {PlanetaryMetrics.LimbUndershoot(moved, size, size, disk):0.0000}"));
            }

            consoleHost.WriteScrollable(string.Create(inv, $"{Path.GetFileName(restoredPath)}: {size} px, the disk at {disk.X:0.00}, {disk.Y:0.00}, R {disk.Radius:0.00} px"));
            float[]? stack = null;
            if (stackPath is not null && Image.TryReadFitsFile(stackPath, out var stackImage))
            {
                stack = stackImage.GetChannelSpan(0).ToArray();
            }
            if (truth is not null)
            {
                if (stack is not null)
                {
                    Score("the stack", stack, truth);
                }
                Score("the restoration", restoredImage.GetChannelSpan(0).ToArray(), truth);
            }

            if (parseResult.GetValue(psfOpt) is { } psfPath && Image.TryReadFitsFile(psfPath, out var psfs))
            {
                var pupil = (parseResult.GetValue(telescopeOpt) ?? "newtonian").ToLowerInvariant() == "maksutov" ? PlanetaryGeometrySubCommands.MaksutovPupil : PlanetaryGeometrySubCommands.NewtonianPupil;
                var scale = ReadScale(windowPath);
                var diffraction = PlanetaryInverse.Diffraction(pupil, parseResult.GetValue(wavelengthOpt) * 1e-9, scale);
                var mean = MeanTransfer(psfs);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the PSFs' mean over the diffraction ({psfs.ChannelCount} PSFs of {psfs.Width} px; {scale:0.0000}\"/px): {Row(f => diffraction.At(f) is var d && d > 0.02 ? mean.At(f) / d : double.NaN)}"));
                if (stack is not null && truth is not null)
                {
                    consoleHost.WriteScrollable($"    the oracle (the stack against its truth):     {Row(PlanetaryInverse.Measure(Onto(stack), truth, size, size).At)}");
                }
            }
            return System.Threading.Tasks.Task.FromResult(0);
        });
        return command;
    }

    // The window's geometry and the run's, in the header the truth reader and the glue read back.
    private static Dictionary<string, (object Value, string Comment)> Header(PlanetaryWindowedStack prepared, double keep) => new()
    {
        ["DISKX"] = (prepared.Disk.X, "disk centre x in the window, px (0-based)"),
        ["DISKY"] = (prepared.Disk.Y, "disk centre y in the window, px (0-based)"),
        ["DISKR"] = (prepared.Disk.Radius, "equatorial radius, px"),
        ["NORTHANG"] = (prepared.Disk.AxisAngleDeg, "the disk's axis, deg from +x toward +y"),
        ["AXISRAT"] = (prepared.Disk.AxisRatio, "the disk's polar over equatorial radius, as scored"),
        ["PIXSCALE"] = (prepared.ArcsecPerPixel, "arcsec per pixel"),
        ["KEEP"] = (keep, "share of the frames kept, by the gradient"),
        ["DATE-OBS"] = (prepared.When.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), "the frames' middle, UTC"),
    };

    private static double ReadScale(string truthPath)
    {
        using var fits = Image.OpenFitsHeader(truthPath);
        return fits.ReadFirstImageHduHeaderOnly()?.Header.GetDoubleValue("PIXSCALE", double.NaN) ?? double.NaN;
    }

    // The ring-averaged magnitude of the PSFs' mean transform, one at zero frequency: the transfer the frames' kernel has, wherever each
    // PSF sits on its grid (a magnitude does not see a shift common to all; their spread is part of the kernel).
    private static RadialTransfer MeanTransfer(Image psfs)
    {
        var n = psfs.Width;
        var field = new Complex[n * n];
        for (var c = 0; c < psfs.ChannelCount; c++)
        {
            var plane = psfs.GetChannelSpan(c);
            double total = 0;
            foreach (var v in plane)
            {
                total += v;
            }
            for (var i = 0; i < field.Length; i++)
            {
                field[i] += plane[i] / total;
            }
        }
        Fft2D.Forward(field, n, n);
        var rings = (n / 2) + 1;
        var (sum, count) = (new double[rings], new int[rings]);
        for (var ky = 0; ky < n; ky++)
        {
            var sy = ky < n / 2 ? ky : ky - n;
            for (var kx = 0; kx < n; kx++)
            {
                var sx = kx < n / 2 ? kx : kx - n;
                var ring = (int)Math.Round(Math.Sqrt((sx * sx) + (sy * sy)));
                if (ring < rings)
                {
                    sum[ring] += field[(ky * n) + kx].Magnitude;
                    count[ring]++;
                }
            }
        }
        var zero = sum[0];
        return new RadialTransfer([.. Enumerable.Range(0, rings).Select(r => count[r] > 0 && zero > 0 ? sum[r] / count[r] / zero : 0)], n);
    }

    private static float[,] ToPlane(ReadOnlySpan<float> flat, int size)
    {
        var plane = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y, x] = flat[(y * size) + x];
            }
        }
        return plane;
    }
}
