using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary-elongated</c> (docs/plans/planetary-restoration.md, R8 follow-up 4 part 2, #1140): an elongated kernel read off the limb's
/// edge in two sectors, (e), the physical kernel along the planet's axis times a jitter along its equator; against a twin's oracle read in
/// the same two sectors and interpolated between them; and Richardson-Lucy with the round physical kernel (a), (e), the round oracle and
/// the 2-D oracle, each set to band 3 = 1.00 against the truth and scored on the bands.
/// </summary>
internal sealed class PlanetaryElongatedSubCommand(IConsoleHost consoleHost)
{
    private const int Bands = 4;
    private static readonly double[] Frequencies = [0.1, 0.2, 0.3, 0.4, 0.45];

    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary-degrade's .truth.fits): the oracle, the restorations and the claims." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames stacked, by the gradient.", DefaultValueFactory = _ => 0.05 };
        var telescopeOpt = new Option<string>("--telescope") { Description = "newtonian or maksutov: the pupil every transfer here is over.", DefaultValueFactory = _ => "newtonian" };
        var wavelengthOpt = new Option<double>("--wavelength") { Description = "The filter's effective wavelength, nm.", DefaultValueFactory = _ => 650 };
        var windowOpt = new Option<int>("--window") { Description = "The side of the window about the disk, px.", DefaultValueFactory = _ => 256 };
        var maxStepsOpt = new Option<int>("--max-steps") { Description = "Richardson-Lucy's most steps.", DefaultValueFactory = _ => 200 };

        var command = new Command("planetary-elongated",
            "An elongated kernel off the limb's edge in two sectors, against the oracle read in the same two, and Richardson-Lucy with it, the round kernel and both oracles (R8 follow-up 4 part 2, #1140).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, framesOpt, keepOpt, telescopeOpt, wavelengthOpt, windowOpt, maxStepsOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            var size = parseResult.GetValue(windowOpt);
            using var prepared = await PlanetaryWindowedStack.CreateAsync(consoleHost, input, parseResult.GetValue(truthOpt), planet, parseResult.GetValue(framesOpt),
                parseResult.GetValue(keepOpt), parseResult.GetValue(telescopeOpt) ?? "newtonian", parseResult.GetValue(wavelengthOpt), size, halves: false, ct);
            if (prepared is null)
            {
                return 1;
            }
            var (disk, stack) = (prepared.Disk, prepared.Stack);
            string Row(Func<double, double> transfer) => string.Join(" ", Frequencies.Select(f => transfer(f).ToString("0.000", inv)));

            // The planet's axis in the image (modulo 180) and its equator a quarter turn away: the sectors the edge and the oracle are read in.
            var axisDeg = ((prepared.Fit.NorthAngleDeg % 180) + 180) % 180;
            var equatorDeg = axisDeg + 90;
            double Along(double deg, double f) => f * Math.Cos(deg * Math.PI / 180);
            double Across(double deg, double f) => f * Math.Sin(deg * Math.PI / 180);
            Func<double, double> AlongAxis(Func<double, double, double> twoD) => f => twoD(Along(axisDeg, f), Across(axisDeg, f));
            Func<double, double> AlongEquator(Func<double, double, double> twoD) => f => twoD(Along(equatorDeg, f), Across(equatorDeg, f));

            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {prepared.Result.FramesUsed} of {prepared.Result.FramesGraded} frames by the gradient; disk R {prepared.Fit.EquatorialRadius:0.00} px; the axis at {axisDeg:0.0} deg, the equator at {equatorDeg % 180:0.0}; transfers over the pupil's diffraction at {string.Join(", ", Frequencies.Select(f => f.ToString("0.0#", inv)))} cycles a pixel"));

            // (a) the round physical kernel on the whole edge (step 3 part 2's), and (e) the elongated one off the two sectors.
            var edge = prepared.LimbEdge(stack);
            var round = PlanetaryFinestBand.FitPhysical(edge, prepared.Cutoff);
            var (edgeAxis, edgeEquator) = (prepared.LimbEdge(stack, axisDeg), prepared.LimbEdge(stack, equatorDeg));
            var elongated = PlanetaryFinestBand.FitElongated(edgeAxis, edgeEquator, prepared.Cutoff, equatorDeg);
            consoleHost.WriteScrollable($"    the edge along the axis:              {Row(edgeAxis.TransferAt)} ({edgeAxis.Counts.Sum()} pixels)");
            consoleHost.WriteScrollable($"    the edge along the equator:           {Row(edgeEquator.TransferAt)} ({edgeEquator.Counts.Sum()} pixels)");
            consoleHost.WriteScrollable($"    (a), round, on the whole edge:        {Row(round.TransferAt)}");
            consoleHost.WriteScrollable(string.Create(inv,
                $"    (e) along the axis:                   {Row(AlongAxis(elongated.TransferAt))} (D/r0 {elongated.Round.ApertureOverR0:0.00}, sigma {elongated.Round.SigmaPx:0.00} px, halo {elongated.Round.Halo:0.000} of {elongated.Round.HaloWidthPx:0.0} px)"));
            consoleHost.WriteScrollable(string.Create(inv,
                $"    (e) along the equator:                {Row(AlongEquator(elongated.TransferAt))} (a jitter of {elongated.SigmaPx:0.000} px)"));
            var ratioE = AlongEquator(elongated.TransferAt)(0.2) / AlongAxis(elongated.TransferAt)(0.2);

            if (prepared.Truth is not { } truth)
            {
                consoleHost.WriteScrollable(string.Create(inv, $"    (e)'s equator over its axis at 0.2 cycles a pixel: {ratioE:0.000}"));
                return 0;
            }

            // The oracle in every direction, along the axis and along the equator, and interpolated between those two as a jitter would be.
            var oracle = PlanetaryInverse.Measure(stack, truth, size, size);
            var (oracleAxis, oracleEquator) = (PlanetaryInverse.Measure(stack, truth, size, size, sectorDeg: axisDeg), PlanetaryInverse.Measure(stack, truth, size, size, sectorDeg: equatorDeg));
            var oracle2D = PlanetaryFinestBand.TwoDirections(oracleAxis.At, oracleEquator.At, axisDeg);
            consoleHost.WriteScrollable($"    the oracle, every direction:          {Row(oracle.At)}");
            consoleHost.WriteScrollable($"    the oracle along the axis:            {Row(oracleAxis.At)}");
            consoleHost.WriteScrollable($"    the oracle along the equator:         {Row(oracleEquator.At)}");
            var ratioOracle = oracleEquator.At(0.2) / oracleAxis.At(0.2);
            consoleHost.WriteScrollable(string.Create(inv,
                $"    claim 1, the equator over the axis at 0.2: (e) {ratioE:0.000}, the oracle {ratioOracle:0.000} ({(Math.Abs(ratioE - ratioOracle) <= 0.05 ? "holds" : "FAILS")}, {Math.Abs(ratioE - ratioOracle):0.000} apart)"));

            // Richardson-Lucy with each kernel, set to band 3 = 1.00 against the truth, scored on bands 1 to 4.
            var stackBands = PlanetaryMetrics.Fidelity(stack, truth, size, size, disk, Bands);
            consoleHost.WriteScrollable(string.Create(inv,
                $"    the stack: transfer {string.Join(", ", stackBands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", stackBands.Select(b => b.Error.ToString("0.000", inv)))} (sum {stackBands.Sum(b => b.Error):0.000})"));
            var errors = new Dictionary<string, double>();
            var maxSteps = parseResult.GetValue(maxStepsOpt);
            foreach (var (name, kernel) in new (string, Func<double, double, double>)[]
            {
                ("(a), round", (fx, fy) => round.TransferAt(Math.Sqrt((fx * fx) + (fy * fy)))),
                ("(e), elongated", elongated.TransferAt),
                ("the oracle, round", (fx, fy) => oracle.At(Math.Sqrt((fx * fx) + (fy * fy)))),
                ("the 2-D oracle", oracle2D),
            })
            {
                var (steps, restored) = RichardsonLucyTo(stack, size, kernel, maxSteps, p => PlanetaryMetrics.Fidelity(p, truth, size, size, disk, Bands)[2].Transfer, 1.0);
                var bands = PlanetaryMetrics.Fidelity(restored, truth, size, size, disk, Bands);
                errors[name] = bands.Sum(b => b.Error);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    Richardson-Lucy, {name} ({steps} steps): transfer {string.Join(", ", bands.Select(b => b.Transfer.ToString("0.000", inv)))}; error {string.Join(", ", bands.Select(b => b.Error.ToString("0.000", inv)))} (sum {errors[name]:0.000})"));
            }

            // The kill line, then claim 3.
            var oracleGain = errors["the oracle, round"] - errors["the 2-D oracle"];
            var share = oracleGain / errors["the oracle, round"];
            var measuredGain = errors["(a), round"] - errors["(e), elongated"];
            consoleHost.WriteScrollable(string.Create(inv,
                $"    claim 2, the kill line: the 2-D oracle leaves {share:P1} less than the round one ({(share >= 0.03 ? "past it" : "FIRES, (e) is not judged")})"));
            if (share >= 0.03)
            {
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    claim 3: (e) gains {measuredGain:0.000} over (a), the 2-D oracle {oracleGain:0.000} over the round one ({(measuredGain >= 0.5 * oracleGain ? "holds" : "FAILS")}, {measuredGain / oracleGain:P0} of it)"));
            }
            return 0;
        });
        return command;
    }

    // Richardson-Lucy at the sky (lifted a thousandth only so a division is defined) with a 2-D kernel, stopped at the first step whose band 3
    // reaches the target, or the last (R8 part 2's rule).
    private static (int Steps, float[] Plane) RichardsonLucyTo(float[] plane, int size, Func<double, double, double> transfer, int maxSteps, Func<float[], double> band3, double target)
    {
        float[]? reached = null;
        var (at, last) = (maxSteps, plane);
        PlanetaryInverse.RichardsonLucy(plane, size, size, transfer, maxSteps, (step, p) =>
        {
            last = p;
            if (reached is null && band3(p) >= target)
            {
                (reached, at) = (p, step);
            }
        }, offset: 1e-3);
        return (at, reached ?? last);
    }
}
