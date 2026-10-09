using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Console.Lib;
using SharpAstro.Ser;

namespace TianWen.Cli;

/// <summary>
/// <c>planetary blur</c> (docs/plans/planetary-restoration.md, R7 part 2): a capture's blur read by two probes. (a), the lucky
/// frames against the stack: the frames graded by the gradient, the best 1 % split by rank into two halves, the frames ranked 1 to
/// 5 % the stack, and the stack's transfer over the lucky frames' in each band from their cross terms
/// (<see cref="PlanetaryMetrics.CrossTransfer"/>). (b), the limb fit on the stack, its core and its halo, read as band transfers
/// by blurring the object with its kernel (<see cref="PlanetaryBlurProbes"/>). With a synthetic capture's truth, both are set
/// against the stack's true transfer.
/// </summary>
internal sealed class PlanetaryBlurSubCommand(IConsoleHost consoleHost)
{
    public Command Build()
    {
        var inputArg = new Argument<string>("capture") { Description = "A SER capture of a planet." };
        var truthOpt = new Option<string?>("--truth") { Description = "A synthetic capture's truth (planetary degrade's .truth.fits): the stack's true transfer." };
        var planetOpt = new Option<string>("--planet") { Description = "jupiter or saturn.", DefaultValueFactory = _ => "jupiter" };
        var planeOpt = new Option<string?>("--plane") { Description = "A colour capture's photosite colour: r, g, g2 or b." };
        var framesOpt = new Option<int?>("--frames") { Description = "Only the first frames." };
        var luckyOpt = new Option<double>("--lucky") { Description = "The share of the frames that are lucky, split in two halves.", DefaultValueFactory = _ => 0.01 };
        var keepOpt = new Option<double>("--keep") { Description = "The share of the frames down to which the stack goes, from the lucky ones' end.", DefaultValueFactory = _ => 0.05 };
        var mapOpt = new Option<string?>("--map") { Description = "The truth's global map: rendered again without the telescope's diffraction, so the stack's TOTAL true transfer and the diffraction's own are read too (the truth is rendered through the diffraction limit, the limb's kernels against a sharp disk)." };
        var kOpt = new Option<double>("--k") { Description = "Minnaert's exponent the truth was rendered with.", DefaultValueFactory = _ => 0.999 };

        var command = new Command("blur",
            "A capture's blur by two probes: the stack's band transfer over its lucky frames' (a), and the limb fit's core and halo read as band transfers (b), each set against the truth when a synthetic capture's is given (R7 part 2).")
        {
            Arguments = { inputArg },
            Options = { truthOpt, planetOpt, planeOpt, framesOpt, luckyOpt, keepOpt, mapOpt, kOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var inv = CultureInfo.InvariantCulture;
            var input = parseResult.GetValue(inputArg) ?? "";
            var planet = parseResult.GetValue(planetOpt)?.ToLowerInvariant() == "saturn" ? CatalogIndex.Saturn : CatalogIndex.Jupiter;
            using var reader = SerReader.Open(input);
            using var whole = new SerFrameStream(reader, ownsReader: false);
            using var window = new PlanetaryFrameWindow(whole, 0, Math.Min(whole.FrameCount, parseResult.GetValue(framesOpt) ?? whole.FrameCount));
            var plane = PlanetaryGeometrySubCommands.ParsePlane(parseResult.GetValue(planeOpt));
            if (window.Layout == PlanetaryFrameLayout.SplitCfa && plane is null)
            {
                consoleHost.WriteError($"{input}: a colour capture; pass --plane r, g, g2 or b");
                return 1;
            }
            using var planeStream = plane is { } channel && window.Layout == PlanetaryFrameLayout.SplitCfa ? new CfaPlaneStream(window, channel) : null;
            IPlanetaryFrameStream stream = planeStream is null ? window : planeStream;
            if (stream.MidCapture is not { } when)
            {
                consoleHost.WriteError($"{input}: no timestamps");
                return 1;
            }
            var limbOptions = PlanetaryLimbFit.OptionsFor(PhysicalEphemeris.Compute(planet, when));

            // The frames ranked by the gradient (R4), best first.
            var estimator = new GradientEnergyEstimator();
            var grades = ImmutableArray.CreateBuilder<FrameGrade>(stream.FrameCount);
            for (var i = 0; i < stream.FrameCount; i++)
            {
                var frame = await stream.LoadAsync(i, ct);
                try
                {
                    grades.Add(new FrameGrade(i, FrameGrader.Grade(estimator, frame)));
                }
                finally
                {
                    frame.Release();
                }
            }
            var ranked = FrameGrader.SortByQuality(grades.MoveToImmutable()).Select(g => g.Index).ToArray();
            var luckyCount = Math.Max(4, (int)Math.Round(parseResult.GetValue(luckyOpt) * ranked.Length));
            var stackEnd = Math.Max(luckyCount + 4, (int)Math.Round(parseResult.GetValue(keepOpt) * ranked.Length));
            var half1 = ranked.Take(luckyCount).Where((_, i) => i % 2 == 0).ToImmutableArray();
            var half2 = ranked.Take(luckyCount).Where((_, i) => i % 2 == 1).ToImmutableArray();
            var rest = ranked.Skip(luckyCount).Take(stackEnd - luckyCount).ToImmutableArray();
            consoleHost.WriteScrollable(string.Create(inv,
                $"{Path.GetFileName(input)}: {stream.FrameCount} frames by the gradient; the lucky {luckyCount} ({half1.Length} and {half2.Length}), the stack of the next {rest.Length}"));

            var options = new PlanetaryStackOptions { KeepFraction = 1, WhitenedCorrelation = false, Interpolation = WarpInterpolation.Lanczos3, QualityEstimator = estimator };
            var stacker = new LuckyImagingStacker();
            async Task<Image> StackOf(ImmutableArray<int> indices)
            {
                using var subset = new PlanetaryFrameSubset(stream, indices);
                return (await stacker.StackGlobalAsync(subset, options, ct)).Master;
            }
            var stackImage = await StackOf(rest);
            var luckyImage1 = await StackOf(half1);
            var luckyImage2 = await StackOf(half2);
            try
            {
                var (width, height) = (stackImage.Width, stackImage.Height);
                var truth = parseResult.GetValue(truthOpt) is { } truthPath ? PlanetaryMeasureSubCommand.ReadTruth(truthPath, consoleHost) : null;
                if (parseResult.GetValue(truthOpt) is not null && truth is null)
                {
                    return 1;
                }
                MetricDisk? onto = truth is { } t ? t.Disk with { AxisRatio = limbOptions.AxisRatio } : null;
                if (PlanetaryMeasureSubCommand.Register(stackImage, limbOptions, onto) is not { } stack
                    || PlanetaryMeasureSubCommand.Register(luckyImage1, limbOptions, stack.Disk) is not { } lucky1
                    || PlanetaryMeasureSubCommand.Register(luckyImage2, limbOptions, stack.Disk) is not { } lucky2
                    || PlanetaryLimbFit.Fit(stackImage, limbOptions) is not { } fit)
                {
                    consoleHost.WriteError($"{input}: a stack's limb could not be fitted");
                    return 1;
                }
                var disk = stack.Disk;
                float[]? truthPlane = truth is { } tr ? PlanetaryMetrics.Normalise(tr.Plane, width, height, disk) : null;
                // The object the kernels' band transfers are read through: the truth where there is one, else the stack itself.
                var objectPlane = truthPlane ?? stack.Plane;

                var probeA = PlanetaryMetrics.CrossTransfer(stack.Plane, lucky1.Plane, lucky2.Plane, width, height, disk);
                var probeB = PlanetaryMetrics.Fidelity(PlanetaryBlurProbes.BlurByLimbKernel(objectPlane, width, height, fit), objectPlane, width, height, disk).Select(f => f.Transfer).ToImmutableArray();
                var sigmaA = PlanetaryBlurProbes.EquivalentGaussianSigma(probeA, objectPlane, width, height, disk);
                var sigmaB = PlanetaryBlurProbes.EquivalentGaussianSigma(probeB, objectPlane, width, height, disk);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    the limb fit on the stack: core sigma {fit.PsfSigma:0.00} px, halo {fit.HaloFraction:P1} of sigma {fit.HaloWidth:0.00} px"));
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    widths, the Gaussian each kernel's band transfers fit: (a) {sigmaA:0.000} px, (b) {sigmaB:0.000} px, (a) over (b) {sigmaA / sigmaB:0.000}"));
                // (b'), part 3: the limb fit's geometry kept, its kernel refitted with a core convolved with the scatter's wing.
                if (PlanetaryLimbKernel.Fit(stackImage, fit, limbOptions) is not { } wide)
                {
                    consoleHost.WriteError($"{input}: the limb's widened kernel could not be fitted");
                    return 1;
                }
                var probeWide = PlanetaryMetrics.Fidelity(PlanetaryLimbKernel.Blur(objectPlane, width, height, wide with { Brightness = 1, Sky = 0 }), objectPlane, width, height, disk)
                    .Select(f => f.Transfer).ToImmutableArray();
                var sigmaWide = PlanetaryBlurProbes.EquivalentGaussianSigma(probeWide, objectPlane, width, height, disk);
                consoleHost.WriteScrollable(string.Create(inv,
                    $"    (b') the limb's kernel with the scatter's wing: core sigma {wide.CoreSigma:0.000} px convolved with {wide.WingFraction:P1} in a wing of a {wide.WingScale:0.00} px; width {sigmaWide:0.000} px, (a) over it {sigmaA / sigmaWide:0.000}"));

                ImmutableArray<double> truthTransfer = [], luckyTransfer = [];
                if (truthPlane is not null)
                {
                    var luckyMean = new float[lucky1.Plane.Length];
                    for (var i = 0; i < luckyMean.Length; i++)
                    {
                        luckyMean[i] = (lucky1.Plane[i] + lucky2.Plane[i]) / 2;
                    }
                    truthTransfer = [.. PlanetaryMetrics.Fidelity(stack.Plane, truthPlane, width, height, disk).Select(f => f.Transfer)];
                    luckyTransfer = [.. PlanetaryMetrics.Fidelity(luckyMean, truthPlane, width, height, disk).Select(f => f.Transfer)];
                    var sigmaTrue = PlanetaryBlurProbes.EquivalentGaussianSigma(truthTransfer, truthPlane, width, height, disk);
                    consoleHost.WriteScrollable(string.Create(inv, $"    the stack's true kernel's width {sigmaTrue:0.000} px"));
                    // The same scene without the telescope's diffraction: what the limb's kernels, fitted against a sharp disk, read.
                    if (parseResult.GetValue(mapOpt) is { } mapPath && truth is { } read && read.Time is { } truthTime)
                    {
                        if (PlanetMap.ReadFits(mapPath) is not { } map)
                        {
                            consoleHost.WriteError($"{mapPath}: no map");
                            return 1;
                        }
                        var aspect = PhysicalEphemeris.Compute(planet, truthTime);
                        var placement = new DiskPlacement(read.Disk.X, read.Disk.Y, read.Disk.Radius, read.Disk.AxisAngleDeg);
                        var geometric = PlanetaryMetrics.Normalise(PlanetaryRender.Render(map, aspect, placement, width, height, parseResult.GetValue(kOpt), supersample: 4), width, height, disk);
                        var total = PlanetaryMetrics.Fidelity(stack.Plane, geometric, width, height, disk).Select(f => f.Transfer).ToArray();
                        var diffraction = PlanetaryMetrics.Fidelity(truthPlane, geometric, width, height, disk).Select(f => f.Transfer).ToArray();
                        consoleHost.WriteScrollable("    against the scene without diffraction: band, the stack's total true, the diffraction's own, (b) over total, (b') over total");
                        for (var b = 0; b < probeA.Length; b++)
                        {
                            consoleHost.WriteScrollable(string.Create(inv,
                                $"      {b + 1,4}   {total[b],7:0.0000}   {diffraction[b],7:0.0000}   {probeB[b] / total[b],7:0.000}   {probeWide[b] / total[b],7:0.000}"));
                        }
                    }
                }
                consoleHost.WriteScrollable(truthPlane is null
                    ? "    band   (a) stack over lucky   (b) limb kernel   (b') widened"
                    : "    band   (a) stack over lucky   (b) limb kernel   (b') widened   true   (b) over true   (b') over true   lucky's own   lucky's own x (a)   over true");
                for (var b = 0; b < probeA.Length; b++)
                {
                    consoleHost.WriteScrollable(truthPlane is null
                        ? string.Create(inv, $"    {b + 1,4}   {probeA[b],20:0.0000}   {probeB[b],15:0.0000}   {probeWide[b],12:0.0000}")
                        : string.Create(inv,
                            $"    {b + 1,4}   {probeA[b],20:0.0000}   {probeB[b],15:0.0000}   {probeWide[b],12:0.0000}   {truthTransfer[b],4:0.0000}   {probeB[b] / truthTransfer[b],12:0.000}   {probeWide[b] / truthTransfer[b],13:0.000}   {luckyTransfer[b],11:0.0000}   {luckyTransfer[b] * probeA[b],17:0.0000}   {luckyTransfer[b] * probeA[b] / truthTransfer[b],9:0.000}"));
                }
                return 0;
            }
            finally
            {
                stackImage.Release();
                luckyImage1.Release();
                luckyImage2.Release();
            }
        });
        return command;
    }
}
